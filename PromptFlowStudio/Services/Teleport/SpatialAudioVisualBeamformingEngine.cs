using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace PromptFlowStudio.Services.Teleport;

public enum BeamSteerMode : byte { TrackViewer, ManualAngle }
public enum BeamEarTarget : byte { Center, Left, Right }

/// <summary>추적 시청자(α-β 필터 평활 · 귀/눈 위치 파생). 좌표: m · Y 상향 · 스피커 배열/홀로 패널은 z=0 평면에서 +Z 를 향함.</summary>
public struct TrackedViewer
{
    public int Id, Track, Updates;
    public bool Active;
    public Vector3 Head, Velocity, LeftEar, RightEar, LeftEye, RightEye;
    public float Yaw, Pitch;
    public long UpdatedMicros;
}

/// <summary>
/// [PromptFlow Studio v16.55.0] 🎯 공간 음향·시각 빔포밍 &amp; 트래커 엔진(Pure BCL · 무할당 핫패스).
///   ▸ 트래커: 시청자 머리 위치/요/피치 → α-β 필터(속도 추정 · 지연 보상 예측) → 좌/우 귀(±8.75cm) · 좌/우 눈(IPD 63mm).
///   ▸ 음향: N 소자 스피커 배열 근거리 지연-합(Delay-and-Sum) 집속 — 트랙별 빔이 지정 시청자 귀에 위상 정렬 · Hann 테이퍼 · 거리 보상 ·
///     분수 지연 선형 보간 + 블록 내 지연/이득 램프(지퍼 노이즈 제거). 수동 모드는 원거리 위상 지향 각도 조향.
///   ▸ 시각: 32×32 위상 SLM 홀로 패널 — 눈 위치 프레넬 집속 위상(λ=532nm · 배정밀 위상) · 눈별 시분할 맵 · 집속 이득 평가.
/// </summary>
public sealed class SpatialAudioVisualBeamformingEngine
{
    public const int MaxViewers = 8, MaxElements = 32, MaxBeams = 4, SampleRate = 48000, MaxBlock = 1024, HoloRes = 32;
    public const int RingLen = 4096, RingMask = RingLen - 1, MaxDelaySamples = RingLen - MaxBlock - 2;
    public const float SpeedOfSound = 343f, EarHalfSpan = 0.0875f, HalfIpd = 0.0315f, HoloPitch = 0.0005f;
    public const double HoloWavelength = 532e-9;
    private const float Alpha = 0.6f, Beta = 0.2f;

    private readonly object _gate = new();
    private readonly TrackedViewer[] _viewers = new TrackedViewer[MaxViewers];
    private readonly Vector3[] _elements = new Vector3[MaxElements];
    private readonly float[] _delay = new float[MaxBeams * MaxElements], _prevDelay = new float[MaxBeams * MaxElements];
    private readonly float[] _gain = new float[MaxBeams * MaxElements], _prevGain = new float[MaxBeams * MaxElements];
    private readonly float[] _ring = new float[MaxBeams * RingLen];
    private readonly int[] _writePos = new int[MaxBeams];
    private readonly int[] _beamViewer = new int[MaxBeams];
    private readonly bool[] _beamActive = new bool[MaxBeams];
    private readonly Vector3[] _beamFocus = new Vector3[MaxBeams];
    private readonly float[] _holoL = new float[HoloRes * HoloRes], _holoR = new float[HoloRes * HoloRes];
    private int _elementCount;
    private float _spacing, _manualDeg, _contrastDb, _steerDeg, _holoGain;
    private long _ticks;
    private double _tickMicros;

    public SpatialAudioVisualBeamformingEngine() { ConfigureArray(16, 0.06f); for (var b = 0; b < MaxBeams; b++) _beamViewer[b] = -1; }

    public event Action? OnChange;

