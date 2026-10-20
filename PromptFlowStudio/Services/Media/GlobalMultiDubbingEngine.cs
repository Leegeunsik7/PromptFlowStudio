using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard; // StoryboardProject

namespace PromptFlowStudio.Services.Media;

/// <summary>글로벌 바이어 타깃 언어 — 코드·표기·자국어명·국기·국가·자막 하단 세이프존 비율.</summary>
public sealed record DubLanguage(string Code, string Name, string Native, string Flag, string Country, double BottomSafeFraction);

/// <summary>자막 1줄 계획 — 원문/번역 + 타임코드(ms).</summary>
public sealed record DubLine(string Source, string Translated, int StartMs, int EndMs);

/// <summary>언어 1건 더빙 계획 — 언어 + 타임코드 정렬된 번역 라인.</summary>
public sealed record DubPlan(DubLanguage Language, IReadOnlyList<DubLine> Lines)
{
    public string BuildSrt() => GlobalMultiDubbingEngine.BuildSrt(Lines);
}

/// <summary>국가 1건 내보내기 결과.</summary>
public sealed record DubVariant(string Code, string Country, bool Ok, string? OutputPath, string Message);

/// <summary>다국어 일괄 내보내기 결과.</summary>
public sealed record MultiDubResult(bool Ok, IReadOnlyList<DubVariant> Variants, string OutputRoot, string Message);

/// <summary>
/// [PromptFlowStudio] 글로벌 수주용 다국어 AI 더빙 &amp; 동적 자막 파이프라인(v2.6.0) — 순수 BCL + FFmpeg CLI(외부 번역/ML 라이브러리 0).
///   ▸ 타깃 바이어 국가(🇺🇸en · 🇩🇪de · 🇨🇳zh · 🇯🇵ja)를 선택하면 한국어 대본을 <b>오프라인 프로모션 글로서리</b>로 번역하고,
///     라인 길이 가중 <b>타임코드 정렬</b>(<see cref="BuildLinePlan"/>)로 각 언어 자막을 배치한다.
///   ▸ 언어별 <b>세이프존 하단 자막</b>을 <c>drawtext</c> 시간 게이트로 번인하고(<see cref="BurnSubtitlesAsync"/>),
///     번역 나레이션 오디오가 있으면 <b>BGM 덕킹 믹스</b>(<see cref="BuildDuckMixArgs"/>)로 더빙한다.
///   ▸ 국가별 맞춤 홍보본을 <c>global/{code}/final.mp4</c> 로 <b>병렬 동시 내보내기</b>(<see cref="ExportAllAsync"/>). 글로서리 미상 문장은 원문 유지(Graceful).
/// </summary>
public sealed class GlobalMultiDubbingEngine
{
    /// <summary>지원 언어(ko=원본 · en/de/zh/ja=타깃).</summary>
    public static readonly IReadOnlyList<DubLanguage> Languages = new[]
    {
        new DubLanguage("ko", "Korean",   "한국어",  "🇰🇷", "대한민국", 0.12),
        new DubLanguage("en", "English",  "English", "🇺🇸", "United States", 0.12),
        new DubLanguage("de", "German",   "Deutsch", "🇩🇪", "Deutschland",   0.12),
        new DubLanguage("zh", "Chinese",  "中文",    "🇨🇳", "中国",          0.16),
        new DubLanguage("ja", "Japanese", "日本語",  "🇯🇵", "日本",          0.16),
    };

    /// <summary>코드로 언어 조회(없으면 null).</summary>
    public static DubLanguage? LanguageFor(string? code) => Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    // ══════════════════════ 오프라인 프로모션 글로서리 번역 ══════════════════════

