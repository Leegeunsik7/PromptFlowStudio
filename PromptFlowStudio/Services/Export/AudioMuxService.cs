using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Export;

/// <summary>SNS 쇼츠 배경음악 선택지.</summary>
public enum SnsBgmPreset { None, FintechSynthwave, LofiBeat, CustomFile }

/// <summary>BGM 합성 결과 — Ok=false 면 원본 영상 그대로 발행([부분 구제]).</summary>
public sealed record AudioMuxResult(bool Ok, string OutputPath, string Message);

/// <summary>
/// [PromptFlowStudio] 🎵 SNS 쇼츠 경량 BGM 믹서 — 무음 Wan 2.1 영상에 배경음악을 합성(Muxing).
///   ▸ <c>ffmpeg -i video -stream_loop -1 -i bgm -c:v copy -c:a aac -b:a 192k -shortest -af loudnorm=I=-14:LRA=11:TP=-1.5,afade=t=out:st={d-1.5}:d=1.5 -ar 48000 -t {d} out_sns.mp4</c>
///   ▸ 비디오는 무손실 copy, 음악은 영상 길이(5/10/15s)에 정확히 맞춰 컷 + 끝 1.5초 페이드아웃. 짧은 음원은 루프.
///   ▸ 프리셋(핀테크 신스웨이브 · 로파이)은 <see cref="AiGenerativeMusicEngine.Synthesize"/> 로컬 결정적 합성(WAV 캐시 · 외부 에셋/네트워크 0).
///   ▸ ffmpeg 부재/실패/취소 → 예외 없이 Ok=false(호출부가 원본 발행으로 폴백).
/// </summary>
public sealed class AudioMuxService
{
    public const double FadeOutSec = 1.5;
    public const string AudioBitrate = "192k";
    public const string OutputSuffix = "_sns";
    /// <summary>📢 SNS 쇼츠/릴스 표준 라우드니스 — EBU R128 원패스 -14 LUFS · LRA 11 · True Peak -1.5 dBTP.</summary>
    public const string LoudnormFilter = "loudnorm=I=-14:LRA=11:TP=-1.5";
    /// <summary>loudnorm 내부 192kHz 업샘플 → AAC 출력 48kHz 고정.</summary>
    public const string OutputSampleRate = "48000";

    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;
    private readonly string _cacheDir;

    public AudioMuxService() : this(null) { }