    public Vector3 ArrayCenter { get; private set; } = new(0f, 1.2f, 0f);
    public Vector3 HoloCenter { get; set; } = new(0f, 1.4f, 0f);
    public BeamSteerMode Mode { get; set; } = BeamSteerMode.TrackViewer;
    public BeamEarTarget EarTarget { get; set; } = BeamEarTarget.Center;
    public float PredictionMs { get; set; } = 20f;
    public int ElementCount => _elementCount;
    public float ElementSpacing => _spacing;
    public float ManualSteerDeg { get => _manualDeg; set { _manualDeg = Math.Clamp(value, -80f, 80f); LastAction = "🧭 위상 지향 " + _manualDeg.ToString("0", CultureInfo.InvariantCulture) + "°"; Raise(); } }
    public int ViewersActive { get { var c = 0; for (var i = 0; i < MaxViewers; i++) if (_viewers[i].Active) c++; return c; } }
    public int BeamsActive { get { var c = 0; for (var i = 0; i < MaxBeams; i++) if (_beamActive[i]) c++; return c; } }
    public float LastContrastDb => Volatile.Read(ref _contrastDb);
    public float LastSteerDeg => Volatile.Read(ref _steerDeg);
    public float LastHoloGain => Volatile.Read(ref _holoGain);
    public long Ticks => Interlocked.Read(ref _ticks);
    public double LastTickMicros => Volatile.Read(ref _tickMicros);
    public string? LastAction { get; private set; }
    public ReadOnlySpan<float> HoloPhaseLeft => _holoL;
    public ReadOnlySpan<float> HoloPhaseRight => _holoR;

    /// <summary>선형 스피커 배열 구성(중심 ArrayCenter · x 축 정렬 · +Z 지향).</summary>
    public void ConfigureArray(int elements, float spacing)
    {
        lock (_gate)
        {
            _elementCount = Math.Clamp(elements, 2, MaxElements);
            _spacing = Math.Clamp(spacing, 0.01f, 0.5f);
            for (var i = 0; i < _elementCount; i++)
                _elements[i] = ArrayCenter + new Vector3((i - (_elementCount - 1) * 0.5f) * _spacing, 0f, 0f);
            Array.Clear(_prevDelay); Array.Clear(_prevGain);
        }
    }

    public Vector3 Element(int i) => _elements[i];

    public TrackedViewer Viewer(int slot) => _viewers[slot];

    private int Slot(int id, bool create)
    {
        var free = -1;
        for (var i = 0; i < MaxViewers; i++)
        {
            if (_viewers[i].Active && _viewers[i].Id == id) return i;
            if (free < 0 && !_viewers[i].Active) free = i;
        }
        if (!create || free < 0) return -1;
        _viewers[free] = new TrackedViewer { Id = id, Track = -1, Active = true };
        return free;
    }

    /// <summary>시청자 측정 갱신(무할당 · α-β 필터). 반환 = 슬롯(-1 = 만석).</summary>
    public int UpdateViewer(int id, Vector3 head, float yawRad, float pitchRad, long tMicros)
    {
        lock (_gate)
        {
            var s = Slot(id, true);
            if (s < 0) return -1;
            ref var v = ref _viewers[s];
            if (v.Updates == 0) { v.Head = head; v.Velocity = Vector3.Zero; v.Yaw = yawRad; v.Pitch = pitchRad; }
            else
            {
                var dt = Math.Clamp((tMicros - v.UpdatedMicros) / 1e6f, 1e-3f, 0.5f);
                var pred = v.Head + v.Velocity * dt;
                var r = head - pred;
                v.Head = pred + r * Alpha;
                v.Velocity += r * (Beta / dt);
                var dy = yawRad - v.Yaw;
                dy -= MathF.Tau * MathF.Round(dy / MathF.Tau);
                v.Yaw += dy * Alpha;
                v.Pitch += (pitchRad - v.Pitch) * Alpha;
            }
            v.UpdatedMicros = tMicros;
            v.Updates++;
            Derive(ref v);
            return s;
        }
    }

    private static void Derive(ref TrackedViewer v)
    {
        var right = new Vector3(MathF.Cos(v.Yaw), 0f, -MathF.Sin(v.Yaw));
        var fwd = new Vector3(-MathF.Sin(v.Yaw) * MathF.Cos(v.Pitch), MathF.Sin(v.Pitch), -MathF.Cos(v.Yaw) * MathF.Cos(v.Pitch));
        v.LeftEar = v.Head - right * EarHalfSpan;
        v.RightEar = v.Head + right * EarHalfSpan;
        var eyeBase = v.Head + fwd * 0.08f + new Vector3(0f, 0.03f, 0f);
        v.LeftEye = eyeBase - right * HalfIpd;
        v.RightEye = eyeBase + right * HalfIpd;
    }