    // 완결 문장(테마/장비 나레이션에 실제 등장) → 4개 언어. 미상은 단어 치환 후 원문 폴백.
    private static readonly Dictionary<string, string[]> PhraseBook = new(StringComparer.Ordinal)
    {
        // [ en, de, zh, ja ]
        ["정밀함은 검증에서 시작됩니다"] = new[] { "Precision begins with verification.", "Präzision beginnt mit der Prüfung.", "精密源于检测。", "精密は検証から始まります。" },
        ["데이터가 실시간으로 판독됩니다"] = new[] { "Data is read out in real time.", "Daten werden in Echtzeit ausgelesen.", "数据实时读取。", "データはリアルタイムで読み取られます。" },
        ["완벽한 품질 PASS 를 확인합니다"] = new[] { "Flawless quality — PASS confirmed.", "Perfekte Qualität — PASS bestätigt.", "完美品质，判定合格。", "完璧な品質、PASS を確認。" },
        ["우리가 만들어온 시간이 여기 서 있습니다"] = new[] { "The years we built now stand here.", "Die Jahre, die wir bauten, stehen hier.", "我们铸就的岁月矗立于此。", "私たちが築いた時間がここに立っています。" },
        ["하늘에서 그 위용을 마주합니다"] = new[] { "Behold its grandeur from the sky.", "Erleben Sie seine Erhabenheit aus der Luft.", "从空中俯瞰其雄姿。", "空からその威容を望みます。" },
        ["밤이 되면 도시가 우리를 주목합니다"] = new[] { "At night, the city turns to us.", "Bei Nacht blickt die Stadt auf uns.", "入夜，城市为我们瞩目。", "夜になると街が私たちに注目します。" },
        ["신뢰의 이름 그 여정은 계속됩니다"] = new[] { "A name of trust — the journey continues.", "Ein Name für Vertrauen — die Reise geht weiter.", "信赖之名，旅程不息。", "信頼の名、その旅は続きます。" },
    };

    // 기술 용어 단어 치환(문장 폴백 시 부분 현지화) → [en, de, zh, ja]
    private static readonly (string Ko, string[] Tr)[] Glossary =
    {
        ("검사", new[] { "inspection", "Prüfung", "检测", "検査" }),
        ("시험", new[] { "test", "Test", "试验", "試験" }),
        ("품질", new[] { "quality", "Qualität", "质量", "品質" }),
        ("정밀", new[] { "precision", "Präzision", "精密", "精密" }),
        ("데이터", new[] { "data", "Daten", "数据", "データ" }),
        ("자동차", new[] { "automotive", "Automobil", "汽车", "自動車" }),
        ("배터리", new[] { "battery", "Batterie", "电池", "バッテリー" }),
        ("사옥", new[] { "headquarters", "Zentrale", "总部", "本社" }),
        ("신뢰", new[] { "trust", "Vertrauen", "信赖", "信頼" }),
    };

    private static int LangIndex(string code) => code switch { "en" => 0, "de" => 1, "zh" => 2, "ja" => 3, _ => -1 };

    /// <summary>한국어 라인을 대상 언어로 오프라인 번역한다(완결 문장 우선 · 미상은 용어 치환 후 원문 유지).</summary>
    public static string Translate(string? koLine, string code)
    {
        var s = (koLine ?? "").Trim();
        if (s.Length == 0) return "";
        var idx = LangIndex((code ?? "").Trim().ToLowerInvariant());
        if (idx < 0) return s;   // ko/미지원 → 원문

        if (PhraseBook.TryGetValue(s, out var tr) && idx < tr.Length) return tr[idx];

        // 폴백: 알려진 용어만 부분 치환(원문 골격 유지 · 완전 번역 아님을 인정).
        var outp = s;
        foreach (var (ko, t) in Glossary)
            if (idx < t.Length && outp.Contains(ko, StringComparison.Ordinal))
                outp = outp.Replace(ko, t[idx], StringComparison.Ordinal);
        return outp;
    }

    // ══════════════════════ 타임코드 정렬 ══════════════════════

