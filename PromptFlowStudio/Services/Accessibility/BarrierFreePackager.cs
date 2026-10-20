using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Accessibility;

public sealed record BarrierFreeResult(string ZipPath, IReadOnlyList<string> Entries, long Bytes, string AggregateSha256, int AdSegments, int SignTokens, int ReadabilityIssues, float AdCoverage);

/// <summary>
/// [PromptFlow Studio v16.47.0] 📦 1-Click 배리어프리 패키저.
///   ▸ 수어 트랙(JSON) · 화면 해설 WebVTT(descriptions) · AD 스템 WAV · (메인 오디오 제공 시) 자동 더킹 믹스 WAV · 인지 친화 자막 WebVTT(STYLE) · 색약 보정 3D LUT(.cube 3종) ·
///     접근성 보고서(WCAG 명암비 · 가독성 위반 · AD 커버리지 · 대사 겹침) · 파일별 SHA-256 + 집계 해시 매니페스트.
/// </summary>
public sealed class BarrierFreePackager
{
    public const int SampleRate = 16000;

    private readonly AiSignLanguageAvatarSynthesizer _sign;
    private readonly GenerativeAudioDescriptionEngine _ad;
    private readonly WcagAccessibilityCorrectionFilter _wcag;
    private long _packages;

    public BarrierFreePackager(IServiceProvider sp)
        : this((AiSignLanguageAvatarSynthesizer)sp.GetService(typeof(AiSignLanguageAvatarSynthesizer))!, (GenerativeAudioDescriptionEngine)sp.GetService(typeof(GenerativeAudioDescriptionEngine))!,
               (WcagAccessibilityCorrectionFilter)sp.GetService(typeof(WcagAccessibilityCorrectionFilter))!) { }

    public BarrierFreePackager(AiSignLanguageAvatarSynthesizer sign, GenerativeAudioDescriptionEngine ad, WcagAccessibilityCorrectionFilter wcag)
    {
        _sign = sign; _ad = ad; _wcag = wcag;
    }

    public event Action? OnChange;

