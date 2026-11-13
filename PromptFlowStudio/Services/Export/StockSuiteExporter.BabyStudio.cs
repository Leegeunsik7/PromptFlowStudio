using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Export;

/// <summary>🎵 아기 감성 BGM 트랙.</summary>
public enum BabyBgmTrack { Lullaby, MusicBox, Acoustic }

/// <summary>💌 시간 동기 자막 큐(초 단위 표시 구간 + 주 자막(KO) + 선택 보조 자막(EN)).</summary>
public readonly record struct SubtitleCue(int StartSec, int EndSec, string Text, string? SecondaryText = null)
{
    /// <summary>🎞 정밀 시작(초 · 소수) — 설정 시 <see cref="StartSec"/> 대신 번인/SRT/프리뷰에 사용(몽타주 클립 경계 정렬).</summary>
    public double? StartPreciseSec { get; init; }
    /// <summary>🎞 정밀 종료(초 · 소수) — 설정 시 <see cref="EndSec"/> 대신 사용.</summary>
    public double? EndPreciseSec { get; init; }
    /// <summary>실효 시작(초) — 정밀 값 우선.</summary>
    public double EffectiveStartSec => StartPreciseSec ?? StartSec;
    /// <summary>실효 종료(초) — 정밀 값 우선.</summary>
    public double EffectiveEndSec => EndPreciseSec ?? EndSec;

    /// <summary>🎞 소수 초 큐 — 정수 필드는 floor/ceil 근사(하위 호환) · 정밀 필드에 원값.</summary>
    public static SubtitleCue Precise(double startSec, double endSec, string text, string? secondaryText = null)
        => new((int)Math.Floor(startSec), (int)Math.Ceiling(endSec), text, secondaryText) { StartPreciseSec = startSec, EndPreciseSec = endSec };
}

public sealed partial class StockSuiteExporter
{
    /// <summary>🎵 아기 BGM 덕킹 볼륨(-14 LUFS 정규화 후 0.25 배).</summary>
    public const double BabyBgmVolume = 0.25;
    /// <summary>🎵 아기 BGM 페이드 인/아웃 길이(초).</summary>
    public const double BabyBgmFadeSec = 1.5;
    /// <summary>🎵 주 산출물 먹싱 임시 접미사(성공 시 원자적 교체 · 실패 시 삭제).</summary>
    internal const string BabyBgmMuxTempSuffix = ".~bgm";
    public const string BabyBgmTag = "🎵 감성 BGM · ";
    public const string GrowthBadgeTag = "🏷️ 성장 뱃지 · ";

    /// <summary>🎵 캔버스 BGM 토글 결선 — null = OFF(무음/기존 트랙 유지).</summary>
    public BabyBgmTrack? BabyBgm { get; set; }

    /// <summary>🎵 BGM 직접 먹싱 활성(<see cref="ExportProfile.BabyCinematic"/>) — 주 MP4 단일 산출물.</summary>
    public bool IsBabyBgmEnabled => BabyBgm is not null;

    /// <summary>⏱️ 비디오 길이 프로브(초 · 0 = 미상) — 기본 <see cref="MediaProbeService.GetDurationAsync"/>. 테스트 주입용.</summary>
    public Func<string, CancellationToken, Task<double>> DurationProbe { get; set; }
        = static (p, ct) => new MediaProbeService().GetDurationAsync(p, ct);

    /// <summary>🎵 아기 BGM 해석기(트랙, 길이) → WAV 경로. 기본 = 로컬 합성(오프라인).</summary>
    public Func<BabyBgmTrack, double, string?> BabyBgmResolver { get; set; } = static (t, d) => EnsureBabyBgm(t, d, null);

    /// <summary>🏷️ 성장 뱃지 번인 텍스트 — null/공백 = OFF.</summary>
    public string? GrowthBadgeText { get; set; }

    public static string BabyBgmLabel(BabyBgmTrack t) => t switch
    {
        BabyBgmTrack.MusicBox => "🎠 오르골",
        BabyBgmTrack.Acoustic => "🎸 어쿠스틱",
        _ => "🌙 자장가",
    };

    /// <summary>⏱️ 페이드 아웃 시작 = max(0, 길이 − 1.5s).</summary>
    public static double BabyBgmFadeOutStart(double durationSec) => Math.Max(0, durationSec - BabyBgmFadeSec);

    /// <summary>
    /// 🎵 아기 BGM 오디오 필터 — loudnorm(-14 LUFS) → volume 0.25 → afade in 1.5s → (길이 기지 시) afade out <c>st={길이-1.5:F2}</c>.
    /// <paramref name="mastered"/> = C# 트루피크 마스터링 완료 WAV → loudnorm·volume 이중 마스터링 생략(페이드만).
    /// </summary>
    public static string BuildBabyBgmAudioFilter(double durationSec, bool mastered = false)
    {
        var inv = CultureInfo.InvariantCulture;
        var d = BabyBgmFadeSec.ToString("0.###", inv);
        var f = (mastered ? "" : AudioMuxService.LoudnormFilter + ",volume=" + BabyBgmVolume.ToString("0.##", inv) + ",") + "afade=t=in:st=0:d=" + d;
        if (double.IsFinite(durationSec) && durationSec > 0)
            f += ",afade=t=out:st=" + BabyBgmFadeOutStart(durationSec).ToString("F2", inv) + ":d=" + d;
        return f;
    }