    /// <summary>대본 라인들을 라인 길이 가중으로 총 길이에 배분해 타임코드를 부여한다(TTS 타이밍 근사).</summary>
    public static IReadOnlyList<DubLine> BuildLinePlan(IReadOnlyList<string> koLines, string code, int totalMs)
    {
        var lines = (koLines ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var result = new List<DubLine>(lines.Count);
        if (lines.Count == 0) return result;

        var total = Math.Max(1000, totalMs);
        var weights = lines.Select(l => Math.Max(1, l.Trim().Length)).ToArray();
        double sum = weights.Sum();
        const int gap = 120;   // 라인 간 짧은 공백(ms)

        double cursor = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var slice = total * (weights[i] / sum);
            var start = (int)Math.Round(cursor);
            var end = (int)Math.Round(cursor + Math.Max(600, slice - gap));
            result.Add(new DubLine(lines[i].Trim(), Translate(lines[i], code), start, Math.Min(end, total)));
            cursor += slice;
        }
        return result;
    }

    /// <summary>프로젝트의 컷 나레이션을 대상 언어 더빙 계획으로 만든다(총 길이 미지정 시 컷당 근사).</summary>
    public DubPlan BuildPlan(StoryboardProject project, string code, int? totalMs = null)
    {
        var lang = LanguageFor(code) ?? Languages[1];
        var koLines = project.Scenes.Select(s => s.VoiceText ?? "").Where(t => t.Trim().Length > 0).ToList();
        var total = totalMs ?? Math.Max(3000, koLines.Count * 2800);
        return new DubPlan(lang, BuildLinePlan(koLines, lang.Code, total));
    }

