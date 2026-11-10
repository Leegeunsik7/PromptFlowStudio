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

/// <summary>💌 시간 동기 자막 큐(초 단위 표시 구간 + 텍스트).</summary>
public readonly record struct SubtitleCue(int StartSec, int EndSec, string Text);

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

    /// <summary>🎵 아기 BGM 오디오 필터 — loudnorm(-14 LUFS) → volume 0.25 → afade in 1.5s → (길이 기지 시) afade out <c>st={길이-1.5:F2}</c>.</summary>
    public static string BuildBabyBgmAudioFilter(double durationSec)
    {
        var inv = CultureInfo.InvariantCulture;
        var d = BabyBgmFadeSec.ToString("0.###", inv);
        var f = AudioMuxService.LoudnormFilter + ",volume=" + BabyBgmVolume.ToString("0.##", inv) + ",afade=t=in:st=0:d=" + d;
        if (double.IsFinite(durationSec) && durationSec > 0)
            f += ",afade=t=out:st=" + BabyBgmFadeOutStart(durationSec).ToString("F2", inv) + ":d=" + d;
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
            "-c:v", "copy", "-c:a", videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus" : "aac", "-b:a", AudioMuxService.AudioBitrate,
            "-af", BuildBabyBgmAudioFilter(durationSec),
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
    {
        if (BabyBgm is not { } track || string.IsNullOrWhiteSpace(primaryPath) || !File.Exists(primaryPath)) return false;
        double duration;
        try { duration = await DurationProbe(primaryPath, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("StockSuite.BabyBgm.Probe", ex); duration = 0; }
        string? bgm;
        try { bgm = BabyBgmResolver(track, duration); }
        catch (Exception ex) { Program.Log("StockSuite.BabyBgm.Resolve", ex); bgm = null; }
        if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm)) return false;
        var temp = primaryPath + BabyBgmMuxTempSuffix;
        try
        {
            var err = await TryFfmpegAsync(BuildBabyBgmMuxArgs(primaryPath, bgm, duration, temp), temp, ct, ExportProfile.BabyCinematic).ConfigureAwait(false);
            if (err is not null) { Program.Log("StockSuite.BabyBgm", new IOException(err)); TryDelete(temp); return false; }
            await SwapIntoPlaceAsync(temp, primaryPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { TryDelete(temp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.BabyBgm", ex);
            TryDelete(temp);
            return false;
        }
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

    /// <summary>💌 하단 중앙 반투명 박스 drawtext × N — 큐별 <c>enable='between(t,s,e)'</c> 시간 게이트. 유효 큐 없음 → null. 4K 는 2배 스케일.</summary>
    public static string? BuildTimedSubtitleFilter(IReadOnlyList<SubtitleCue>? cues, string? fontFile, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080)
    {
        if (cues is null || cues.Count == 0) return null;
        var k = resolution == StockSuiteResolution.Uhd4K ? 2 : 1;
        var inv = CultureInfo.InvariantCulture;
        var font = string.IsNullOrWhiteSpace(fontFile) ? "" : "fontfile='" + fontFile.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal) + "':";
        var style = ":fontsize=" + (24 * k).ToString(inv) + ":fontcolor=white:box=1:boxcolor=black@0.4:boxborderw=" + (10 * k).ToString(inv)
            + ":x=(w-text_w)/2:y=h-" + (120 * k).ToString(inv);
        var sb = new StringBuilder(256 * cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EndSec <= c.StartSec || c.StartSec < 0) continue;
            var clean = StripEmoji(c.Text.Replace('\r', ' ').Replace('\n', ' '));
            if (clean.Length == 0) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append("drawtext=").Append(font).Append("text='").Append(EscapeDrawText(clean))
              .Append("':enable='between(t,").Append(c.StartSec.ToString(inv)).Append(',').Append(c.EndSec.ToString(inv)).Append(")'")
              .Append(style);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static string? AppendTimedSubtitles(string? baseFilter, IReadOnlyList<SubtitleCue>? cues, string? fontFile, StockSuiteResolution resolution)
    {
        var subs = BuildTimedSubtitleFilter(cues, fontFile, resolution);
        if (subs is null) return baseFilter;
        return baseFilter is null ? subs : baseFilter + "," + subs;
    }

    /// <summary>💌 표준 SRT 직렬화(1-based 번호 · <c>HH:mm:ss,fff</c>). 유효 큐 없음 → 빈 문자열.</summary>
    public static string BuildSrt(IReadOnlyList<SubtitleCue>? cues)
    {
        if (cues is null || cues.Count == 0) return "";
        var sb = new StringBuilder(128 * cues.Count);
        var n = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EndSec <= c.StartSec || c.StartSec < 0) continue;
            if (n > 0) sb.Append('\n');
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendSrtTime(sb, c.StartSec).Append(" --> ");
            AppendSrtTime(sb, c.EndSec).Append('\n');
            sb.Append(c.Text.Replace("\r", "", StringComparison.Ordinal).Trim()).Append('\n');
        }
        return sb.ToString();
    }

    private static StringBuilder AppendSrtTime(StringBuilder sb, int totalSec)
    {
        var inv = CultureInfo.InvariantCulture;
        return sb.Append((totalSec / 3600).ToString("00", inv)).Append(':')
                 .Append((totalSec / 60 % 60).ToString("00", inv)).Append(':')
                 .Append((totalSec % 60).ToString("00", inv)).Append(",000");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.SubtitleSrt", ex);
            TryDelete(tmp);
            return null;
        }
    }
}
