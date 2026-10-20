using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.24.0 정렬 입력 1건 — 원본 발화 시작(초) · 사용 가능 구간(초) · 더빙 음성 길이(초).</summary>
public readonly record struct DubTiming(double At, double Slot, double DubSeconds);

/// <summary>v16.24.0 정렬 결과 1건 — 배치 시각 · 템포(atempo ≥1) · 최종 길이 · 다음 발화 침범/잘림 여부.</summary>
public readonly record struct AlignedDubCue(double At, double Tempo, double Seconds, bool Overflow, bool Trimmed);

/// <summary>
/// [PromptFlowStudio] v16.24.0 👄 언어별 입모양 &amp; 자막 자동 재동기화 엔진 — 타겟 언어 발화 길이에 맞춘 타임라인 재정렬 · 타임 스트레칭.
///   ▸ 구간: 원본 발화 시작(컷 시작 + 0.3초) ~ 다음 발화 직전(<see cref="GapSeconds"/>) · 마지막은 영상 끝.
///   ▸ 더빙이 구간보다 길면 atempo 로 최대 <see cref="MaxTempo"/>x 압축 → 그래도 넘치면 다음 발화 직전까지 앞당김(최대 <see cref="MaxShiftSeconds"/>) → 그래도 넘치면 잘림 표시.
///     짧으면 원 속도 유지(무리한 감속은 부자연스러움).
///   ▸ 자막: 정렬된 큐 + 문자 체계별 줄 길이(CJK 14 · 태국어 20 · 라틴 등 30)·폰트로 v16.14.0 세이프존 ASS/SRT 생성.
///   ▸ 입모양: v16.20.0 viseme 타임라인을 (오프셋 · 템포) 로 제자리 재정렬 · 언어별 음절 수 추정(대본 길이 예측).
///   ▸ 정렬/재정렬/음절 추정은 Span 입출력 무할당.
/// </summary>
public static class MultilingualLipSyncAndSubtitleAligner
{
    public const double MaxTempo = 1.35;
    public const double GapSeconds = 0.12;
    public const double MaxShiftSeconds = 0.6;
    public const double TempoEpsilon = 0.02;