    public string OutputRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "BarrierFree");
    public long Packages => Interlocked.Read(ref _packages);
    public BarrierFreeResult? Last { get; private set; }
    public string? LastAction { get; private set; }

    public BarrierFreeResult Build(string title, IReadOnlyList<A11yCue> cues, double duration, float[]? mainAudio = null, string? outDir = null)
    {
        if (cues.Count == 0) throw new ArgumentException("자막 큐가 없습니다.");
        if (_sign.Tokens.Count == 0) _sign.LoadCaptions(cues);
        var segs = _ad.Segments.Count > 0 ? _ad.Segments : _ad.BuildScript(cues, duration);
        var files = new List<(string Path, byte[] Data)>();
        var utf8 = new UTF8Encoding(false);
        files.Add(("sign/sign_track.json", utf8.GetBytes(_sign.ExportTrackJson())));
        files.Add(("audio_description/descriptions.vtt", utf8.GetBytes(GenerativeAudioDescriptionEngine.ToWebVtt(segs))));
        var stem = GenerativeAudioDescriptionEngine.SynthesizeStem(segs, duration, SampleRate);
        files.Add(("audio_description/ad_stem.wav", GenerativeAudioDescriptionEngine.ToWav16(stem, SampleRate)));
        if (mainAudio is not null)
        {
            var ducked = (float[])mainAudio.Clone();
            Span<(double, double)> regions = stackalloc (double, double)[Math.Min(64, segs.Count)];
            for (var i = 0; i < regions.Length; i++) regions[i] = (segs[i].Start, segs[i].End);
            GenerativeAudioDescriptionEngine.ApplyDucking(ducked, regions, SampleRate, _ad.DuckDb);
            var mix = new float[Math.Max(ducked.Length, stem.Length)];
            GenerativeAudioDescriptionEngine.Mix(ducked, stem, mix);
            files.Add(("audio_description/mix_ducked.wav", GenerativeAudioDescriptionEngine.ToWav16(mix, SampleRate)));
        }
        var cog = WcagAccessibilityCorrectionFilter.CognitiveCues(cues, _wcag.Style);
        files.Add(("captions/cognitive.vtt", utf8.GetBytes(WcagAccessibilityCorrectionFilter.ToStyledVtt(cog, _wcag.Style))));
        foreach (var d in new[] { ColorVisionDeficiency.Protanopia, ColorVisionDeficiency.Deuteranopia, ColorVisionDeficiency.Tritanopia })
            files.Add(("color/daltonize_" + d.ToString().ToLowerInvariant() + ".cube", utf8.GetBytes(WcagAccessibilityCorrectionFilter.ExportCube(d, 1f))));
        var issues = WcagAccessibilityCorrectionFilter.CheckReadability(cog, _wcag.Style);
        double adSec = 0, gapSec = 0;
        foreach (var s in segs) adSec += s.Duration;
        Span<DialogueGap> gaps = stackalloc DialogueGap[64];
        var cueArr = new A11yCue[cues.Count];
        for (var i = 0; i < cueArr.Length; i++) cueArr[i] = cues[i];
        var gn = GenerativeAudioDescriptionEngine.FindGaps(cueArr, duration, _ad.MinGap, _ad.Guard, gaps);
        for (var i = 0; i < gn; i++) gapSec += gaps[i].Duration;
        var coverage = gapSec <= 0 ? 0 : (float)(adSec / gapSec);
        files.Add(("accessibility_report.json", Report(title, cues, segs, issues, coverage)));

        long total = 0; var agg = new StringBuilder();
        using var mms = new MemoryStream();
        using (var j = new Utf8JsonWriter(mms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject(); j.WriteString("title", title); j.WriteStartArray("files");
            foreach (var (p, d) in files)
            {
                var h = Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
                total += d.Length;
                agg.Append(p).Append(':').Append(h).Append(':').Append(d.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
                j.WriteStartObject(); j.WriteString("path", p); j.WriteString("sha256", h); j.WriteNumber("bytes", d.Length); j.WriteEndObject();
            }
            j.WriteEndArray();
            agg.Append("count=").Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append(";bytes=").Append(total.ToString(CultureInfo.InvariantCulture));
            j.WriteNumber("fileCount", files.Count); j.WriteNumber("totalBytes", total);
            j.WriteString("aggregateSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agg.ToString()))).ToLowerInvariant());
            j.WriteEndObject();
        }
        var aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agg.ToString()))).ToLowerInvariant();
        files.Add(("manifest.json", mms.ToArray()));

        var dir = outDir ?? OutputRoot;
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, ScormXSafe(title) + "_barrierfree.zip");
        var tmp = zip + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (p, d) in files) { var e = za.CreateEntry(p, CompressionLevel.Optimal); using var s = e.Open(); s.Write(d); }
        File.Move(tmp, zip, true);
        Interlocked.Increment(ref _packages);
        var names = new List<string>(files.Count);
        foreach (var f in files) names.Add(f.Path);
        var res = new BarrierFreeResult(zip, names, new FileInfo(zip).Length, aggregate, segs.Count, _sign.Tokens.Count, issues.Count, coverage);
        Last = res;
        LastAction = "📦 배리어프리 패키지 · " + Path.GetFileName(zip);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("BarrierFree.OnChange", ex); }
        return res;
    }

    private static string ScormXSafe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "program" : sb.ToString();
    }

    private byte[] Report(string title, IReadOnlyList<A11yCue> cues, IReadOnlyList<AdSegment> segs, List<ReadabilityIssue> issues, float coverage)
    {
        var st = _wcag.Style;
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("title", title);
            j.WriteStartObject("subtitles");
            j.WriteString("font", st.FontFamily); j.WriteString("resolvedFont", _wcag.ResolvedFont.Length > 0 ? _wcag.ResolvedFont : _wcag.ResolveFont());
            j.WriteNumber("contrastRatio", Math.Round(WcagAccessibilityCorrectionFilter.ContrastRatio(st.Foreground, st.Background), 2));
            j.WriteBoolean("wcagAAA", WcagAccessibilityCorrectionFilter.ContrastRatio(st.Foreground, st.Background) >= WcagAccessibilityCorrectionFilter.AaaRatio);
            j.WriteStartArray("issues"); foreach (var i in issues) { j.WriteStartObject(); j.WriteNumber("cue", i.Cue); j.WriteString("reason", i.Reason); j.WriteEndObject(); } j.WriteEndArray();
            j.WriteEndObject();
            j.WriteStartObject("audioDescription");
            j.WriteNumber("segments", segs.Count); j.WriteNumber("gapCoverage", Math.Round(coverage, 3));
            j.WriteBoolean("overlapsDialogue", GenerativeAudioDescriptionEngine.OverlapsDialogue(segs, cues));
            j.WriteNumber("duckDb", _ad.DuckDb);
            j.WriteEndObject();
            j.WriteStartObject("signLanguage");
            j.WriteString("language", _sign.Language == SignLanguage.Ksl ? "KSL" : "ASL"); j.WriteNumber("tokens", _sign.Tokens.Count);
            var spelled = 0; foreach (var t in _sign.Tokens) if (t.Fingerspelled) spelled++;
            j.WriteNumber("fingerspelled", spelled);
            j.WriteString("notice", "시연용 근사 수어 — 송출 전 공인 수어 통역사 검수 필요");
            j.WriteEndObject();
            j.WriteEndObject();
        }
        return ms.ToArray();
    }
}
