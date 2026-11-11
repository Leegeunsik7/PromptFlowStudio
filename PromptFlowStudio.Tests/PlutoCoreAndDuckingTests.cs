using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Pluto.Dsp;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎚️ 사이드체인 더킹 DSP · 🪐 PlutoCore 허브 관리자/페일오버 라우팅.</summary>
public sealed class PlutoCoreAndDuckingTests
{
    private const int Rate = 48000;
    private static readonly IReadOnlyList<PlutoSfxCue> Cues = PlutoSfxDirector.Shared.BuildDefault15sCueSheet();

    private static int At(double sec) => (int)Math.Round(sec * Rate);
    private static double Db(double g) => 20.0 * Math.Log10(g);

    [Fact]
    public void SidechainDucker_AttenuatesBgm_DuringSfxIntervals()
    {
        var n = At(15.0);
        var env = PlutoSidechainDucker.ComputeGainEnvelope(Cues, n, Rate);

        // 1) 큐 이전/사이 구간 무변경(≈ 0 dB)
        Assert.Equal(1f, env[At(0.5)]);
        Assert.InRange(Db(env[At(3.0)]), -0.05, 0.0);
        Assert.InRange(Db(env[At(10.0)]), -0.05, 0.0);

        // 2) 각 큐 본체 −4.0 dB ± 0.3 (셔터 0.8s · 차임 5.5s · 벨 13.2s)
        foreach (var c in Cues)
        {
            var mid = c.OffsetMs / 1000.0 + Math.Min(0.1, PlutoAudioSynthesizer.DurationMs(c.SfxType) / 2000.0);
            Assert.InRange(Db(env[At(mid)]), -4.3, -3.7);
        }

        // 3) 25ms 선행 어택 — 큐 시작 시점엔 부분 감쇠(급변 없음)
        Assert.InRange(Db(env[At(0.8)]), -3.5, -1.0);
        Assert.InRange(Db(env[At(0.8 - PlutoSidechainDucker.PrePadSec) - 1]), -0.01, 0.0);

        // 4) 180ms 지수 릴리스 — 셔터 종료(0.95s)+180ms 이후 서서히 복귀
        var relStart = 0.95 + PlutoSidechainDucker.PostPadSec;
        Assert.InRange(Db(env[At(relStart)]), -4.3, -3.7);
        Assert.InRange(Db(env[At(relStart + 0.18)]), -2.0, -1.0);   // 1τ ≈ 63% 복귀
        Assert.InRange(Db(env[At(relStart + 1.0)]), -0.05, 0.0);

        // 5) 평활도 — 샘플간 최대 게인 변화 < 0.001(펌핑/클릭 없음)
        var maxStep = 0f;
        for (var i = 1; i < n; i++) maxStep = Math.Max(maxStep, Math.Abs(env[i] - env[i - 1]));
        Assert.True(maxStep < 1e-3f, "maxStep=" + maxStep);

        // 6) 1.0 진폭 톤 · 스테레오 동일 엔벨로프 · 결정론
        var l = new float[n];
        var r = new float[n];
        for (var i = 0; i < n; i++) l[i] = r[i] = (float)Math.Sin(2 * Math.PI * 440 * i / Rate);
        PlutoSidechainDucker.Shared.ApplyDucking(l, r, Cues, Rate);
        for (var i = 0; i < n; i += 997)
        {
            Assert.Equal(l[i], r[i]);
            Assert.Equal((float)Math.Sin(2 * Math.PI * 440 * i / Rate) * env[i], l[i], 1e-5f);
        }

        // 7) 무효 입력 → 무변경
        var flat = new float[1000];
        Array.Fill(flat, 1f);
        PlutoSidechainDucker.Shared.ApplyDucking(flat, Array.Empty<PlutoSfxCue>(), Rate);
        PlutoSidechainDucker.Shared.ApplyDucking(flat, Cues, Rate, 0f);
        PlutoSidechainDucker.Shared.ApplyDucking(flat, Cues, 100);
        Assert.All(flat, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void SidechainDucker_MergesOverlappingAndUnsortedCues()
    {
        var cues = new[]
        {
            new PlutoSfxCue(PlutoSfxType.ChimeSparkle, 2000, 0.6),
            new PlutoSfxCue(PlutoSfxType.ShutterClick, 1000, 0.7),
            new PlutoSfxCue(PlutoSfxType.ShutterClick, 1100, 0.7),   // 겹침
            new PlutoSfxCue(PlutoSfxType.ShutterClick, -50, 0.7),    // 음수 오프셋 무시
        };
        Span<int> win = stackalloc int[cues.Length * 2];
        var m = PlutoSidechainDucker.BuildWindows(cues, Rate, At(5.0), win);
        Assert.Equal(2, m);
        Assert.True(win[0] < win[1] && win[1] < win[2] && win[2] < win[3]);
        Assert.Equal(At(1.0 - PlutoSidechainDucker.PrePadSec), win[0]);
    }

    [Fact]
    public void PlutoTruePeakMasteringEngine_WithDuckedBgm_PreservesTargetLoudness()
    {
        var (l, r) = MakeBgm(At(15.0));

        // 1) 마스터링 → 더킹(BGM) → SFX 합산 → 최종 트루피크 리미팅
        Assert.True(PlutoTruePeakMasteringEngine.MasterInPlace(l, r, Rate, out var rep1));
        Assert.True(rep1.LoudnessOnTarget);
        PlutoSidechainDucker.Shared.ApplyDucking(l, r, Cues, Rate);
        Assert.True(PlutoTruePeakMasteringEngine.MeasureTruePeakDbtp(l, r) <= PlutoTruePeakMasteringEngine.CeilingDbtp + 0.01);   // 게인 ≤ 1 → 상한 유지
        foreach (var c in Cues) MixSfx(l, r, c);
        Assert.True(PlutoTruePeakMasteringEngine.MasterInPlace(l, r, Rate, out var rep2));
        var lufs = PlutoTruePeakMasteringEngine.MeasureIntegratedLoudness(l, r, Rate);
        Assert.InRange(lufs, PlutoTruePeakMasteringEngine.TargetLufs - 0.5, PlutoTruePeakMasteringEngine.TargetLufs + 0.5);
        Assert.True(PlutoTruePeakMasteringEngine.MeasureTruePeakDbtp(l, r) <= -1.0, "TP=" + rep2.OutputTruePeakDbtp);

        // 2) WAV 경로(StockSuite 결선) — 더킹 큐 투입 시 큐 구간만 −4 dB, 그 외 동일
        var (sl, sr) = MakeBgm(At(15.0));
        var wav = PlutoPcmWav.EncodePcm16(sl, sr, 2, Rate);
        Assert.True(PlutoTruePeakMasteringEngine.TryMasterWavBuffer(wav, out var plain, out _));
        Assert.True(PlutoTruePeakMasteringEngine.TryMasterWavBuffer(wav, Cues, out var ducked, out var rep3));
        Assert.True(rep3.LoudnessOnTarget);
        var a = Decode(plain);
        var b = Decode(ducked);
        Assert.InRange(Db(Rms(b, 0.88, 0.92) / Rms(a, 0.88, 0.92)), -4.3, -3.7);
        Assert.InRange(Db(Rms(b, 5.70, 5.90) / Rms(a, 5.70, 5.90)), -4.3, -3.7);
        Assert.InRange(Db(Rms(b, 3.00, 4.00) / Rms(a, 3.00, 4.00)), -0.05, 0.05);
        Assert.True(PlutoTruePeakMasteringEngine.MeasureTruePeakDbtp(b, b) <= -1.0);
    }

    [Fact]
    public async Task PlutoCoreProcessManager_DetectsStatus_GracefullyWhenDirectoryMissing()
    {
        Assert.Equal(@"D:\PROGRAM\PlutoCore", PlutoCoreOptions.DefaultRootDirectory);
        var missing = Path.Combine(Path.GetTempPath(), "pfs_plutocore_missing_" + Guid.NewGuid().ToString("N"));
        using var mgr = new PlutoCoreProcessManager(new PlutoCoreOptions
        {
            RootDirectory = missing,
            PipeName = "pfs_test_absent_" + Guid.NewGuid().ToString("N"),
            ProcessName = "pfs_no_such_process_" + Guid.NewGuid().ToString("N"),
            HealthUrl = "http://127.0.0.1:1/health",   // 닫힌 루프백 포트 → 1.5초 내 실패
        });
        var changed = 0;
        mgr.OnChanged += () => changed++;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var st = await mgr.CheckStatusAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(st.DirectoryExists);
        Assert.False(st.IsRunning);
        Assert.Null(st.ProcessId);
        Assert.Null(st.ExecutablePath);
        Assert.False(st.CanLaunch);
        Assert.Null(st.Latency);
        Assert.Null(mgr.FindExecutable());
        Assert.False(await mgr.TryLaunchAsync());
        await mgr.StopAsync();   // 기동 이력 없음 → 무동작
        Assert.False(mgr.Current.IsRunning);
        Assert.Equal(0, changed);   // Unknown → 동일 상태(변화 없음)

        // 비루프백 헬스 URL 무시(프라이버시 게이트) · 파이프명 경로 주입 차단
        Assert.False(new PlutoCoreOptions { HealthUrl = "http://10.0.0.5:8080/health" }.TryResolveHealthUri(out _));
        Assert.False(PlutoCoreProcessManager.IsPipePresent(@"..\evil"));

        // 미실행 → PlutoCore 단계 건너뜀 → 결정론 템플릿(null)
        var core = new PlutoCoreLlmClient(mgr);
        Assert.False(core.IsAvailable);
        Assert.Null(await PlutoLlmBridge.TryCompleteAsync(new PlutoLlmFailoverClient(null, null, core), "s", "u", 0.7, CancellationToken.None));
    }

    [Fact]
    public async Task PlutoCore_FailoverChain_RoutesToLivePipeHub_WhenGeminiUnavailable()
    {
        var pipeName = "pfs_test_hub_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string? request = null;
        var hub = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            request = await reader.ReadLineAsync();
            await writer.WriteLineAsync("{\"Success\":true,\"Content\":\"```json\\n{\\\"from\\\":\\\"core\\\"}\\n```\",\"DurationMs\":12}");
        });

        using var mgr = new PlutoCoreProcessManager(new PlutoCoreOptions
        {
            RootDirectory = Path.GetTempPath(),
            PipeName = pipeName,
            ProcessName = "pfs_no_such_process_" + Guid.NewGuid().ToString("N"),
            ApiKey = "test-key",
        });
        var st = await mgr.CheckStatusAsync();
        Assert.True(st.IsRunning);
        Assert.True(st.Latency.HasValue);
        Assert.Equal("pipe://./" + pipeName, st.EndpointUrl);

        var chain = new PlutoLlmFailoverClient(new OfflineLlm(), null, new PlutoCoreLlmClient(mgr));
        Assert.True(chain.IsAvailable);
        var text = await PlutoLlmBridge.TryCompleteAsync(chain, "sys", "user", 0.7, CancellationToken.None, TimeSpan.FromSeconds(10));
        await hub.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("{\"from\":\"core\"}", text);   // 🩹 펜스 자가 치유
        Assert.NotNull(request);
        using var doc = System.Text.Json.JsonDocument.Parse(request!);
        Assert.Equal(PlutoCoreOptions.AppName, doc.RootElement.GetProperty("AppName").GetString());
        Assert.Equal("test-key", doc.RootElement.GetProperty("ApiKey").GetString());

        // 응답 스키마 즉시 검증
        Assert.Null(PlutoCoreLlmClient.ParseResponse("{\"Success\":false,\"Error\":\"bad key\"}"));
        Assert.Null(PlutoCoreLlmClient.ParseResponse("not json"));
        Assert.Null(PlutoCoreLlmClient.ParseResponse("{\"Success\":true,\"Content\":\"  \"}"));
        Assert.Equal("ok", PlutoCoreLlmClient.ParseResponse("{\"Success\":true,\"Content\":\"ok\"}"));

        // 키 미설정 → 생존해도 라우팅 금지
        using var noKey = new PlutoCoreProcessManager(new PlutoCoreOptions { PipeName = pipeName, RootDirectory = Path.GetTempPath() });
        Assert.False(new PlutoCoreLlmClient(noKey).IsAvailable);
    }