    /// <summary>원본 발화 시작들 → 정렬 입력(구간 계산 · 무할당). 반환 = 기록 수.</summary>
    public static int BuildTimings(ReadOnlySpan<double> starts, ReadOnlySpan<double> dubSeconds, double totalSeconds, Span<DubTiming> dst)
    {
        var n = Math.Min(Math.Min(starts.Length, dubSeconds.Length), dst.Length);
        for (var i = 0; i < n; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] - GapSeconds : totalSeconds;
            dst[i] = new DubTiming(starts[i], Math.Max(0.2, end - starts[i]), Math.Max(0, dubSeconds[i]));
        }
        return n;
    }

    /// <summary>타임 스트레칭 정렬(무할당) — 반환 = 기록 수.</summary>
    public static int Align(ReadOnlySpan<DubTiming> src, Span<AlignedDubCue> dst, double totalSeconds)
    {
        var n = Math.Min(src.Length, dst.Length);
        var prevEnd = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = src[i];
            var slot = Math.Max(0.2, t.Slot);
            var dub = Math.Max(0, t.DubSeconds);
            var ratio = dub / slot;
            var tempo = ratio > 1 + TempoEpsilon ? Math.Min(MaxTempo, ratio) : 1.0;
            var seconds = dub / tempo;
            var at = t.At;
            var overflow = false;
            var trimmed = false;
            if (seconds > slot + 1e-6)
            {
                // 앞당김: 이전 발화 종료 + 간격 이후 · 최대 MaxShiftSeconds
                var shift = Math.Min(MaxShiftSeconds, Math.Max(0, at - (prevEnd + GapSeconds)));
                shift = Math.Min(shift, seconds - slot);
                at -= shift;
                overflow = true;
                var limit = slot + shift;
                if (seconds > limit + 1e-6)
                {
                    trimmed = true;
                    seconds = limit;
                }
            }
            if (at + seconds > totalSeconds) { seconds = Math.Max(0, totalSeconds - at); trimmed = true; }
            dst[i] = new AlignedDubCue(Math.Round(at, 3), Math.Round(tempo, 3), Math.Round(seconds, 3), overflow, trimmed);
            prevEnd = at + seconds;
        }
        return n;
    }

    /// <summary>viseme 타임라인 제자리 재정렬 — 시작을 offset 으로 옮기고 템포만큼 압축(무할당).</summary>
    public static void RetimeVisemes(Span<VisemeCue> cues, double offset, double tempo)
    {
        var k = 1 / Math.Max(0.25, tempo);
        for (var i = 0; i < cues.Length; i++)
        {
            var c = cues[i];
            cues[i] = c with { Start = Math.Round(offset + c.Start * k, 4), End = Math.Round(offset + c.End * k, 4) };
        }
    }

    /// <summary>언어별 음절(모라) 수 추정(무할당) — 한글/한자/가나/태국 자음/데바나가리 자음 1글자 = 1 · 라틴/키릴/그리스/히브리/아랍은 모음 군집.</summary>
    public static int CountSyllables(ReadOnlySpan<char> text)
    {
        var n = 0;
        var inVowel = false;
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' || c is >= '一' and <= '鿿' || c is >= '぀' and <= 'ヿ' || c is >= 'ก' and <= 'ฮ' || c is >= 'क' and <= 'ह')
            { n++; inVowel = false; continue; }
            var v = IsVowel(c);
            if (v && !inVowel) n++;
            inVowel = v;
        }
        return n;
    }

    /// <summary>더빙 발화 길이 추정(초) — 음절 / 로캘 초당 음절 + 구두점 쉼 0.2초.</summary>
    public static double EstimateSeconds(ReadOnlySpan<char> text, LocaleProfile locale)
    {
        var pauses = 0;
        foreach (var c in text) if (c is '.' or '!' or '?' or ',' or '。' or '、' or '！' or '？' or '，') pauses++;
        var syl = CountSyllables(text);
        if (syl == 0) return 0;
        return Math.Round(Math.Max(0.4, syl / Math.Max(1, locale.SyllablesPerSecond) + pauses * 0.2), 3);
    }

    /// <summary>문자 체계별 자막 스타일(줄 길이 · 폰트).</summary>
    public static SubtitleStyle StyleFor(LocaleProfile locale, SubtitleStyle? baseStyle = null)
    {
        var st = baseStyle ?? new SubtitleStyle();
        var (font, perLine) = locale.Script switch
        {
            LocaleScript.Hangul => ("Malgun Gothic", 16),
            LocaleScript.Cjk => (locale.Code == "zh-TW" ? "Microsoft JhengHei" : "Microsoft YaHei", 14),
            LocaleScript.Kana => ("Yu Gothic", 14),
            LocaleScript.Thai => ("Leelawadee UI", 20),
            LocaleScript.Devanagari => ("Nirmala UI", 26),
            _ => ("Segoe UI", 30),
        };
        return st with { FontName = font, MaxCharsPerLine = perLine };
    }

    /// <summary>정렬 큐 + 번역 대사 → 자막용 VoiceCue(경로 없음 · 순수).</summary>
    public static List<VoiceCue> ToCaptionTiming(ReadOnlySpan<AlignedDubCue> aligned, IReadOnlyList<string> lines)
    {
        var list = new List<VoiceCue>(aligned.Length);
        for (var i = 0; i < aligned.Length && i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || aligned[i].Seconds <= 0) continue;
            list.Add(new VoiceCue("", aligned[i].At, aligned[i].Seconds, lines[i].Trim()));
        }
        return list;
    }

    /// <summary>타겟 언어 자막 사이드카(SRT/ASS) 기록 → ASS 경로 · 블록 수(블록 0 → null).</summary>
    public static async Task<(string AssPath, int Count)?> WriteSubtitlesAsync(string basePath, IReadOnlyList<VoiceCue> timing, LocaleProfile locale,
        SafePlatform platform, int width, int height, CancellationToken ct)
    {
        var style = StyleFor(locale);
        var cues = AutoSubtitleGenerator.BuildCues(timing, style);
        if (cues.Count == 0) return null;
        var layout = AutoSubtitleGenerator.ComputeLayout(platform, width, height, style);
        var ass = await AutoSubtitleGenerator.WriteAsync(basePath, cues, layout, style, ct).ConfigureAwait(false);
        return (ass, cues.Count);
    }

    /// <summary>템포 스트레칭 ffmpeg 인자(순수) — atempo(0.5~2 단일 필터) · 길이 상한 · 48k 스테레오 WAV.</summary>
    public static List<string> BuildTempoArgs(string input, string output, double tempo, double maxSeconds)
    {
        var t = Math.Clamp(tempo, 0.5, 2.0);
        var af = "atempo=" + FfmpegVideoMixerService.Inv(t) + ",aresample=48000";
        if (maxSeconds > 0) af += ",atrim=0:" + FfmpegVideoMixerService.Inv(maxSeconds) + ",afade=t=out:st=" + FfmpegVideoMixerService.Inv(Math.Max(0, maxSeconds - 0.08)) + ":d=0.08";
        return new List<string>(16) { "-y", "-hide_banner", "-nostdin", "-i", input, "-af", af, "-ac", "2", "-ar", "48000", output };
    }

    /// <summary>
    /// 정렬 적용 — 템포/잘림이 필요한 음성만 ffmpeg 로 스트레칭(실패 시 원본 유지 [부분 구제]) → 믹서용 VoiceCue.
    /// </summary>
    public static async Task<List<VoiceCue>> ApplyAsync(string? ffmpeg, IReadOnlyList<string?> audioPaths, IReadOnlyList<string> lines,
        AlignedDubCue[] aligned, string workDir, CancellationToken ct)
    {
        var list = new List<VoiceCue>(aligned.Length);
        for (var i = 0; i < aligned.Length && i < audioPaths.Count; i++)
        {
            var path = audioPaths[i];
            var a = aligned[i];
            if (path is null || a.Seconds <= 0) continue;
            var use = path;
            if (ffmpeg is not null && (a.Tempo > 1 + 1e-3 || a.Trimmed))
            {
                Directory.CreateDirectory(workDir);
                var outPath = Path.Combine(workDir, "dub_" + i.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8] + ".wav");
                var r = await FfmpegVideoMixerService.RunAsync(ffmpeg, BuildTempoArgs(path, outPath, a.Tempo, a.Trimmed ? a.Seconds : 0), outPath, 0, null, ct).ConfigureAwait(false);
                if (r.Ok) use = outPath;
            }
            list.Add(new VoiceCue(use, a.At, a.Seconds, i < lines.Count ? lines[i] : ""));
        }
        return list;
    }

    private static bool IsVowel(char c) => char.ToLowerInvariant(c) switch
    {
        'a' or 'e' or 'i' or 'o' or 'u' or 'y' or 'á' or 'à' or 'â' or 'ä' or 'ã' or 'å' or 'é' or 'è' or 'ê' or 'ë' or 'í' or 'ì' or 'î' or 'ï'
            or 'ó' or 'ò' or 'ô' or 'ö' or 'õ' or 'ø' or 'ú' or 'ù' or 'û' or 'ü' or 'ő' or 'ű' or 'ę' or 'ą' or 'ı' => true,
        'а' or 'е' or 'ё' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я' or 'і' or 'ї' or 'є' => true,
        'α' or 'ε' or 'η' or 'ι' or 'ο' or 'υ' or 'ω' or 'ά' or 'έ' or 'ή' or 'ί' or 'ό' or 'ύ' or 'ώ' => true,
        'ا' or 'و' or 'ي' or 'ى' => true,
        'א' or 'ו' or 'י' => true,
        >= 'अ' and <= 'औ' => true,       // 데바나가리 독립 모음(자음은 고유 모음 포함 → 1글자 = 1)
        _ => false,
    };
}
