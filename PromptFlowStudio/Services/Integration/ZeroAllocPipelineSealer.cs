using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>핫패스 1개의 무할당 계측 결과(불변 · HUD 표출).</summary>
public sealed record HotPathSealReport(
    string Name, int Iterations, double BytesPerOp, long TotalBytes,
    int Gen0Delta, int Gen1Delta, int Gen2Delta, long LohDeltaKb, long PohDeltaKb, bool Sealed, string Note);

/// <summary>파이프라인 봉인 인증서(불변 · SHA-256 해시로 리포트 집합을 하드 바인딩).</summary>
public sealed record PipelineSealCertificate(
    DateTimeOffset At, string Version, int Paths, int SealedPaths, int Violations,
    double TotalBytesPerOp, long Gen2Delta, string Hash)
{
    /// <summary>전 경로 무할당 달성 여부(위반 0 · 경로 1개 이상).</summary>
    public bool IsSealed => Violations == 0 && Paths > 0;
}

/// <summary>
/// [PromptFlow Studio v8.9.3] Zero-Alloc 파이프라인 최종 봉인기(관찰 가능 싱글턴 · 순수 BCL <see cref="GC"/>/<see cref="GCMemoryInfo"/>/<see cref="ArrayPool{T}"/> · 외부 NuGet 0).
///   ▸ <see cref="SealAsync"/>: 등록된 핫패스를 <b>단일 전용 스레드</b>에서 워밍업 후 반복 실행하며 <see cref="GC.GetAllocatedBytesForCurrentThread"/> 델타로 op 당 바이트를 계측(스레드 홉 없음 = 계측 오염 0).
///   ▸ Gen0/1/2 수집 횟수 · LOH/POH 세대 힙 증분을 동시 채집해 "핫패스가 LOH/POH 를 건드렸는가"를 직접 판정한다.
///   ▸ 전 경로 위반 0 이면 리포트 집합을 정규화 직렬화해 SHA-256 으로 봉인(<see cref="PipelineSealCertificate"/>) — v8 릴리스 게이트의 무할당 증적으로 보관.
///   ▸ 기본 등록 프로브는 v8 파이프라인의 실제 핫패스 형상(고정 링버퍼 쓰기 · Span 파싱 · 풀 대여/반납 · stackalloc HEX · Interlocked 계수)을 재현한다.
///   ⚠ <see cref="OnChange"/> 는 계측 스레드(백그라운드)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장). 프로브 예외는 삼켜 위반으로 기록(앱 동작 무영향).
/// </summary>
public sealed class ZeroAllocPipelineSealer
{
    private const int WarmupIterations = 128;
    private const int DefaultIterations = 4096;
    private const double AllocTolerancePerOp = 0.5;   // op 당 0.5B 미만이면 무할당으로 판정(계측 노이즈 흡수)
    private const string SealVersion = "v8.9.0";

    private readonly List<Probe> _probes = new();
    private readonly List<HotPathSealReport> _reports = new();
    private readonly SemaphoreSlim _sealGate = new(1, 1);
    private readonly object _gate = new();
    private readonly AutonomousMemoryCompactor? _compactor;