    public void RemoveViewer(int id)
    {
        lock (_gate)
        {
            var s = Slot(id, false);
            if (s < 0) return;
            _viewers[s].Active = false;
            for (var b = 0; b < MaxBeams; b++) if (_beamViewer[b] == s) { _beamViewer[b] = -1; _beamActive[b] = false; }
        }
    }

    /// <summary>연출 오디오 트랙(=빔) → 시청자 지정.</summary>
    public bool AssignTrack(int viewerId, int track)
    {
        if ((uint)track >= MaxBeams) return false;
        lock (_gate)
        {
            var s = Slot(viewerId, false);
            if (s < 0) return false;
            if (_viewers[s].Track == track && _beamViewer[track] == s) return true;
            for (var b = 0; b < MaxBeams; b++) if (_beamViewer[b] == s) _beamViewer[b] = -1;
            _viewers[s].Track = track;
            _beamViewer[track] = s;
            return true;
        }
    }

    /// <summary>지연 보상 예측 위치(머리 + 속도 × ahead).</summary>
    public Vector3 Predict(int slot, float aheadMs) => _viewers[slot].Head + _viewers[slot].Velocity * (aheadMs * 0.001f);

    private Vector3 FocusOf(int slot)
    {
        ref var v = ref _viewers[slot];
        var lead = v.Velocity * (PredictionMs * 0.001f);
        return lead + EarTarget switch { BeamEarTarget.Left => v.LeftEar, BeamEarTarget.Right => v.RightEar, _ => v.Head };
    }

    /// <summary>근거리 집속 빔(무할당) — 지연 τ_i = (d_max − d_i)/c · 이득 = Hann × 거리 보상(정규화 Σ=1).</summary>
    public void FocusBeam(int beam, Vector3 focus)
    {
        var o = beam * MaxElements;
        var n = _elementCount;
        float dmax = 0, gsum = 0;
        for (var i = 0; i < n; i++) dmax = MathF.Max(dmax, Vector3.Distance(focus, _elements[i]));
        for (var i = 0; i < n; i++)
        {
            var d = Vector3.Distance(focus, _elements[i]);
            _delay[o + i] = MathF.Min(MaxDelaySamples, (dmax - d) / SpeedOfSound * SampleRate);
            var w = 0.5f - 0.5f * MathF.Cos(MathF.Tau * (i + 0.5f) / n);
            _gain[o + i] = w * d / MathF.Max(dmax, 1e-3f);
            gsum += _gain[o + i];
        }
        for (var i = 0; i < n; i++) _gain[o + i] /= MathF.Max(gsum, 1e-6f);
        _beamFocus[beam] = focus;
        _beamActive[beam] = true;
    }

    /// <summary>원거리 조향(무할당) — 브로드사이드(+Z) 기준 deg 방향 평면파 위상 정렬.</summary>
    public void SteerBeam(int beam, float deg)
    {
        var o = beam * MaxElements;
        var n = _elementCount;
        var rad = deg * (MathF.PI / 180f);
        var u = new Vector3(MathF.Sin(rad), 0f, MathF.Cos(rad));
        var minP = float.MaxValue;
        for (var i = 0; i < n; i++) minP = MathF.Min(minP, Vector3.Dot(_elements[i] - ArrayCenter, u));
        float gsum = 0;
        for (var i = 0; i < n; i++)
        {
            _delay[o + i] = MathF.Min(MaxDelaySamples, (Vector3.Dot(_elements[i] - ArrayCenter, u) - minP) / SpeedOfSound * SampleRate);
            _gain[o + i] = 0.5f - 0.5f * MathF.Cos(MathF.Tau * (i + 0.5f) / n);
            gsum += _gain[o + i];
        }
        for (var i = 0; i < n; i++) _gain[o + i] /= MathF.Max(gsum, 1e-6f);
        _beamFocus[beam] = ArrayCenter + u * 3f;
        _beamActive[beam] = true;
    }

    public float Delay(int beam, int element) => _delay[beam * MaxElements + element];
    public float Gain(int beam, int element) => _gain[beam * MaxElements + element];
    public Vector3 BeamFocus(int beam) => _beamFocus[beam];
    public bool BeamActive(int beam) => _beamActive[beam];

