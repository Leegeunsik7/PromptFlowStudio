using System;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

public enum MeshNodeHealth : byte { Healthy = 0, Degraded = 1, Down = 2 }

/// <summary>노드 1개의 링버퍼 통계 지문(값 형식).</summary>
public readonly record struct MeshNodeFingerprint(int Samples, double EwmaLatencyMs, double JitterMs, double P95LatencyMs, double EwmaBandwidthMbps, int ConsecutiveLosses, MeshNodeHealth Health, double Cost, double LossPct = 0);

/// <summary>
/// [PromptFlow Studio v12.2.0-Phase3] P2P 메시 릴레이 노드 1개의 레이턴시/대역폭 핑거프린트 — 고정 용량 링버퍼(기본 64샘플,
///   10ms 프로브 주기 기준 최근 640ms 창)에 샘플을 누적하고 EWMA·지터(평균 절대 편차)·p95 를 산출한다.
///   ▸ Zero-Alloc: 링은 생성 시 1회 할당, p95 는 stackalloc 사본을 Span.Sort(무할당) 해서 계산.
///   ▸ 스레드 모델: 샘플 기록/통계 계산은 노드별 짧은 락으로 직렬화(프로브 스레드 1 + 밸런서 재구성 1 수준의 저경합).
///     라우팅 핫패스(<see cref="P2pLoadBalancer.SelectRoute"/>)는 이 락을 전혀 잡지 않는다.
/// </summary>
public sealed class MeshRelayNode
{
    public const int DefaultRingCapacity = 64;
    private const double EwmaAlpha = 0.2;
    private const int LossesForDown = 3;
    private const double DegradedLatencyMs = 150;

    private readonly object _gate = new();
    private readonly float[] _latRing;
    private readonly float[] _bwRing;
    private int _head, _count;
    private double _ewmaLat, _ewmaBw, _jitter;
    private int _consecutiveLosses;
    private long _probes, _lossTotal;   // v12.2.0-Phase4: 누적 패킷 손실률(%) = 유실 / (성공+유실)
    private long _lastSeenTicks;
    private bool _forcedDown;

    public MeshRelayNode(string nodeId, int ringCapacity = DefaultRingCapacity)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("nodeId 필요", nameof(nodeId));
        if (ringCapacity < 4 || ringCapacity > 1024) throw new ArgumentOutOfRangeException(nameof(ringCapacity));
        NodeId = nodeId;
        _latRing = new float[ringCapacity];
        _bwRing = new float[ringCapacity];
    }

    public string NodeId { get; }
    public int RingCapacity => _latRing.Length;
    public long LastSeenTicks { get { lock (_gate) return _lastSeenTicks; } }

    /// <summary>프로브 1회 성공 샘플 기록(레이턴시 ms, 대역폭 Mbps, 관측 시각 Stopwatch ticks).</summary>
    public void RecordSample(double latencyMs, double bandwidthMbps, long timestampTicks)
    {
        if (latencyMs < 0 || double.IsNaN(latencyMs) || bandwidthMbps < 0 || double.IsNaN(bandwidthMbps)) return;   // 스키마 방어
        lock (_gate)
        {
            _latRing[_head] = (float)latencyMs;
            _bwRing[_head] = (float)bandwidthMbps;
            _head = (_head + 1) % _latRing.Length;
            if (_count < _latRing.Length) _count++;
            if (_count == 1) { _ewmaLat = latencyMs; _ewmaBw = bandwidthMbps; _jitter = 0; }
            else
            {
                _jitter = _jitter * (1 - EwmaAlpha) + Math.Abs(latencyMs - _ewmaLat) * EwmaAlpha;
                _ewmaLat = _ewmaLat * (1 - EwmaAlpha) + latencyMs * EwmaAlpha;
                _ewmaBw = _ewmaBw * (1 - EwmaAlpha) + bandwidthMbps * EwmaAlpha;
            }
            _consecutiveLosses = 0;
            _probes++;
            _lastSeenTicks = timestampTicks;
            _forcedDown = false;
        }
    }

    /// <summary>프로브 유실 1회 기록 - 연속 <see cref="LossesForDown"/>회면 Down.</summary>
    public void RecordLoss() { lock (_gate) { _consecutiveLosses++; _probes++; _lossTotal++; } }

    /// <summary>명시적 이탈 통보(피어 종료/소켓 리셋 등) - 다음 성공 샘플까지 Down 고정.</summary>
    public void MarkDown() { lock (_gate) _forcedDown = true; }

    /// <summary>현재 링 통계. <paramref name="nowTicks"/> 기준 <paramref name="staleAfterTicks"/> 이상 무응답이면 Down.</summary>
    public MeshNodeFingerprint Fingerprint(long nowTicks, long staleAfterTicks)
    {
        Span<float> scratch = stackalloc float[1024];
        lock (_gate)
        {
            var stale = _count == 0 || nowTicks - _lastSeenTicks > staleAfterTicks;
            var health = _forcedDown || stale || _consecutiveLosses >= LossesForDown ? MeshNodeHealth.Down
                : _ewmaLat > DegradedLatencyMs || _consecutiveLosses > 0 ? MeshNodeHealth.Degraded
                : MeshNodeHealth.Healthy;

            double p95 = 0;
            if (_count > 0)
            {
                var s = scratch[.._count];
                _latRing.AsSpan(0, _count).CopyTo(s);
                s.Sort();
                p95 = s[Math.Min(_count - 1, (int)Math.Ceiling(_count * 0.95) - 1)];
            }
            var cost = health == MeshNodeHealth.Down ? double.PositiveInfinity : ComputeCost(_ewmaLat, _jitter, _ewmaBw, _consecutiveLosses);
            var lossPct = _probes == 0 ? 0 : _lossTotal * 100.0 / _probes;
            return new MeshNodeFingerprint(_count, _ewmaLat, _jitter, p95, _ewmaBw, _consecutiveLosses, health, cost, lossPct);
        }
    }

    /// <summary>경로 비용(낮을수록 우선): 레이턴시 + 2×지터 + 대역폭 페널티(100/Mbps) + 유실 페널티. Down 은 +∞.</summary>
    internal static double ComputeCost(double latencyMs, double jitterMs, double bandwidthMbps, int losses)
        => latencyMs + 2 * jitterMs + 100.0 / Math.Max(bandwidthMbps, 0.1) + losses * 25.0;
}