    public ZeroAllocPipelineSealer(AutonomousMemoryCompactor? compactor = null)
    {
        _compactor = compactor;
        RegisterDefaultProbes();
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public bool IsSealing { get; private set; }
    public int CurrentPath { get; private set; }
    public int PathCount { get { lock (_gate) return _probes.Count; } }
    public int SealedPaths { get; private set; }
    public int ViolationCount { get; private set; }
    public double TotalBytesPerOp { get; private set; }
    public double WorstBytesPerOp { get; private set; }
    public string? WorstPath { get; private set; }
    public PipelineSealCertificate? Certificate { get; private set; }
    public string? LastAction { get; private set; }
    public DateTimeOffset? LastSealAt { get; private set; }

    /// <summary>봉인 성립 여부(인증서 발급 + 위반 0).</summary>
    public bool IsPipelineSealed => Certificate?.IsSealed == true;

    /// <summary>계측 리포트 스냅샷(등록 순 · 스냅샷 시점에만 할당).</summary>
    public IReadOnlyList<HotPathSealReport> Reports
    {
        get { lock (_gate) return _reports.ToArray(); }
    }

    // ── 등록 ──

    /// <summary>핫패스 프로브 등록(동일 이름 재등록 시 교체 — 멱등).</summary>
    public void RegisterHotPath(string name, Action probe, int iterations = DefaultIterations)
    {
        if (string.IsNullOrWhiteSpace(name) || probe is null) return;
        lock (_gate)
        {
            var i = _probes.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            var record = new Probe(name, probe, Math.Clamp(iterations, 64, 1 << 20));
            if (i >= 0) _probes[i] = record; else _probes.Add(record);
        }
        LastAction = $"핫패스 등록 — {name}";
        Notify();
    }

    /// <summary>v8 파이프라인 핫패스 형상 기본 프로브(외부 서비스 결합 0 · 순수 BCL 재현).</summary>
    private void RegisterDefaultProbes()
    {
        var ring = new long[64];
        var ringHead = 0;
        RegisterHotPath("텔레메트리 고정 링버퍼 쓰기", () =>
        {
            ring[ringHead] = Environment.TickCount64;
            ringHead = (ringHead + 1) & 63;
        });

        RegisterHotPath("Interlocked 카운터 누적", () =>
        {
            Interlocked.Increment(ref _opCounter);
            Interlocked.Read(ref _opCounter);
        });

        var payload = "cut=017;dur=4.500;seed=88213;grid=node-gpu-02".AsMemory();
        RegisterHotPath("Span 무할당 파싱(세그먼트 헤더)", () =>
        {
            var span = payload.Span;
            var total = 0;
            while (!span.IsEmpty)
            {
                var cut = span.IndexOf(';');
                var field = cut < 0 ? span : span[..cut];
                var eq = field.IndexOf('=');
                if (eq > 0 && int.TryParse(field[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) total += v;
                span = cut < 0 ? ReadOnlySpan<char>.Empty : span[(cut + 1)..];
            }
            _opCounter += total & 1;
        });

        RegisterHotPath("ArrayPool 대여/반납(추론 텐서 버퍼)", () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            buffer[0] = 1;
            buffer[4095] = 2;
            ArrayPool<byte>.Shared.Return(buffer);
        });

        RegisterHotPath("stackalloc HEX 포매팅(C2PA 다이제스트)", () =>
        {
            Span<char> hex = stackalloc char[16];
            var value = (ulong)Environment.TickCount64;
            for (var i = 0; i < 16; i++)
            {
                var nibble = (int)((value >> ((15 - i) * 4)) & 0xF);
                hex[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            }
            _opCounter += hex[15];
        });
    }

    private long _opCounter;

    // ── 봉인 ──

    /// <summary>
    /// 전 핫패스를 순차 계측하고 위반 0 이면 SHA-256 인증서를 발급한다.
    /// 계측 전 <see cref="AutonomousMemoryCompactor"/> 가 주입돼 있으면 1회 압축으로 기저선을 정리한다(정확도 향상).
    /// </summary>
    public async Task<PipelineSealCertificate> SealAsync(CancellationToken ct = default)
    {
        await _sealGate.WaitAsync(ct).ConfigureAwait(false);
        IsSealing = true;
        CurrentPath = 0;
        SealedPaths = 0;
        ViolationCount = 0;
        WorstBytesPerOp = 0d;
        WorstPath = null;
        lock (_gate) _reports.Clear();
        LastAction = "무할당 계측 시작…";
        Notify();

        try
        {
            Probe[] probes;
            lock (_gate) probes = _probes.ToArray();

            try { _compactor?.Compact(blocking: true); } catch { /* Graceful — 압축 실패는 계측을 막지 않음 */ }

            // 단일 전용 스레드에서 전 경로를 계측 — await 로 인한 스레드 홉이 없어야 GetAllocatedBytesForCurrentThread 델타가 유효.
            var measured = await Task.Run(() =>
            {
                var list = new List<HotPathSealReport>(probes.Length);
                foreach (var probe in probes)
                {
                    ct.ThrowIfCancellationRequested();
                    CurrentPath = list.Count + 1;
                    list.Add(Measure(probe));
                    Notify();
                }
                return list;
            }, ct).ConfigureAwait(false);

            var total = 0d;
            foreach (var r in measured)
            {
                total += r.BytesPerOp;
                if (r.Sealed) SealedPaths++; else ViolationCount++;
                if (r.BytesPerOp > WorstBytesPerOp) { WorstBytesPerOp = r.BytesPerOp; WorstPath = r.Name; }
            }
            lock (_gate) _reports.AddRange(measured);
            TotalBytesPerOp = total;

            var gen2 = 0;
            foreach (var r in measured) gen2 += r.Gen2Delta;
            var certificate = new PipelineSealCertificate(
                DateTimeOffset.Now, SealVersion, measured.Count, SealedPaths, ViolationCount,
                Math.Round(total, 3), gen2, ComputeSealHash(measured));
            Certificate = certificate;
            LastSealAt = certificate.At;
            LastAction = certificate.IsSealed
                ? $"봉인 완료 — {SealedPaths}/{measured.Count} 경로 0.0B · {certificate.Hash[..12]}"
                : $"봉인 보류 — 위반 {ViolationCount}건(최대 {WorstBytesPerOp:0.##}B/op · {WorstPath})";
            return certificate;
        }
        catch (OperationCanceledException)
        {
            LastAction = "봉인 취소 — 완료분 보존";
            var partial = new PipelineSealCertificate(DateTimeOffset.Now, SealVersion, SealedPaths + ViolationCount, SealedPaths, ViolationCount, TotalBytesPerOp, 0, string.Empty);
            Certificate = null;
            return partial;
        }
        finally
        {
            IsSealing = false;
            _sealGate.Release();
            Notify();
        }
    }

    /// <summary>프로브 1개 계측 — 워밍업(JIT/풀 예열) 후 기저선 정리, 반복 실행 델타 채집.</summary>
    private static HotPathSealReport Measure(Probe probe)
    {
        var note = "핫패스 무할당";
        try
        {
            for (var i = 0; i < WarmupIterations; i++) probe.Action();

            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            var infoBefore = GC.GetGCMemoryInfo();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < probe.Iterations; i++) probe.Action();

            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var infoAfter = GC.GetGCMemoryInfo();
            var totalBytes = allocAfter - allocBefore;
            var perOp = probe.Iterations <= 0 ? 0d : (double)totalBytes / probe.Iterations;
            var loh = HeapDeltaKb(infoBefore, infoAfter, 3);
            var poh = HeapDeltaKb(infoBefore, infoAfter, 4);
            var ok = perOp < AllocTolerancePerOp && loh <= 0 && poh <= 0;
            if (!ok) note = perOp >= AllocTolerancePerOp ? $"op 당 {perOp:0.##}B 할당 — 핫패스 박싱/클로저 점검" : "LOH/POH 증분 감지 — 대형 배열 풀링 점검";
            return new HotPathSealReport(probe.Name, probe.Iterations, Math.Round(perOp, 3), totalBytes,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, loh, poh, ok, note);
        }
        catch (Exception ex)
        {
            return new HotPathSealReport(probe.Name, probe.Iterations, 0d, 0, 0, 0, 0, 0, 0, false, $"프로브 예외 — {ex.GetType().Name}");
        }
    }

    /// <summary>세대 힙 증분(KB) — GenerationInfo 3=LOH · 4=POH(런타임이 제공하지 않으면 0).</summary>
    private static long HeapDeltaKb(GCMemoryInfo before, GCMemoryInfo after, int generation)
    {
        var b = before.GenerationInfo;
        var a = after.GenerationInfo;
        if (b.Length <= generation || a.Length <= generation) return 0;
        return (a[generation].SizeAfterBytes - b[generation].SizeAfterBytes) / 1024;
    }

    /// <summary>리포트 집합 정규화 직렬화 → SHA-256 HEX(소문자) 봉인 해시.</summary>
    private static string ComputeSealHash(IReadOnlyList<HotPathSealReport> reports)
    {
        var sb = new StringBuilder(reports.Count * 64);
        sb.Append(SealVersion).Append('|');
        foreach (var r in reports)
        {
            sb.Append(r.Name).Append(':')
              .Append(r.Iterations.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(r.BytesPerOp.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
              .Append(r.LohDeltaKb.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(r.PohDeltaKb.ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private readonly record struct Probe(string Name, Action Action, int Iterations);
}