    /// <summary>점 P 에서의 정규화 배열 응답(0..1 · 집속점 = 1) — 지연 위상 + 전파 위상 합.</summary>
    public float Response(int beam, Vector3 p, float freqHz)
    {
        var o = beam * MaxElements;
        double re = 0, im = 0, norm = 0;
        var w = 2.0 * Math.PI * freqHz;
        for (var i = 0; i < _elementCount; i++)
        {
            var d = Math.Max(0.05, Vector3.Distance(p, _elements[i]));
            var amp = _gain[o + i] / d;
            var ph = -w * (_delay[o + i] / (double)SampleRate + d / SpeedOfSound);
            re += amp * Math.Cos(ph); im += amp * Math.Sin(ph);
            norm += amp;
        }
        return norm <= 0 ? 0f : (float)(Math.Sqrt(re * re + im * im) / norm);
    }

    /// <summary>극좌표 빔 패턴(dB · −90..+90° · radius m) — UI 폴라 플롯.</summary>
    public void BeamPattern(int beam, float freqHz, float radius, Span<float> outDb)
    {
        lock (_gate)
        {
            for (var k = 0; k < outDb.Length; k++)
            {
                var deg = -90f + 180f * k / Math.Max(1, outDb.Length - 1);
                var rad = deg * (MathF.PI / 180f);
                var r = Response(beam, ArrayCenter + new Vector3(MathF.Sin(rad), 0f, MathF.Cos(rad)) * radius, freqHz);
                outDb[k] = 20f * MathF.Log10(MathF.Max(r, 1e-4f));
            }
        }
    }

    /// <summary>
    /// 빔 오디오 렌더(무할당) — 모노 트랙 → 소자별 인터리브 출력(frames × ElementCount).
    /// 분수 지연 선형 보간 · 블록 내 지연/이득 선형 램프. accumulate=true 면 다른 빔 위에 합산.
    /// </summary>
    public int RenderBlock(int beam, ReadOnlySpan<float> mono, Span<float> outInterleaved, bool accumulate)
    {
        var e = _elementCount;
        var frames = Math.Min(mono.Length, MaxBlock);
        if ((uint)beam >= MaxBeams || outInterleaved.Length < frames * e) return 0;
        lock (_gate)
        {
            var ring = _ring.AsSpan(beam * RingLen, RingLen);
            var w = _writePos[beam];
            for (var n = 0; n < frames; n++) ring[(w + n) & RingMask] = mono[n];
            var o = beam * MaxElements;
            var inv = 1f / Math.Max(1, frames);
            for (var i = 0; i < e; i++)
            {
                float d0 = _prevDelay[o + i], d1 = _delay[o + i], g0 = _prevGain[o + i], g1 = _gain[o + i];
                if (!_beamActive[beam]) g1 = 0f;
                for (var n = 0; n < frames; n++)
                {
                    var t = (n + 1) * inv;
                    var pos = w + n - (d0 + (d1 - d0) * t);
                    var i0 = (int)MathF.Floor(pos);
                    var frac = pos - i0;
                    var s = ring[i0 & RingMask] * (1f - frac) + ring[(i0 + 1) & RingMask] * frac;
                    var y = (g0 + (g1 - g0) * t) * s;
                    var idx = n * e + i;
                    outInterleaved[idx] = accumulate ? outInterleaved[idx] + y : y;
                }
                _prevDelay[o + i] = d1; _prevGain[o + i] = g1;
            }
            _writePos[beam] = (w + frames) & RingMask;
            return frames;
        }
    }

    /// <summary>눈 위치 집속 프레넬 위상(무할당 · 배정밀 위상 → [0, 2π)).</summary>
    public void ComputeHoloPhase(Vector3 eye, Span<float> phase)
    {
        var k = 2.0 * Math.PI / HoloWavelength;
        var c = HoloCenter;
        for (var y = 0; y < HoloRes; y++)
        {
            for (var x = 0; x < HoloRes; x++)
            {
                double px = c.X + (x - (HoloRes - 1) * 0.5) * HoloPitch, py = c.Y + (y - (HoloRes - 1) * 0.5) * HoloPitch;
                double dx = eye.X - px, dy = eye.Y - py, dz = eye.Z - c.Z;
                var m = (k * Math.Sqrt(dx * dx + dy * dy + dz * dz)) % (2.0 * Math.PI);
                phase[y * HoloRes + x] = (float)(m == 0 ? 0 : 2.0 * Math.PI - m);
            }
        }
    }