    // ── helpers ──

    private sealed class OfflineLlm : IPlutoLlmClient
    {
        public bool IsAvailable => false;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>밀도 높은 BGM(패드 화음 + 킥 + 하이햇 노이즈) — 결정론 시드.</summary>
    private static (float[] L, float[] R) MakeBgm(int n)
    {
        var l = new float[n];
        var r = new float[n];
        var rng = new Random(1234);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var bt = t % 0.5;
            var kick = 0.5 * Math.Exp(-bt / 0.06) * Math.Sin(2 * Math.PI * 55 * bt);
            var pad = 0.08 * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.18 * t) + Math.Sin(2 * Math.PI * 329.63 * t));
            var hat = 0.05 * (rng.NextDouble() * 2 - 1);
            l[i] = (float)(0.3 * (kick + pad + hat));
            r[i] = (float)(0.3 * (kick + 0.9 * pad + hat));
        }
        return (l, r);
    }

    private static void MixSfx(float[] l, float[] r, PlutoSfxCue cue)
    {
        var wav = PlutoAudioSynthesizer.Generate(cue.SfxType);
        Assert.True(PlutoPcmWav.TryReadHeader(wav, out var info));
        var sl = new float[info.Frames];
        var sr = new float[info.Frames];
        PlutoPcmWav.DecodeChannel(wav, in info, 0, sl);
        PlutoPcmWav.DecodeChannel(wav, in info, info.Channels >= 2 ? 1 : 0, sr);
        var off = At(cue.OffsetMs / 1000.0);
        var v = (float)cue.VolumeRatio;
        for (var i = 0; i < sl.Length && off + i < l.Length; i++)
        {
            l[off + i] += sl[i] * v;
            r[off + i] += sr[i] * v;
        }
    }

    private static float[] Decode(byte[] wav)
    {
        Assert.True(PlutoPcmWav.TryReadHeader(wav, out var info));
        var x = new float[info.Frames];
        PlutoPcmWav.DecodeChannel(wav, in info, 0, x);
        return x;
    }

    private static double Rms(float[] x, double fromSec, double toSec)
    {
        double s = 0;
        int a = At(fromSec), b = At(toSec);
        for (var i = a; i < b; i++) s += (double)x[i] * x[i];
        return Math.Sqrt(s / Math.Max(1, b - a));
    }
}