    /// <summary>SRT 자막 문자열을 만든다(1-based 인덱스 · HH:MM:SS,mmm).</summary>
    public static string BuildSrt(IReadOnlyList<DubLine> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(Ts(lines[i].StartMs)).Append(" --> ").Append(Ts(lines[i].EndMs)).Append('\n');
            sb.Append(lines[i].Translated).Append('\n').Append('\n');
        }
        return sb.ToString();
    }

    private static string Ts(int ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    // ══════════════════════ 자막 번인 + 덕킹 믹스 ══════════════════════

    /// <summary>언어별 세이프존 하단 자막 <c>-vf</c>(라인마다 시간 게이트 drawtext · textfile 참조 예약 토큰).</summary>
    public static string BuildSubtitleVf(DubPlan plan, int height, string? escFontPath, Func<int, string> textFileFor)
    {
        var h = Math.Max(2, height);
        var fs = Math.Max(22, (int)Math.Round(h / 24.0));
        var bottom = (int)Math.Round(h * plan.Language.BottomSafeFraction);
        var fontHead = escFontPath is not null ? $"fontfile={escFontPath}:" : "";
        var parts = new List<string>();
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var ln = plan.Lines[i];
            if (string.IsNullOrWhiteSpace(ln.Translated)) continue;
            var t0 = (ln.StartMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var t1 = (ln.EndMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            parts.Add($"drawtext={fontHead}textfile={escFontPathSafe(textFileFor(i))}:fontcolor=white:fontsize={fs}:borderw=3:bordercolor=black@0.85:" +
                      $"x=(w-text_w)/2:y=h-text_h-{bottom}:enable=between(t\\,{t0}\\,{t1})");
        }
        return string.Join(",", parts);
    }

    private static string escFontPathSafe(string p) => (p ?? "").Replace('\\', '/').Replace(":", "\\:");

    /// <summary>번역 나레이션 오디오를 BGM 위에 덕킹 믹스하는 ffmpeg 인자(사이드체인 컴프레션).</summary>
    public static IReadOnlyList<string> BuildDuckMixArgs(string video, string narration, string output, double bgmVolume = 0.3)
    {
        var vol = bgmVolume.ToString("0.##", CultureInfo.InvariantCulture);
        // [0:a]=원본(BGM 포함) · [1:a]=번역 나레이션 → 나레이션 구간 BGM 덕킹 후 합성
        var fc = $"[0:a]volume={vol}[bg];[bg][1:a]sidechaincompress=threshold=0.05:ratio=8:attack=20:release=250[dux];" +
                 $"[dux][1:a]amix=inputs=2:duration=first:dropout_transition=2[aout]";
        return new[]
        {
            "-y", "-i", video, "-i", narration, "-filter_complex", fc,
            "-map", "0:v", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output,
        };
    }

    // ══════════════════════ 국가별 병렬 내보내기 ══════════════════════

    /// <summary>
    /// 마스터를 선택 언어들로 병렬 더빙/자막 내보내기한다. <c>global/{code}/final.mp4</c>. 실패 언어는 건너뛰고 계속(Graceful).
    /// </summary>
    public async Task<MultiDubResult> ExportAllAsync(string masterVideo, string projectDir, StoryboardProject project,
        IReadOnlyList<string> codes, int? totalMs = null, int maxParallel = 2,
        IProgress<(string Code, double Pct)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(masterVideo) || !File.Exists(masterVideo))
            return new(false, Array.Empty<DubVariant>(), "", "마스터 영상을 찾을 수 없습니다.");
        var targets = (codes ?? Array.Empty<string>())
            .Select(c => LanguageFor(c)).Where(l => l is not null && l!.Code != "ko").Select(l => l!).DistinctBy(l => l.Code).ToList();
        if (targets.Count == 0)
            return new(false, Array.Empty<DubVariant>(), "", "선택된 더빙 언어가 없습니다.");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return new(false, Array.Empty<DubVariant>(), "", "ffmpeg 실행 파일을 찾을 수 없습니다.");

        var outRoot = Path.Combine(projectDir, "global");
        Directory.CreateDirectory(outRoot);
        var font = FontResolver.FindKoreanFont(bold: false);
        var escFont = font is not null ? FontResolver.EscapeDrawPath(font) : null;

        var variants = new DubVariant[targets.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));

        var tasks = targets.Select(async (lang, idx) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var tmp = Path.Combine(Path.GetTempPath(), "promptflow-dub", Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var dir = Path.Combine(outRoot, lang.Code);
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(tmp);
                var finalPath = Path.Combine(dir, "final.mp4");

                var plan = BuildPlan(project, lang.Code, totalMs);
                var ok = await BurnSubtitlesAsync(ffmpeg, masterVideo, finalPath, plan, escFont, tmp, ct).ConfigureAwait(false);
                progress?.Report((lang.Code, ok ? 1.0 : 0.0));
                variants[idx] = new(lang.Code, lang.Country, ok, ok ? finalPath : null, ok ? "완료" : "자막 번인 실패");
            }
            catch (OperationCanceledException) { variants[idx] = new(lang.Code, lang.Country, false, null, "취소됨"); }
            catch (Exception ex) { variants[idx] = new(lang.Code, lang.Country, false, null, ex.Message); }
            finally { gate.Release(); try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var list = variants.Where(v => v is not null).ToList();
        var okCount = list.Count(v => v.Ok);
        return new(okCount > 0, list, outRoot, $"{okCount}/{targets.Count} 개국 홍보본 내보내기 완료");
    }

    /// <summary>더빙 계획의 자막을 언어 세이프존에 맞춰 번인한다(각 라인 UTF-8 textfile · 시간 게이트 drawtext).</summary>
    public async Task<bool> BurnSubtitlesAsync(string ffmpeg, string input, string output, DubPlan plan, string? escFont, string tmpDir, CancellationToken ct)
    {
        if (plan.Lines.Count == 0) return false;
        // 각 번역 라인을 개별 textfile 로(한글/CJK/라틴 안전).
        var files = new string[plan.Lines.Count];
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            files[i] = Path.Combine(tmpDir, $"l{i}.txt");
            File.WriteAllText(files[i], plan.Lines[i].Translated, new UTF8Encoding(false));
        }
        var height = 1080;
        var vf = BuildSubtitleVf(plan, height, escFont, i => files[i]);
        if (vf.Length == 0) return false;

        var args = new List<string>
        {
            "-y", "-i", input, "-vf", vf,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "copy", "-movflags", "+faststart", output,
        };
        var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
        return res.Ok && File.Exists(output);
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