    /// <summary>위상 맵의 점 P 집속 이득(0..1 · 집속 눈 = 1).</summary>
    public double HoloFocusGain(ReadOnlySpan<float> phase, Vector3 p)
    {
        var k = 2.0 * Math.PI / HoloWavelength;
        var c = HoloCenter;
        double re = 0, im = 0;
        for (var y = 0; y < HoloRes; y++)
        {
            for (var x = 0; x < HoloRes; x++)
            {
                double px = c.X + (x - (HoloRes - 1) * 0.5) * HoloPitch, py = c.Y + (y - (HoloRes - 1) * 0.5) * HoloPitch;
                double dx = p.X - px, dy = p.Y - py, dz = p.Z - c.Z;
                var th = phase[y * HoloRes + x] + (k * Math.Sqrt(dx * dx + dy * dy + dz * dz)) % (2.0 * Math.PI);
                re += Math.Cos(th); im += Math.Sin(th);
            }
        }
        return Math.Sqrt(re * re + im * im) / (HoloRes * HoloRes);
    }

    /// <summary>패널 중심 → 눈 가시 광선 수평각(°).</summary>
    public float ViewAngleDeg(Vector3 eye) => MathF.Atan2(eye.X - HoloCenter.X, MathF.Max(1e-3f, eye.Z - HoloCenter.Z)) * (180f / MathF.PI);

    /// <summary>
    /// 1틱(무할당) — 2초 무갱신 시청자 만료 → 빔 재계산(트랙 추종 집속 or 수동 조향) → 1번 빔 시청자 눈 홀로 위상 → 대비(dB) 통계.
    /// </summary>
    public void Tick(long tMicros)
    {
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            for (var i = 0; i < MaxViewers; i++)
                if (_viewers[i].Active && tMicros - _viewers[i].UpdatedMicros > 2_000_000) _viewers[i].Active = false;
            for (var b = 0; b < MaxBeams; b++)
                if (_beamViewer[b] >= 0 && !_viewers[_beamViewer[b]].Active) _beamViewer[b] = -1;
            var primary = -1;
            if (Mode == BeamSteerMode.ManualAngle)
            {
                SteerBeam(0, _manualDeg);
                for (var b = 1; b < MaxBeams; b++) _beamActive[b] = false;
                Volatile.Write(ref _steerDeg, _manualDeg);
            }
            else
            {
                for (var b = 0; b < MaxBeams; b++)
                {
                    var s = _beamViewer[b];
                    if (s < 0) { _beamActive[b] = false; continue; }
                    FocusBeam(b, FocusOf(s));
                    if (primary < 0) primary = s;
                }
                if (primary >= 0)
                {
                    var f = _beamFocus[_viewers[primary].Track];
                    Volatile.Write(ref _steerDeg, MathF.Atan2(f.X - ArrayCenter.X, MathF.Max(1e-3f, f.Z - ArrayCenter.Z)) * (180f / MathF.PI));
                }
            }
            if (_beamActive[0] || primary >= 0)
            {
                var beam = primary >= 0 ? _viewers[primary].Track : 0;
                var focus = _beamFocus[beam];
                var other = Vector3.Zero;
                var others = 0;
                for (var i = 0; i < MaxViewers; i++)
                    if (_viewers[i].Active && i != primary) { other += _viewers[i].Head; others++; }
                var probe = others > 0 ? other / others : new Vector3(2f * ArrayCenter.X - focus.X + (MathF.Abs(focus.X - ArrayCenter.X) < 0.3f ? 1f : 0f), focus.Y, focus.Z);
                var on = Response(beam, focus, 2000f);
                var off = Response(beam, probe, 2000f);
                Volatile.Write(ref _contrastDb, 20f * MathF.Log10(MathF.Max(on, 1e-4f) / MathF.Max(off, 1e-4f)));
            }
            if (primary >= 0)
            {
                ComputeHoloPhase(_viewers[primary].LeftEye, _holoL);
                ComputeHoloPhase(_viewers[primary].RightEye, _holoR);
                Volatile.Write(ref _holoGain, (float)HoloFocusGain(_holoL, _viewers[primary].LeftEye));
            }
        }
        Volatile.Write(ref _tickMicros, (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
        if ((Interlocked.Increment(ref _ticks) & 31) == 0) Raise();
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Teleport.Beam.OnChange", ex); }
    }
}