    /// <summary>🎵 아기 BGM 합성 인자 — 비디오 copy + BGM 루프 · AAC · -shortest.</summary>
    public static string[] BuildBabyBgmMuxArgs(string videoPath, string bgmPath, double durationSec, string outputPath, bool mastered = false)
    {
        var a = new List<string>(32)
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-stream_loop", "-1", "-i", bgmPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus" : "aac", "-b:a", AudioMuxService.AudioBitrate,
            "-af", BuildBabyBgmAudioFilter(durationSec, mastered),
            "-ar", AudioMuxService.OutputSampleRate, "-shortest",
        };
        if (double.IsFinite(durationSec) && durationSec > 0) { a.Add("-t"); a.Add(durationSec.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (videoPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { a.Add("-movflags"); a.Add("+faststart"); a.Add("-f"); a.Add("mp4"); }
        else if (videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) { a.Add("-f"); a.Add("webm"); }
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>🎵 아기 BGM 로컬 합성 WAV(캐시 재사용) — 실패 시 null.</summary>
    public static string? EnsureBabyBgm(BabyBgmTrack track, double durationSec, string? cacheDir)
    {
        var secs = Math.Clamp(Math.Ceiling(durationSec > 0 ? durationSec : 5) + 1, 3, 120);
        var dir = string.IsNullOrWhiteSpace(cacheDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "bgm") : cacheDir;
        var path = Path.Combine(dir, "baby_" + track.ToString().ToLowerInvariant() + "_" + secs.ToString("0", CultureInfo.InvariantCulture) + "s.wav");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 44) return path;
            Directory.CreateDirectory(dir);
            var req = track switch
            {
                BabyBgmTrack.MusicBox => new MusicRequest(MusicGenre.Ballad, null, "soft music box lullaby", secs, Seed: 100, AllowRemote: false, Vocals: false),
                BabyBgmTrack.Acoustic => new MusicRequest(MusicGenre.Ballad, null, "gentle acoustic guitar baby", secs, Seed: 365, AllowRemote: false, Vocals: false),
                _ => new MusicRequest(MusicGenre.Lofi, null, "soft lullaby sleep", secs, Seed: 30, AllowRemote: false, Vocals: false),
            };
            var t = AiGenerativeMusicEngine.Synthesize(req);
            var tmp = path + ".~tmp";
            SoundDsp.WriteStereoWav(tmp, t.Left, t.Right, t.Rate);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("StockSuite.BabyBgm", ex);
            return null;
        }
    }

    /// <summary>
    /// 🎵 BGM ON 시 주 산출물(<paramref name="primaryPath"/>)에 BGM 직접 먹싱 — 길이 프로브 → 페이드 아웃 계산 → 임시 파일 합성 → 원자적 교체.
    /// <see cref="ExportProfile.BabyCinematic"/> 로 실행(Silent Ambient Guard 면제). 성공 true · OFF/실패 false(무음 산출물 보존 · 취소는 전파).
    /// </summary>
    public async Task<bool> TryMuxBabyBgmIntoPrimaryAsync(string primaryPath, CancellationToken ct = default)
        => await MuxBabyAudioIntoPrimaryAsync(primaryPath, ct).ConfigureAwait(false) >= 0;

    /// <summary>
    /// 🎵🔔 BGM(+ <see cref="IncludeSfx"/> 시 타임라인 SFX) 먹싱 코어 — 반환 −1 = OFF/실패(무음 보존) · 0 = BGM 단독 · n&gt;0 = BGM + SFX n큐.
    /// SFX 합성/믹싱 실패 → 경고 로그 후 BGM 단독 폴백. 임시 SFX WAV 는 finally 에서 정리.
    /// </summary>
    internal async Task<int> MuxBabyAudioIntoPrimaryAsync(string primaryPath, CancellationToken ct)
    {
        if (BabyBgm is not { } track || string.IsNullOrWhiteSpace(primaryPath) || !File.Exists(primaryPath)) return -1;
        double duration;
        try { duration = await DurationProbe(primaryPath, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("StockSuite.BabyBgm.Probe", ex); duration = 0; }
        string? bgm;
        try { bgm = BabyBgmResolver(track, duration); }
        catch (Exception ex) { Program.Log("StockSuite.BabyBgm.Resolve", ex); bgm = null; }
        if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return -1;
        var temp = primaryPath + BabyBgmMuxTempSuffix;
        string[]? sfxFiles = null;
        string? masteredBgm = null;
        try
        {
            // 🎛️🥁🎚️ FFmpeg 투입 전 C# DSP — 트루피크 마스터링(−14 LUFS · ≤ −1 dBTP) + 비트 그리드 + SFX 큐 사이드체인 더킹(실패 시 원본 BGM · 스냅 생략).
            var rawCues = IncludeSfx ? ResolveSfxCues(duration) : null;
            var dsp = await PrepareBgmDspAsync(bgm, IncludeSfx && BeatSyncEnabled, ct, SidechainDuckingEnabled ? rawCues : null).ConfigureAwait(false);
            masteredBgm = dsp.MasteredPath;
            if (masteredBgm is not null) bgm = masteredBgm;

            // 🔔 SFX 타임라인 믹스 우선 시도 — 실패 시 BGM 단독 폴백(경고 로그).
            if (IncludeSfx)
            {
                var cues = rawCues ?? Array.Empty<Pluto.PlutoSfxCue>();
                if (dsp.Beats is { Count: > 0 } beats) cues = Pluto.Dsp.PlutoBeatSyncQuantizer.Shared.QuantizeSfxCues(cues, beats);
                sfxFiles = cues.Count > 0 ? TryWriteSfxCueWavs(cues) : null;
                if (sfxFiles is not null)
                {
                    var sfxErr = await TryFfmpegAsync(BuildBabyBgmSfxMixArgs(primaryPath, bgm, sfxFiles, cues, duration, temp, masteredBgm is not null), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
                    if (sfxErr is null)
                    {
                        await SwapIntoPlaceAsync(temp, primaryPath, ct).ConfigureAwait(false);
                        return cues.Count;
                    }
                    Program.Log("StockSuite.BabySfx", new IOException("⚠ SFX 믹싱 실패 → BGM 단독 폴백: " + sfxErr));
                    TryDelete(temp);
                }
            }
            // 🎛️ 마스터링 WAV → loudnorm/volume 우회(AAC 직행) · 마스터링 OFF/실패 → 레거시 FFmpeg 정규화 폴백.
            var err = await TryFfmpegAsync(BuildBabyBgmMuxArgs(primaryPath, bgm, duration, temp, masteredBgm is not null), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
            if (err is not null) { Program.Log("StockSuite.BabyBgm", new IOException(err)); TryDelete(temp); return -1; }
            await SwapIntoPlaceAsync(temp, primaryPath, ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { TryDelete(temp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.BabyBgm", ex);
            TryDelete(temp);
            return -1;
        }
        finally
        {
            // 🧹 성공 시 temp 는 이미 교체(Move)되어 부재 — 비IO 예외(Win32·InvalidOperation 등) 경로에서도 잔여 temp 확정 정리.
            TryDelete(temp);
            if (masteredBgm is not null) TryDelete(masteredBgm);
            if (sfxFiles is not null)
                for (var i = 0; i < sfxFiles.Length; i++) TryDelete(sfxFiles[i]);
        }
    }

    // ── 🥁🎛️ 플루토 비트 동기 · 트루피크 마스터링 ──
    /// <summary>🥁 BGM 비트 그리드로 SFX 큐 · 모션 막 경계 자석 스냅(검출 실패 = 폴백 격자 → 스냅 생략).</summary>
    public bool BeatSyncEnabled { get; set; } = true;
    /// <summary>🎛️ BGM 을 FFmpeg 투입 전 C# 트루피크 마스터링(−14 LUFS · ≤ −1 dBTP · PCM16). 실패 → 원본 BGM.</summary>
    public bool TruePeakMasteringEnabled { get; set; } = true;
    /// <summary>🎚️ 마스터링 BGM 에 SFX 큐 구간 사이드체인 더킹(−4 dB · 25ms/180ms) — 마스터링 ON 경로에서만 적용.</summary>
    public bool SidechainDuckingEnabled { get; set; } = true;
    /// <summary>🎛️ 마스터링 임시 WAV 폴더(null = %TEMP%\PromptFlowStudio\master).</summary>
    public string? MasteringTempDir { get; set; }

    /// <summary>
    /// 🎛️🥁 BGM 1회 로드 → (비트 그리드) + (트루피크 마스터링 임시 WAV). 백그라운드 스레드 실행 · 실패/비정상 WAV → (null, null)(검증된 경로 그대로).
    /// 마스터링 WAV 는 호출자가 finally 에서 정리.
    /// </summary>
    internal async Task<(string? MasteredPath, IReadOnlyList<double>? Beats)> PrepareBgmDspAsync(string bgmPath, bool wantBeats, CancellationToken ct, IReadOnlyList<Pluto.PlutoSfxCue>? duckCues = null)
    {
        var master = TruePeakMasteringEnabled;
        if ((!master && !wantBeats) || string.IsNullOrWhiteSpace(bgmPath)) return (null, null);
        var dir = string.IsNullOrWhiteSpace(MasteringTempDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "master") : MasteringTempDir;
        try
        {
            return await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(bgmPath);
                IReadOnlyList<double>? beats = null;
                if (wantBeats && Pluto.Dsp.PlutoAudioBeatDetector.Shared.Analyze(bytes) is { IsFallback: false } grid) beats = grid.Beats;
                ct.ThrowIfCancellationRequested();
                // 🎚️ 더킹 구간 = 믹스에 실제 투입될 비트 스냅 후 큐(호출측 QuantizeSfxCues 와 동일 결정론 결과).
                if (duckCues is { Count: > 0 } && beats is { Count: > 0 }) duckCues = Pluto.Dsp.PlutoBeatSyncQuantizer.Shared.QuantizeSfxCues(duckCues, beats);
                if (!master || !Pluto.Dsp.PlutoTruePeakMasteringEngine.TryMasterWavBuffer(bytes, duckCues, out var wav, out _)) return ((string?)null, beats);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "master_" + Guid.NewGuid().ToString("N") + ".wav");
                try { File.WriteAllBytes(path, wav); }
                catch { TryDelete(path); throw; }
                return (path, beats);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Program.Log("StockSuite.BabyBgm.Dsp", ex);
            return (null, null);
        }
    }

    /// <summary>🥁 모션 막 경계 비트 스냅 — BGM 해석 → 비트 그리드 → <see cref="Pluto.Dsp.PlutoBeatSyncQuantizer.QuantizeTrajectory"/>. 실패 → 원본 궤적.</summary>
    internal async Task<Pluto.PlutoMotionTrajectory> TryBeatSyncTrajectoryAsync(Pluto.PlutoMotionTrajectory trajectory, BabyBgmTrack track, CancellationToken ct)
    {
        if (!BeatSyncEnabled) return trajectory;
        try
        {
            var bgm = BabyBgmResolver(track, trajectory.DurationSec);
            if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return trajectory;
            return await Task.Run(() =>
            {
                var grid = Pluto.Dsp.PlutoAudioBeatDetector.Shared.Analyze(File.ReadAllBytes(bgm));
                return grid.IsFallback ? trajectory : Pluto.Dsp.PlutoBeatSyncQuantizer.Shared.QuantizeTrajectory(trajectory, grid.Beats);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Program.Log("StockSuite.Motion.BeatSync", ex);
            return trajectory;
        }
    }

    // ── 🔔 플루토 타임라인 SFX ──
    public const string SfxTag = "🔔 감성 효과음 · ";
    /// <summary>🔔 SFX 마스터 리미터 상한(≈ −1 dBFS) — amix normalize=0 합산 클리핑 방지.</summary>
    public const string SfxLimiter = "alimiter=limit=0.89:level=0";
    private const string SfxAudioFormat = "aformat=sample_rates=48000:channel_layouts=stereo";

    /// <summary>🔔 감성 효과음 믹스 토글(BGM ON 시에만 적용 · 기본 OFF — UI 가 매 내보내기마다 결선).</summary>
    public bool IncludeSfx { get; set; }

    /// <summary>🔔 큐 시트 공급자(영상 길이 초 → 큐) — 기본 <see cref="Pluto.PlutoSfxDirector.Shared"/>. 테스트 주입용.</summary>
    public Func<double, IReadOnlyList<Pluto.PlutoSfxCue>> SfxCueSheetProvider { get; set; } = static d => Pluto.PlutoSfxDirector.Shared.BuildCueSheet(d);

    /// <summary>🔔 SFX 임시 WAV 폴더(null = %TEMP%\PromptFlowStudio\sfx).</summary>
    public string? SfxTempDir { get; set; }

    private IReadOnlyList<Pluto.PlutoSfxCue> ResolveSfxCues(double durationSec)
    {
        try { return SfxCueSheetProvider(durationSec) ?? Array.Empty<Pluto.PlutoSfxCue>(); }
        catch (Exception ex) { Program.Log("StockSuite.BabySfx.Cues", ex); return Array.Empty<Pluto.PlutoSfxCue>(); }
    }

    /// <summary>🔔 큐별 절차적 SFX → 임시 WAV(큐 순서). 실패 시 작성분 정리 후 null(BGM 단독 폴백).</summary>
    internal string[]? TryWriteSfxCueWavs(IReadOnlyList<Pluto.PlutoSfxCue> cues)
    {
        var dir = string.IsNullOrWhiteSpace(SfxTempDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "sfx") : SfxTempDir;
        var paths = new string[cues.Count];
        var written = 0;
        var ok = false;
        try
        {
            Directory.CreateDirectory(dir);
            var stamp = Guid.NewGuid().ToString("N");
            for (var i = 0; i < cues.Count; i++)
            {
                var p = Path.Combine(dir, "sfx_" + stamp + "_" + i.ToString(CultureInfo.InvariantCulture) + "_" + cues[i].SfxType.ToString().ToLowerInvariant() + ".wav");
                File.WriteAllBytes(p, Pluto.PlutoAudioSynthesizer.Generate(cues[i].SfxType));
                paths[written++] = p;
            }
            ok = true;
            return paths;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.BabySfx", new IOException("⚠ SFX 합성 실패 → BGM 단독 폴백", ex));
            return null;
        }
        finally
        {
            // 🧹 필터 외 예외(합성기 내부 오류 등)로 빠져나가도 작성분 정리.
            if (!ok)
                for (var i = 0; i < written; i++) TryDelete(paths[i]);
        }
    }

    /// <summary>
    /// 🔔 BGM + SFX 멀티트랙 필터그래프 — 입력 1 = BGM(loudnorm −14 LUFS · 0.25 · 페이드), 2.. = SFX(볼륨 → <c>adelay=ms|ms</c>).
    /// <c>amix=inputs=N:normalize=0</c>(BGM 레벨 보존) → <see cref="SfxLimiter"/> → <c>[aout]</c>.
    /// </summary>
    public static string BuildBabyBgmSfxFilterGraph(double durationSec, IReadOnlyList<Pluto.PlutoSfxCue> cues, bool mastered = false)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256 + cues.Count * 96);
        sb.Append("[1:a]").Append(BuildBabyBgmAudioFilter(durationSec, mastered)).Append(',').Append(SfxAudioFormat).Append("[bgm];");
        for (var i = 0; i < cues.Count; i++)
        {
            var ms = Math.Max(0, cues[i].OffsetMs).ToString(inv);
            var vol = Math.Clamp(double.IsFinite(cues[i].VolumeRatio) ? cues[i].VolumeRatio : 0, 0, 1).ToString("0.###", inv);
            sb.Append("[").Append((i + 2).ToString(inv)).Append(":a]").Append(SfxAudioFormat)
              .Append(",volume=").Append(vol)
              .Append(",adelay=").Append(ms).Append('|').Append(ms)
              .Append("[d").Append(i.ToString(inv)).Append("];");
        }
        sb.Append("[bgm]");
        for (var i = 0; i < cues.Count; i++) sb.Append("[d").Append(i.ToString(inv)).Append(']');
        sb.Append("amix=inputs=").Append((cues.Count + 1).ToString(inv)).Append(":duration=first:dropout_transition=0:normalize=0,")
          .Append(SfxLimiter).Append("[aout]");
        return sb.ToString();
    }

    /// <summary>🔔 BGM + SFX 먹싱 인자 — 비디오 copy · BGM 루프 · SFX 입력 N개 · filter_complex → AAC · -shortest.</summary>
    public static string[] BuildBabyBgmSfxMixArgs(string videoPath, string bgmPath, IReadOnlyList<string> sfxPaths, IReadOnlyList<Pluto.PlutoSfxCue> cues, double durationSec, string outputPath, bool mastered = false)
    {
        if (sfxPaths.Count != cues.Count) throw new ArgumentException("SFX 경로 수와 큐 수 불일치", nameof(sfxPaths));
        var a = new List<string>(40 + sfxPaths.Count * 2)
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-stream_loop", "-1", "-i", bgmPath,
        };
        for (var i = 0; i < sfxPaths.Count; i++) { a.Add("-i"); a.Add(sfxPaths[i]); }
        a.Add("-filter_complex"); a.Add(BuildBabyBgmSfxFilterGraph(durationSec, cues, mastered));
        a.Add("-map"); a.Add("0:v:0"); a.Add("-map"); a.Add("[aout]");
        a.Add("-c:v"); a.Add("copy");
        a.Add("-c:a"); a.Add(videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus" : "aac");
        a.Add("-b:a"); a.Add(AudioMuxService.AudioBitrate);
        a.Add("-ar"); a.Add(AudioMuxService.OutputSampleRate);
        a.Add("-shortest");
        if (double.IsFinite(durationSec) && durationSec > 0) { a.Add("-t"); a.Add(durationSec.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (videoPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { a.Add("-movflags"); a.Add("+faststart"); a.Add("-f"); a.Add("mp4"); }
        else if (videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) { a.Add("-f"); a.Add("webm"); }
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>🏷️ 단계 기본 D-day — 신생아 D+30 · 백일 D+100 · 첫돌 D+365 · 그 외 오늘 날짜.</summary>
    public static string DefaultGrowthDday(BabyGrowthStage? stage, DateTime today) => stage switch
    {
        BabyGrowthStage.Newborn => "D+30",
        BabyGrowthStage.HundredDays => "D+100",
        BabyGrowthStage.FirstBirthday => "D+365",
        _ => today.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
    };

    /// <summary>🏷️ 성장 뱃지 텍스트 — <c>{이모지} {단계명} · {사용자 D-day 또는 기본}</c>. 단계 미선택 → <c>🏷️ 우리 아기 성장 기록 · …</c>.</summary>
    public static string FormatGrowthBadgeText(BabyGrowthStage? stage, string? customDday, DateTime today)
    {
        var head = stage is { } s ? GeminiPromptService.GrowthStageOf(s).Emoji + " " + GeminiPromptService.GrowthStageOf(s).Label : "🏷️ 우리 아기 성장 기록";
        var tail = string.IsNullOrWhiteSpace(customDday) ? DefaultGrowthDday(stage, today) : customDday.Trim();
        return head + " · " + tail;
    }

    /// <summary>drawtext 미지원 이모지(서로게이트·기호·변형 선택자) 제거 + 공백 정리.</summary>
    public static string StripEmoji(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsSurrogate(ch) || ch is '️' or '‍' || (ch >= '☀' && ch <= '➿')) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>🏷️ 좌하단 흰색 박스 drawtext(패딩 · 진한 글자) — 빈 텍스트 → null. 4K 는 2배 스케일.</summary>
    public static string? BuildGrowthBadgeFilter(string? text, string? fontFile, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = StripEmoji(text);
        if (clean.Length == 0) return null;
        var k = resolution == StockSuiteResolution.Uhd4K ? 2 : 1;
        var inv = CultureInfo.InvariantCulture;
        var font = string.IsNullOrWhiteSpace(fontFile) ? "" : "fontfile='" + fontFile.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal) + "':";
        var m = (48 * k).ToString(inv);
        return "drawtext=" + font + "text='" + EscapeDrawText(clean) + "':fontsize=" + (30 * k).ToString(inv)
            + ":fontcolor=0x3A2E39:box=1:boxcolor=white@0.88:boxborderw=" + (18 * k).ToString(inv)
            + ":x=" + m + ":y=h-th-" + m;
    }

    internal static string? AppendGrowthBadge(string? baseFilter, string? text, string? fontFile, StockSuiteResolution resolution)
    {
        var badge = BuildGrowthBadgeFilter(text, fontFile, resolution);
        if (badge is null) return baseFilter;
        return baseFilter is null ? badge : baseFilter + "," + badge;
    }

    // ── 💌 플루토 감성 편지 자막 번인 ──
    public const string SubtitleTag = "💌 감성 자막 · ";

    /// <summary>💌 시간 동기 감성 자막 번인 스위치(true + 유효 큐 → drawtext enable 체인 + <c>.srt</c> 동반 기록).</summary>
    public bool BurnSubtitles { get; set; }

    /// <summary>💌 번인 대상 자막 큐(null/빈 → 생략).</summary>
    public IReadOnlyList<SubtitleCue>? SubtitleCues { get; set; }

    public bool IsSubtitleBurnEnabled => BurnSubtitles && BuildTimedSubtitleFilter(SubtitleCues, null) is not null;

    /// <summary>💌 작업실 단일 내보내기 — 자막 스위치/큐를 이 호출 범위로 한정(공유 싱글톤 누수 차단). <paramref name="cues"/> null/빈 → 자막 없이 내보내기.</summary>
    public async Task<StockSuiteExportResult> ExportWithSubtitlesAsync(string? sourcePath, IReadOnlyList<SubtitleCue>? cues, CancellationToken ct = default)
    {
        BurnSubtitles = cues is { Count: > 0 };
        SubtitleCues = BurnSubtitles ? cues : null;
        try { return await ExportAsync(sourcePath, null, null, ct).ConfigureAwait(false); }
        finally { BurnSubtitles = false; SubtitleCues = null; }
    }

    /// <summary>
    /// 💌 하단 중앙 반투명 박스 drawtext — 큐별 <c>enable='between(t,s,e)'</c> 시간 게이트. 유효 큐 없음 → null. 4K 는 2배 스케일.
    /// 단일 언어: 흰 글자 24pt · <c>y=h-120</c>. 이중 언어(<see cref="SubtitleCue.SecondaryText"/>): KO 웜 크림 24pt <c>y=h-164</c>(주) + EN 반투명 흰 16pt <c>y=h-120</c>(보조).
    /// </summary>
    public static string? BuildTimedSubtitleFilter(IReadOnlyList<SubtitleCue>? cues, string? fontFile, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080)
    {
        if (cues is null || cues.Count == 0) return null;
        var k = resolution == StockSuiteResolution.Uhd4K ? 2 : 1;
        var inv = CultureInfo.InvariantCulture;
        var font = string.IsNullOrWhiteSpace(fontFile) ? "" : "fontfile='" + fontFile.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal) + "':";
        var center = ":x=(w-text_w)/2:y=h-";
        var mono = ":fontcolor=white:box=1:boxcolor=black@0.4:boxborderw=" + (10 * k).ToString(inv)
            + center + (120 * k).ToString(inv);
        var primary = ":fontcolor=0xFFF1D6:box=1:boxcolor=black@0.45:boxborderw=" + (10 * k).ToString(inv)
            + center + (164 * k).ToString(inv);
        var secondary = ":fontcolor=white@0.85:box=1:boxcolor=black@0.3:boxborderw=" + (8 * k).ToString(inv)
            + center + (120 * k).ToString(inv);
        var sb = new StringBuilder(384 * cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EffectiveEndSec <= c.EffectiveStartSec || c.EffectiveStartSec < 0) continue;
            var clean = FitSubtitleText(CleanSubtitleLine(c.Text), SubtitlePrimaryFontSize);
            if (clean.Length == 0) continue;
            var en = FitSubtitleText(CleanSubtitleLine(c.SecondaryText), SubtitleSecondaryFontSize);
            var gate = "':enable='between(t," + c.EffectiveStartSec.ToString("0.###", inv) + "," + c.EffectiveEndSec.ToString("0.###", inv) + ")'";
            if (sb.Length > 0) sb.Append(',');
            sb.Append("drawtext=").Append(font).Append("text='").Append(EscapeDrawText(clean)).Append(gate)
              .Append(":fontsize=").Append((FitSubtitleFontSize(clean, SubtitlePrimaryFontSize) * k).ToString(inv))
              .Append(en.Length == 0 ? mono : primary);
            if (en.Length > 0)
                sb.Append(",drawtext=").Append(font).Append("text='").Append(EscapeDrawText(en)).Append(gate)
                  .Append(":fontsize=").Append((FitSubtitleFontSize(en, SubtitleSecondaryFontSize) * k).ToString(inv))
                  .Append(secondary);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string CleanSubtitleLine(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : StripEmoji(s.Replace('\r', ' ').Replace('\n', ' '));

    // ── 💌 자막 클리핑 가드 — 1080px 폭 기준 한 줄 수용 한계(한글 30자 · 라틴 65자) 초과 시 폰트 비례 축소(하한 보장) ──
    public const int SubtitlePrimaryFontSize = 24;
    public const int SubtitleSecondaryFontSize = 16;
    public const int SubtitleKoFitChars = 30;
    public const int SubtitleEnFitChars = 65;
    /// <summary>💌 축소 하한(1080 기준 pt) — 이하로는 가독성 붕괴 → 대신 말줄임.</summary>
    public const int SubtitleMinFontSize = 11;

    private static bool HasHangulOrCjk(string s)
    {
        foreach (var ch in s)
            if (ch is >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' or >= '가' and <= '힯' or >= '一' and <= '鿿') return true;
        return false;
    }

    /// <summary>💌 줄 길이 기반 폰트 크기(1080 기준) — 한계 이내 → 기본값 · 초과 → <c>base·limit/len</c>(하한 <see cref="SubtitleMinFontSize"/>).</summary>
    public static int FitSubtitleFontSize(string line, int baseSize)
    {
        if (string.IsNullOrEmpty(line) || baseSize <= SubtitleMinFontSize) return baseSize;
        var limit = HasHangulOrCjk(line) ? SubtitleKoFitChars : SubtitleEnFitChars;
        if (line.Length <= limit) return baseSize;
        return Math.Max(SubtitleMinFontSize, baseSize * limit / line.Length);
    }

    /// <summary>💌 하한 폰트로도 폭 초과하는 극단 길이 → 단어 경계 말줄임(<c>...</c>)으로 프레임 이탈 차단.</summary>
    public static string FitSubtitleText(string line, int baseSize)
    {
        if (string.IsNullOrEmpty(line) || baseSize <= SubtitleMinFontSize) return line;
        var limit = HasHangulOrCjk(line) ? SubtitleKoFitChars : SubtitleEnFitChars;
        var cap = limit * baseSize / SubtitleMinFontSize;
        if (line.Length <= cap) return line;
        var cut = line.LastIndexOf(' ', cap - 3);
        if (cut < cap / 2) cut = cap - 3;
        return line[..cut].TrimEnd() + "...";
    }

    internal static string? AppendTimedSubtitles(string? baseFilter, IReadOnlyList<SubtitleCue>? cues, string? fontFile, StockSuiteResolution resolution)
    {
        var subs = BuildTimedSubtitleFilter(cues, fontFile, resolution);
        if (subs is null) return baseFilter;
        return baseFilter is null ? subs : baseFilter + "," + subs;
    }

    /// <summary>💌 표준 SRT 직렬화(1-based 번호 · <c>HH:mm:ss,fff</c> · 보조 자막(EN) 있으면 2번째 줄). 유효 큐 없음 → 빈 문자열.</summary>
    public static string BuildSrt(IReadOnlyList<SubtitleCue>? cues)
    {
        if (cues is null || cues.Count == 0) return "";
        var sb = new StringBuilder(128 * cues.Count);
        var n = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EffectiveEndSec <= c.EffectiveStartSec || c.EffectiveStartSec < 0) continue;
            if (n > 0) sb.Append('\n');
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendSrtTime(sb, c.EffectiveStartSec).Append(" --> ");
            AppendSrtTime(sb, c.EffectiveEndSec).Append('\n');
            sb.Append(c.Text.Replace("\r", "", StringComparison.Ordinal).Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(c.SecondaryText))
                sb.Append(c.SecondaryText.Replace('\r', ' ').Replace('\n', ' ').Trim()).Append('\n');
        }
        return sb.ToString();
    }

    private static StringBuilder AppendSrtTime(StringBuilder sb, double sec)
    {
        var inv = CultureInfo.InvariantCulture;
        var ms = (long)Math.Round(Math.Max(0, sec) * 1000.0);
        var totalSec = ms / 1000;
        return sb.Append((totalSec / 3600).ToString("00", inv)).Append(':')
                 .Append((totalSec / 60 % 60).ToString("00", inv)).Append(':')
                 .Append((totalSec % 60).ToString("00", inv)).Append(',')
                 .Append((ms % 1000).ToString("000", inv));
    }

    /// <summary>💌 <c>{출력명}.srt</c> 동반 기록(외부 플레이어용 · 원자적 · 실패 격리). 성공 시 경로.</summary>
    internal string? TryWriteSubtitleSidecar(string targetPath)
    {
        var srt = BuildSrt(SubtitleCues);
        if (srt.Length == 0) return null;
        var path = Path.ChangeExtension(targetPath, ".srt");
        var tmp = path + TempExt;
        try
        {
            File.WriteAllText(tmp, srt, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.SubtitleSrt", ex);
            return null;
        }
        finally
        {
            TryDelete(tmp);   // 🧹 성공 시 Move 로 부재 · 실패/비IO 예외 시 잔여 .srt.tmp 정리
        }
    }

    // ══════════════════════ 🎥 Ken Burns 모션 영상(사진 1장 → 15초 60fps) ══════════════════════

    public const string MotionTag = "🎥 Ken Burns 모션 · ";
    /// <summary>모션 영상 기본 출력 폴더(<c>%TEMP%</c> 하위).</summary>
    public const string MotionOutputFolder = "motion";
    public const string MotionFileSuffix = "_kenburns.mp4";

    /// <summary>
    /// 🎥 정지 이미지 → zoompan 모션 MP4 인자 — 입력 1프레임 · <c>-frames:v</c>/<c>-t</c> 로 길이 고정 · libx264 crf18 · 무음(오디오는 후속 먹싱).
    /// </summary>
    public static string[] BuildMotionVideoArgs(string imagePath, string videoFilter, int frames, int fps, string outputPath)
    {
        var inv = CultureInfo.InvariantCulture;
        return
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", imagePath,
            "-vf", videoFilter,
            "-frames:v", frames.ToString(inv), "-r", fps.ToString(inv),
            "-t", ((double)frames / fps).ToString("0.###", inv),
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
            "-an", "-sn", "-dn", "-movflags", "+faststart", "-f", "mp4", outputPath,
        ];
    }

    /// <summary>
    /// 🎥 사진 1장 → 15초 60fps Ken Burns MP4 — <see cref="Pluto.PlutoMotionCompiler.CompileFfmpegZoomPanFilter"/> 필터그래프 +
    /// 이중 언어 자막 번인(+ <c>.srt</c>) → BGM(−14 LUFS) + 절차적 SFX(셔터 · 차임 · 자장가 톤) 먹싱까지 단일 산출물.
    /// [부분 구제] 자막 번인 실패 → 자막 없이 재시도 · 오디오 먹싱 실패 → 무음 영상 보존(둘 다 Ok=true + ⚠ 태그).
    /// </summary>
    /// <param name="options">해상도(<see cref="StockSuiteResolution.Uhd4K"/> = 2160×3840 · 그 외 1080×1920) · 출력 폴더.</param>
    /// <param name="subtitles">자막 큐(null → <see cref="BurnSubtitles"/> ON 일 때 <see cref="SubtitleCues"/>).</param>
    /// <param name="bgm">BGM 트랙(null → <see cref="BabyBgm"/> → 자장가).</param>
    /// <param name="includeSfx">SFX 믹스(null → ON).</param>
    /// <param name="outputPath">출력 MP4 경로(null → <c>{출력 폴더}/{이미지명}_kenburns.mp4</c>).</param>
    /// <param name="customBgmWavBytes">🎵 커스텀 BGM 하이라이트 WAV(null/빈 → 선택 트랙) — 비트 스냅 · 사이드체인 더킹(−4 dB) · 트루피크 마스터링(−14 LUFS / −1 dBTP) 동일 경로.</param>
    public async Task<StockSuiteExportResult> ExportMotionVideoFromPhotoAsync(string imagePath, Pluto.PlutoMotionTrajectory trajectory, ExportOptions options,
        IReadOnlyList<SubtitleCue>? subtitles = null, BabyBgmTrack? bgm = null, bool? includeSfx = null, string? outputPath = null, CancellationToken ct = default,
        byte[]? customBgmWavBytes = null)
    {
        ct.ThrowIfCancellationRequested();
        var custom = BeginCustomBgm(customBgmWavBytes);
        try { return await ExportMotionVideoCoreAsync(imagePath, trajectory, options, subtitles, bgm, includeSfx, outputPath, ct).ConfigureAwait(false); }
        finally { EndCustomBgm(custom); }
    }

    private async Task<StockSuiteExportResult> ExportMotionVideoCoreAsync(string imagePath, Pluto.PlutoMotionTrajectory trajectory, ExportOptions options,
        IReadOnlyList<SubtitleCue>? subtitles, BabyBgmTrack? bgm, bool? includeSfx, string? outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return new StockSuiteExportResult(false, null, false, "원본 사진을 찾을 수 없습니다.");
        if (options is null) return new StockSuiteExportResult(false, null, false, "옵션이 없습니다.");
        trajectory ??= Pluto.PlutoMotionTrajectory.Empty;
        if (Pluto.PlutoMotionCompiler.TotalFrames(trajectory) == 0)
            trajectory = Pluto.PlutoMotionCompiler.SynthesizeFourAct(Pluto.PlutoMotionCompiler.BabyFocal);   // 빈 궤적 → 기본 4막
        trajectory = await TryBeatSyncTrajectoryAsync(trajectory, bgm ?? BabyBgm ?? BabyBgmTrack.Lullaby, ct).ConfigureAwait(false);   // 🥁 막 경계 → BGM 비트

        var uhd = options.Resolution == StockSuiteResolution.Uhd4K;
        int width = uhd ? 2160 : Pluto.PlutoMotionCompiler.DefaultWidth, height = uhd ? 3840 : Pluto.PlutoMotionCompiler.DefaultHeight;
        const int fps = Pluto.PlutoMotionCompiler.DefaultFps;
        var frames = Pluto.PlutoMotionCompiler.TotalFrames(trajectory, fps);
        var motion = Pluto.PlutoMotionCompiler.CompileFfmpegZoomPanFilter(trajectory, width, height, fps, uhd ? 2 : Pluto.PlutoMotionCompiler.DefaultSupersample);

        string target;
        try
        {
            target = Path.GetFullPath(string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", MotionOutputFolder) : options.OutputDirectory,
                    Path.GetFileNameWithoutExtension(imagePath) + MotionFileSuffix)
                : outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Motion.Path", ex);
            return new StockSuiteExportResult(false, null, false, "출력 경로를 만들 수 없습니다: " + ex.Message);
        }

        var cues = subtitles ?? (BurnSubtitles ? SubtitleCues : null);
        var subsOn = BuildTimedSubtitleFilter(cues, null, options.Resolution) is not null;
        var src = Path.GetFullPath(imagePath);
        var temp = target + TempExt;
        var tag = MotionTag + frames.ToString(CultureInfo.InvariantCulture) + "프레임 @" + fps.ToString(CultureInfo.InvariantCulture) + "fps · ";
        var prevBgm = BabyBgm;
        var prevSfx = IncludeSfx;
        var prevCues = SubtitleCues;
        try
        {
            string? err = null;
            if (subsOn)
            {
                var burned = AppendTimedSubtitles(motion, cues, ResolveTickerBadgeFont(), options.Resolution)!;
                err = await TryFfmpegAsync(BuildMotionVideoArgs(src, burned, frames, fps, temp), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
                if (err is null) tag += SubtitleTag;
                else
                {
                    Program.Log("StockSuite.Motion.Subtitle", new IOException("⚠ 자막 번인 실패 → 자막 없이 재시도: " + err));
                    TryDelete(temp);
                }
            }
            if (!subsOn || err is not null)
            {
                err = await TryFfmpegAsync(BuildMotionVideoArgs(src, motion, frames, fps, temp), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
                if (err is not null) { TryDelete(temp); return new StockSuiteExportResult(false, null, false, "모션 영상 렌더 실패: " + err); }
                if (subsOn) tag += "⚠ 자막 생략 · ";
            }
            await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);

            if (subsOn) { SubtitleCues = cues; TryWriteSubtitleSidecar(target); }

            // 🎵🔔 BGM(−14 LUFS) + SFX — 검증된 먹싱 코어 재사용(실패 시 무음 영상 보존).
            BabyBgm = bgm ?? prevBgm ?? BabyBgmTrack.Lullaby;
            IncludeSfx = includeSfx ?? true;
            var mixed = await MuxBabyAudioIntoPrimaryAsync(target, ct).ConfigureAwait(false);
            tag += mixed < 0
                ? "⚠ 오디오 생략 · 🔇 무음"
                : "🔊 BGM · " + BabyBgmTag + ActiveBgmLabel(BabyBgm.Value) + (mixed > 0 ? " · " + SfxTag + mixed.ToString(CultureInfo.InvariantCulture) + "큐" : "");
            return new StockSuiteExportResult(true, target, false, tag) { HasAudio = mixed >= 0 };
        }
        catch (OperationCanceledException) { TryDelete(temp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.Motion", ex);
            return new StockSuiteExportResult(false, null, false, "모션 영상 내보내기 실패: " + ex.Message);
        }
        finally
        {
            TryDelete(temp);
            BabyBgm = prevBgm;
            IncludeSfx = prevSfx;
            SubtitleCues = prevCues;
        }
    }

    // ── 🎵 커스텀 BGM(사용자 업로드 하이라이트) ──
    public const string CustomBgmLabel = "🎧 커스텀 BGM";
    private bool _customBgmActive;

    /// <summary>🎵 진행 중 내보내기 BGM 라벨(커스텀 활성 → <see cref="CustomBgmLabel"/>).</summary>
    private string ActiveBgmLabel(BabyBgmTrack track) => _customBgmActive ? CustomBgmLabel : BabyBgmLabel(track);

    private sealed record CustomBgmScope(string Path, Func<BabyBgmTrack, double, string?> PrevResolver, bool PrevActive);

    /// <summary>
    /// 🎵 커스텀 WAV → 임시 파일 → <see cref="BabyBgmResolver"/> 일시 교체(비트 스냅 · 마스터링 · 더킹 · 먹싱 전 경로가 동일 파일 사용).
    /// null/빈/비정상 WAV → null(선택 트랙 유지 · 경고 로그).
    /// </summary>
    private CustomBgmScope? BeginCustomBgm(byte[]? wav)
    {
        if (wav is not { Length: > 0 }) return null;
        if (!Pluto.Dsp.PlutoPcmWav.TryReadHeader(wav, out _))
        {
            Program.Log("StockSuite.CustomBgm", new InvalidDataException("⚠ 커스텀 BGM WAV 헤더 무효 → 기본 트랙 폴백"));
            return null;
        }
        var dir = string.IsNullOrWhiteSpace(MasteringTempDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "master") : MasteringTempDir;
        var path = Path.Combine(dir, "custom_bgm_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, wav);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.CustomBgm", ex);
            TryDelete(path);
            return null;
        }
        var scope = new CustomBgmScope(path, BabyBgmResolver, _customBgmActive);
        BabyBgmResolver = (_, _) => path;
        _customBgmActive = true;
        return scope;
    }

    private void EndCustomBgm(CustomBgmScope? scope)
    {
        if (scope is null) return;
        BabyBgmResolver = scope.PrevResolver;
        _customBgmActive = scope.PrevActive;
        TryDelete(scope.Path);
    }

    // ══════════════════════ 🎞️ 멀티컷 몽타주(사진 2~5장 → 15초 60fps · 자동 색보정 · 비트 xfade) ══════════════════════

    public const string MontageTag = "🎞️ 멀티컷 몽타주 · ";
    public const string MontageFileSuffix = "_montage.mp4";
    /// <summary>자막 번인 전 xfade 체인 중간 패드.</summary>
    internal const string MontageMainPad = "vmain";

    private int _montageExporting;
    /// <summary>🔒 몽타주 렌더 진행 중(동시 실행 차단 — 익스포터 공유 상태 BabyBgm/IncludeSfx/SubtitleCues 보호).</summary>
    public bool IsMontageExporting => Volatile.Read(ref _montageExporting) != 0;

    /// <summary>🎨 색 분석기(테스트 주입용 · 기본 <see cref="Pluto.PlutoImageColorAnalyzer.Shared"/>).</summary>
    public Pluto.IPlutoImageColorAnalyzer ColorAnalyzer { get; set; } = Pluto.PlutoImageColorAnalyzer.Shared;
    /// <summary>🎞️ 몽타주 컴파일러(기본 <see cref="Pluto.PlutoMontageCompiler.Shared"/>).</summary>
    public Pluto.IPlutoMontageCompiler MontageCompiler { get; set; } = Pluto.PlutoMontageCompiler.Shared;
    /// <summary>🛡️ 오답 노트(차단 룩 → NaturalPastel 폴백 · null = 미적용).</summary>
    public Pluto.IPlutoMistakeNotebookService? MistakeNotebook { get; set; }
    /// <summary>마지막 성공 몽타주 플랜(UI 배지 · 진단).</summary>
    public Pluto.PlutoMontagePlan? LastMontagePlan { get; private set; }

    /// <summary>🎞️ 다중 입력 몽타주 인자 — 사진별 <c>-i</c> · <c>-filter_complex</c> · <c>-map [pad]</c> · 프레임/길이 고정 · libx264 crf18 · 무음.</summary>
    public static string[] BuildMontageVideoArgs(IReadOnlyList<string> imagePaths, string filterComplex, string finalPad, int frames, int fps, string outputPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string>(32 + imagePaths.Count * 2) { "-y", "-hide_banner", "-loglevel", "error" };
        for (var i = 0; i < imagePaths.Count; i++) { a.Add("-i"); a.Add(imagePaths[i]); }
        a.Add("-filter_complex"); a.Add(filterComplex);
        a.Add("-map"); a.Add("[" + finalPad + "]");
        a.Add("-frames:v"); a.Add(frames.ToString(inv));
        a.Add("-r"); a.Add(fps.ToString(inv));
        a.Add("-t"); a.Add(((double)frames / fps).ToString("0.###", inv));
        a.Add("-c:v"); a.Add("libx264"); a.Add("-preset"); a.Add("medium"); a.Add("-crf"); a.Add("18"); a.Add("-pix_fmt"); a.Add("yuv420p");
        a.Add("-an"); a.Add("-sn"); a.Add("-dn"); a.Add("-movflags"); a.Add("+faststart"); a.Add("-f"); a.Add("mp4");
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>💌 몽타주 그래프 말미에 자막 번인 — 최종 패드 → <c>[vmain];[vmain]drawtext…[pad]</c>. 유효 큐 없음 → 원본 그래프.</summary>
    public static string AppendMontageSubtitles(string filterComplex, string finalPad, IReadOnlyList<SubtitleCue>? cues, string? fontFile, StockSuiteResolution resolution)
    {
        var subs = BuildTimedSubtitleFilter(cues, fontFile, resolution);
        var tail = "[" + finalPad + "]";
        if (subs is null || !filterComplex.EndsWith(tail, StringComparison.Ordinal)) return filterComplex;
        return filterComplex[..^tail.Length] + "[" + MontageMainPad + "];[" + MontageMainPad + "]" + subs + tail;
    }

    /// <summary>🥁 BGM 비트 그리드(검출 실패/폴백 격자/OFF → null · 취소만 전파).</summary>
    internal async Task<IReadOnlyList<double>?> TryDetectBgmBeatsAsync(BabyBgmTrack track, double durationSec, CancellationToken ct)
    {
        if (!BeatSyncEnabled) return null;
        try
        {
            var bgm = BabyBgmResolver(track, durationSec);
            if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return null;
            return await Task.Run(() =>
            {
                var grid = Pluto.Dsp.PlutoAudioBeatDetector.Shared.Analyze(File.ReadAllBytes(bgm));
                return grid.IsFallback ? null : grid.Beats;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Program.Log("StockSuite.Montage.Beats", ex);
            return null;
        }
    }

    /// <summary>
    /// 🎞️ 사진 2~5장 → 15초 60fps 세로 몽타주 MP4 — 사진별 색 분석 → 노출 정합 + 시네마틱 룩(오답 노트 차단 → NaturalPastel) →
    /// BGM 비트 스냅 컷 · 클립별 Push-In/Drift/Pull-Back zoompan · xfade 체인 → KO/EN 자막 번인 →
    /// 트루피크 마스터링(−14 LUFS · ≤ −1 dBTP) + 사이드체인 더킹 + 비트 스냅 SFX 먹싱(검증된 <see cref="MuxBabyAudioIntoPrimaryAsync"/> 재사용).
    /// [부분 구제] 자막 번인 실패 → 자막 없이 재시도 · 오디오 실패 → 무음 영상 보존. 동시 실행은 즉시 거부.
    /// </summary>
    /// <param name="preferredLook">룩 후보(null → NaturalPastel) — <see cref="Pluto.PlutoColorGradingCompiler.SelectLook"/> 결과 권장.</param>
    public async Task<StockSuiteExportResult> ExportMontageVideoFromPhotosAsync(IReadOnlyList<string> imagePaths, IReadOnlyList<(double X, double Y)>? focalPoints, ExportOptions options,
        IReadOnlyList<SubtitleCue>? subtitles = null, BabyBgmTrack? bgm = null, bool includeSfx = true, string? preferredLook = null, string? outputPath = null, CancellationToken ct = default,
        byte[]? customBgmWavBytes = null)
    {
        ct.ThrowIfCancellationRequested();
        if (options is null) return new StockSuiteExportResult(false, null, false, "옵션이 없습니다.");
        if (imagePaths is null || imagePaths.Count < Pluto.PlutoMontageCompiler.MinClips || imagePaths.Count > Pluto.PlutoMontageCompiler.MaxClips)
            return new StockSuiteExportResult(false, null, false, "몽타주는 사진 2~5장이 필요합니다.");
        var srcs = new string[imagePaths.Count];
        for (var i = 0; i < srcs.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(imagePaths[i]) || !File.Exists(imagePaths[i])) return new StockSuiteExportResult(false, null, false, "원본 사진을 찾을 수 없습니다: " + imagePaths[i]);
            srcs[i] = Path.GetFullPath(imagePaths[i]);
        }
        if (Interlocked.CompareExchange(ref _montageExporting, 1, 0) != 0) return new StockSuiteExportResult(false, null, false, "이미 몽타주 렌더가 진행 중입니다.");

        var prevBgm = BabyBgm;
        var prevSfx = IncludeSfx;
        var prevCues = SubtitleCues;
        string? temp = null;
        CustomBgmScope? custom = null;
        try
        {
            custom = BeginCustomBgm(customBgmWavBytes);   // 🎵 동시 실행 가드 통과 후에만 해석기 교체

            var uhd = options.Resolution == StockSuiteResolution.Uhd4K;
            int width = uhd ? 2160 : Pluto.PlutoMotionCompiler.DefaultWidth, height = uhd ? 3840 : Pluto.PlutoMotionCompiler.DefaultHeight;
            const int fps = Pluto.PlutoMotionCompiler.DefaultFps;
            const double total = Pluto.PlutoMotionCompiler.FourActDurationSec;
            var track = bgm ?? prevBgm ?? BabyBgmTrack.Lullaby;

            // 🎨 색 분석(백그라운드 · 실패 → 중립) → 노출 정합 프로파일
            var analyzer = ColorAnalyzer;
            var metrics = await Task.Run(() =>
            {
                var m = new Pluto.PlutoImageColorMetrics[srcs.Length];
                for (var i = 0; i < m.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { m[i] = analyzer.AnalyzeImage(srcs[i]); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("StockSuite.Montage.Color", ex); m[i] = Pluto.PlutoImageColorMetrics.Neutral; }
                }
                return m;
            }, ct).ConfigureAwait(false);
            var profiles = analyzer.BuildHarmonizedProfiles(metrics, preferredLook ?? Pluto.PlutoColorGradingCompiler.NaturalPastelName, MistakeNotebook);

            // 🥁 BGM 비트 → 컷 스냅 → filter_complex
            var beats = await TryDetectBgmBeatsAsync(track, total, ct).ConfigureAwait(false);
            var plan = MontageCompiler.CompileMontagePlan(srcs, focalPoints, beats, profiles, total, width, height, fps);
            var frames = (int)Math.Round(plan.TotalDurationSec * fps, MidpointRounding.AwayFromZero);

            string target;
            try
            {
                target = Path.GetFullPath(string.IsNullOrWhiteSpace(outputPath)
                    ? Path.Combine(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", MotionOutputFolder) : options.OutputDirectory,
                        Path.GetFileNameWithoutExtension(srcs[0]) + MontageFileSuffix)
                    : outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Program.Log("StockSuite.Montage.Path", ex);
                return new StockSuiteExportResult(false, null, false, "출력 경로를 만들 수 없습니다: " + ex.Message);
            }

            var cues = subtitles ?? (BurnSubtitles ? SubtitleCues : null);
            var subsOn = BuildTimedSubtitleFilter(cues, null, options.Resolution) is not null;
            temp = target + TempExt;
            var look = profiles.Count > 0 ? profiles[0].PresetName : Pluto.PlutoColorGradingCompiler.NaturalPastelName;
            var inv = CultureInfo.InvariantCulture;
            var tag = MontageTag + srcs.Length.ToString(inv) + "컷 · " + frames.ToString(inv) + "프레임 @" + fps.ToString(inv) + "fps · 🎨 " + look
                      + " · 🥁 비트 컷 " + plan.SnappedCuts.ToString(inv) + "/" + (srcs.Length - 1).ToString(inv) + " · ";

            string? err = null;
            if (subsOn)
            {
                var burned = AppendMontageSubtitles(plan.FilterComplexGraph, plan.FinalVideoPadLabel, cues, ResolveTickerBadgeFont(), options.Resolution);
                err = await TryFfmpegAsync(BuildMontageVideoArgs(srcs, burned, plan.FinalVideoPadLabel, frames, fps, temp), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
                if (err is null) tag += SubtitleTag;
                else
                {
                    Program.Log("StockSuite.Montage.Subtitle", new IOException("⚠ 자막 번인 실패 → 자막 없이 재시도: " + err));
                    TryDelete(temp);
                }
            }
            if (!subsOn || err is not null)
            {
                err = await TryFfmpegAsync(BuildMontageVideoArgs(srcs, plan.FilterComplexGraph, plan.FinalVideoPadLabel, frames, fps, temp), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
                if (err is not null) { TryDelete(temp); return new StockSuiteExportResult(false, null, false, "몽타주 렌더 실패: " + err); }
                if (subsOn) tag += "⚠ 자막 생략 · ";
            }
            await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
            LastMontagePlan = plan;

            if (subsOn) { SubtitleCues = cues; TryWriteSubtitleSidecar(target); }

            // 🎵🔔 마스터링 BGM + 사이드체인 더킹 + 비트 스냅 SFX(실패 시 무음 영상 보존).
            BabyBgm = track;
            IncludeSfx = includeSfx;
            var mixed = await MuxBabyAudioIntoPrimaryAsync(target, ct).ConfigureAwait(false);
            tag += mixed < 0
                ? "⚠ 오디오 생략 · 🔇 무음"
                : "🔊 BGM · " + BabyBgmTag + ActiveBgmLabel(track) + (mixed > 0 ? " · " + SfxTag + mixed.ToString(inv) + "큐" : "");
            return new StockSuiteExportResult(true, target, false, tag) { HasAudio = mixed >= 0 };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Program.Log("StockSuite.Montage", ex);
            return new StockSuiteExportResult(false, null, false, "몽타주 내보내기 실패: " + ex.Message);
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
            EndCustomBgm(custom);
            BabyBgm = prevBgm;
            IncludeSfx = prevSfx;
            SubtitleCues = prevCues;
            Volatile.Write(ref _montageExporting, 0);
        }
    }
}