    /// <summary>🛡️ DI 주입 실행기 경유(장중 세이프가드 일원화).</summary>
    public AudioMuxService(Func<string?>? resolveFfmpeg, Engines.IProcessRunner processRunner, string? cacheDir)
        : this(resolveFfmpeg, Engines.ProcessRunnerExtensions.AsFfmpegRunner(processRunner), cacheDir) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null = <see cref="FfmpegLocator"/>).</param>
    /// <param name="runner">ffmpeg 실행기(테스트 주입 · null = ProcessRunner).</param>
    /// <param name="cacheDir">프리셋 BGM WAV 캐시 폴더(null = %TEMP%\PromptFlowStudio\bgm).</param>
    public AudioMuxService(Func<string?>? resolveFfmpeg, FfmpegRunner? runner = null, string? cacheDir = null)
    {
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _run = runner ?? Engines.ProcessRunnerExtensions.AsFfmpegRunner(Engines.SystemProcessRunner.Shared);
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "bgm") : cacheDir;
    }

    public static string Label(SnsBgmPreset p) => p switch
    {
        SnsBgmPreset.FintechSynthwave => "📈 핀테크 신스웨이브",
        SnsBgmPreset.LofiBeat => "☕ 로파이 비트",
        SnsBgmPreset.CustomFile => "사용자 MP3 선택",
        _ => "무음(기본)",
    };

    public static SnsBgmPreset ParsePreset(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "synthwave" or "fintech" => SnsBgmPreset.FintechSynthwave,
        "lofi" => SnsBgmPreset.LofiBeat,
        "custom" => SnsBgmPreset.CustomFile,
        _ => SnsBgmPreset.None,
    };

    public static string Key(SnsBgmPreset p) => p switch
    {
        SnsBgmPreset.FintechSynthwave => "synthwave",
        SnsBgmPreset.LofiBeat => "lofi",
        SnsBgmPreset.CustomFile => "custom",
        _ => "none",
    };

    /// <summary>페이드아웃 시작점 = max(0, duration − 1.5).</summary>
    public static double FadeStart(double durationSec) => Math.Max(0, durationSec - FadeOutSec);

    /// <summary>입력 영상 옆 <c>{name}_sns.mp4</c>.</summary>
    public static string BuildOutputPath(string videoPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath)) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + OutputSuffix + ".mp4");
    }

    /// <summary>
    /// 합성 인자 — 비디오 copy · AAC 192k · 영상 길이 컷(-shortest + -t) · <see cref="LoudnormFilter"/>(-14 LUFS) 정규화 → 끝 1.5초 페이드아웃.
    /// 길이 미상(≤0)이면 -shortest 만으로 영상 끝에 맞춘다(정규화만 · 페이드 생략).
    /// </summary>
    public static string[] BuildMuxArgs(string videoPath, string bgmPath, double durationSec, string outputPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string>(32)
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-stream_loop", "-1", "-i", bgmPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", "aac", "-b:a", AudioBitrate, "-shortest",
        };
        a.Add("-af");
        if (durationSec > 0)
        {
            a.Add(LoudnormFilter + ",afade=t=out:st=" + FadeStart(durationSec).ToString("0.###", inv) + ":d=" + FadeOutSec.ToString("0.###", inv));
            a.Add("-t"); a.Add(durationSec.ToString("0.###", inv));
        }
        else a.Add(LoudnormFilter);
        a.Add("-ar"); a.Add(OutputSampleRate);
        a.Add("-movflags"); a.Add("+faststart");
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>프리셋 → 로컬 합성 BGM WAV 경로(캐시 재사용). None/Custom → null.</summary>
    public string? EnsurePresetBgm(SnsBgmPreset preset, double durationSec)
    {
        if (preset is SnsBgmPreset.None or SnsBgmPreset.CustomFile) return null;
        var secs = Math.Clamp(Math.Ceiling(durationSec > 0 ? durationSec : 5) + 1, 3, 120);
        var path = Path.Combine(_cacheDir, Key(preset) + "_" + secs.ToString("0", CultureInfo.InvariantCulture) + "s.wav");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 44) return path;
            Directory.CreateDirectory(_cacheDir);
            var req = preset == SnsBgmPreset.FintechSynthwave
                ? new MusicRequest(MusicGenre.Edm, null, "neon synthwave fintech", secs, Seed: 1987, AllowRemote: false, Vocals: false)
                : new MusicRequest(MusicGenre.Lofi, null, "chill lofi", secs, Seed: 2024, AllowRemote: false, Vocals: false);
            var t = AiGenerativeMusicEngine.Synthesize(req);
            var tmp = path + ".~tmp";
            SoundDsp.WriteStereoWav(tmp, t.Left, t.Right, t.Rate);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("AudioMux.Preset", ex);
            return null;
        }
    }

    /// <summary>영상 + BGM 합성. 절대 throw 하지 않음(취소 포함) — 실패 시 Ok=false.</summary>
    public async Task<AudioMuxResult> MuxAsync(string? videoPath, SnsBgmPreset preset, string? customBgmPath, double durationSec, CancellationToken ct = default)
    {
        if (preset == SnsBgmPreset.None) return new(false, videoPath ?? "", "무음 — 합성 생략");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return new(false, videoPath ?? "", "원본 영상을 찾을 수 없습니다.");

        var bgm = preset == SnsBgmPreset.CustomFile ? customBgmPath?.Trim().Trim('"') : EnsurePresetBgm(preset, durationSec);
        if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return new(false, videoPath, "배경음악 파일을 찾을 수 없습니다.");

        string? exe;
        try { exe = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("AudioMux.Locate", ex); exe = null; }
        if (string.IsNullOrWhiteSpace(exe)) return new(false, videoPath, "ffmpeg 미설치 — 무음 원본으로 발행합니다.");

        var output = BuildOutputPath(videoPath);
        try
        {
            var ok = await _run(exe, BuildMuxArgs(videoPath, bgm, durationSec, output), ct).ConfigureAwait(false);
            if (ok && File.Exists(output)) return new(true, output, "🎵 " + Label(preset) + " 합성 완료");
            return new(false, videoPath, "BGM 합성 실패 — 무음 원본으로 발행합니다.");
        }
        catch (OperationCanceledException) { return new(false, videoPath, "BGM 합성 취소"); }
        catch (Exception ex)
        {
            Program.Log("AudioMux.Run", ex);
            return new(false, videoPath, "BGM 합성 실패: " + ex.Message);
        }
    }
}
