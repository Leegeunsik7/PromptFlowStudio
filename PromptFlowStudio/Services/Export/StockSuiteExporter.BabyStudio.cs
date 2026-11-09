using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Export;

/// <summary>🎵 아기 감성 BGM 트랙.</summary>
public enum BabyBgmTrack { Lullaby, MusicBox, Acoustic }

public sealed partial class StockSuiteExporter
{
    /// <summary>🎵 아기 BGM 덕킹 볼륨(-14 LUFS 정규화 후 0.25 배).</summary>
    public const double BabyBgmVolume = 0.25;
    /// <summary>🎵 아기 BGM 페이드 인/아웃 길이(초).</summary>
    public const double BabyBgmFadeSec = 1.5;
    /// <summary>🎵 아기 BGM 동반 산출물 접미사(원본 옆 · 무음 StockSuite 에셋과 분리).</summary>
    public const string BabyBgmSuffix = "_baby_bgm";
    public const string BabyBgmTag = "🎵 감성 BGM · ";
    public const string GrowthBadgeTag = "🏷️ 성장 뱃지 · ";

    /// <summary>🎵 캔버스 BGM 토글 결선 — null = OFF(무음/기존 트랙 유지).</summary>
    public BabyBgmTrack? BabyBgm { get; set; }

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

    /// <summary>🎵 아기 BGM 오디오 필터 — loudnorm(-14 LUFS) → volume 0.25 덕킹 → 페이드 인 → (길이 기지 시) 페이드 아웃.</summary>
    public static string BuildBabyBgmAudioFilter(double durationSec)
    {
        var inv = CultureInfo.InvariantCulture;
        var f = AudioMuxService.LoudnormFilter + ",volume=" + BabyBgmVolume.ToString("0.##", inv)
            + ",afade=t=in:st=0:d=" + BabyBgmFadeSec.ToString("0.###", inv);
        if (double.IsFinite(durationSec) && durationSec > BabyBgmFadeSec * 2)
            f += ",afade=t=out:st=" + (durationSec - BabyBgmFadeSec).ToString("0.###", inv) + ":d=" + BabyBgmFadeSec.ToString("0.###", inv);
        return f;
    }

    /// <summary>🎵 아기 BGM 합성 인자 — 비디오 copy + BGM 루프 · AAC · -shortest.</summary>
    public static string[] BuildBabyBgmMuxArgs(string videoPath, string bgmPath, double durationSec, string outputPath)
    {
        var a = new List<string>(32)
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-stream_loop", "-1", "-i", bgmPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", "aac", "-b:a", AudioMuxService.AudioBitrate,
            "-af", BuildBabyBgmAudioFilter(durationSec),
            "-ar", AudioMuxService.OutputSampleRate, "-shortest",
        };
        if (double.IsFinite(durationSec) && durationSec > 0) { a.Add("-t"); a.Add(durationSec.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { a.Add("-movflags"); a.Add("+faststart"); }
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>원본 옆 <c>{name}_baby_bgm.mp4</c>.</summary>
    public static string BuildBabyBgmOutputPath(string videoPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath)) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + BabyBgmSuffix + ".mp4");
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
    /// 🎵 BGM ON 시 원본 옆 동반 산출물(<c>_baby_bgm.mp4</c>) 합성 — StockSuite 무음 에셋(Silent Ambient Guard)은 그대로 유지.
    /// 성공 시 출력 경로, OFF/실패 시 null(throw 없음 · 취소는 전파).
    /// </summary>
    public async Task<string?> TryMuxBabyBgmAsync(string sourcePath, double durationSec, CancellationToken ct = default)
    {
        if (BabyBgm is not { } track || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        string? bgm;
        try { bgm = BabyBgmResolver(track, durationSec); }
        catch (Exception ex) { Program.Log("StockSuite.BabyBgm.Resolve", ex); bgm = null; }
        if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return null;
        var output = BuildBabyBgmOutputPath(sourcePath);
        var err = await TryFfmpegAsync(BuildBabyBgmMuxArgs(sourcePath, bgm, durationSec, output), output, ct, ExportProfile.SnsShorts).ConfigureAwait(false);
        if (err is null) return output;
        Program.Log("StockSuite.BabyBgm", new IOException(err));
        return null;
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
}
