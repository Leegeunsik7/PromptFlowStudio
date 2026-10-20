using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>텔레메트리 1 샘플 — 시각(초) + 지표값(정규 지표명→값) + 판정(있으면).</summary>
public sealed record TelemetrySample(double TimeSec, IReadOnlyDictionary<string, double> Metrics, bool? Pass);

/// <summary>파싱된 텔레메트리 시계열 — 샘플 + 등장 지표명(표시 순) + 총 길이(초) + 종합 판정.</summary>
public sealed record TelemetrySeries(IReadOnlyList<TelemetrySample> Samples, IReadOnlyList<string> MetricNames, double DurationSec, bool OverallPass)
{
    /// <summary>유효 데이터가 있는가.</summary>
    public bool HasData => Samples.Count > 0 && MetricNames.Count > 0;
}

/// <summary>
/// [PromptFlowStudio] 검사/시험 텔레메트리 HUD 오버레이 엔진(v2.5.0) — 순수 BCL 파서 + FFmpeg CLI drawtext/drawbox(외부 NuGet 0).
///   ▸ 실측 검사/시험 <b>CSV</b>(time,voltage,torque,rpm,angle,pressure,result …)를 파싱(<see cref="ParseCsv"/>)해
///     최종 영상 위에 <b>실시간 계기 HUD</b>를 번인(<see cref="BurnAsync"/>)한다 — 반투명 패널·지표 리드아웃·PASS/FAIL 판정 배지.
///   ▸ <b>3대 HUD 스타일</b>(장비 카테고리 자동 · <see cref="Services.Storyboard.VideoFormatFactory"/>):
///       <c>gauge</c>(시트 검사 · 수평 바 게이지) · <c>oscilloscope</c>(완성차/캘리브레이션 · 스윕 파형 라인) · <c>dyno</c>(전동화/다이나모 · 대형 RPM 리드아웃).
///   ▸ 시간축에 맞춰 각 샘플을 <c>enable=between(t\,t0\,t1)</c>로 게이트 → 값이 실시간으로 갱신되는 계기판 연출. 표시 샘플은 최대 <see cref="MaxKeyframes"/>로 리샘플(필터 그래프 방어).
///   ▸ HUD 라벨은 ASCII(RPM/TORQUE/VOLT/PASS 등)로 고정해 drawtext 인라인 이스케이프 리스크 0. ffmpeg 부재/CSV 결함은 Graceful(false).
/// </summary>
public sealed class TelemetryHudOverlayEngine
{
    /// <summary>시간축에 표시할 최대 키프레임 수(초과 시 균등 리샘플 · 필터 그래프 폭주 방어).</summary>
    public const int MaxKeyframes = 18;

    /// <summary>패널에 표시할 최대 지표 수(우선순위 상위 N).</summary>
    public const int MaxMetrics = 3;

    // 정규 지표 표시 설정 — 라벨(ASCII) · 단위 · drawtext 색 · 표시 우선순위(낮을수록 먼저).
    private sealed record MetricStyle(string Label, string Unit, string Color, int Priority);
    private static readonly Dictionary<string, MetricStyle> MetricStyles = new(StringComparer.Ordinal)
    {
        ["rpm"] = new("RPM", "", "0x39FF14", 0),
        ["torque"] = new("TORQUE", "Nm", "0x22D3EE", 1),
        ["voltage"] = new("VOLT", "V", "0xFDE047", 2),
        ["current"] = new("CURR", "A", "0xF59E0B", 3),
        ["pressure"] = new("PRESS", "bar", "0x38BDF8", 4),
        ["angle"] = new("ANGLE", "deg", "0xA78BFA", 5),
        ["force"] = new("FORCE", "N", "0xFB7185", 6),
        ["temp"] = new("TEMP", "C", "0xFCA5A5", 7),
    };

    // 헤더 별칭(영/한) → 정규 지표명. 'time'/'result'는 특수 처리.
    private static readonly (string Canon, string[] Aliases)[] Aliases =
    {
        ("time", new[] { "time", "t", "sec", "second", "seconds", "timestamp", "ms", "시간", "시각" }),
        ("voltage", new[] { "voltage", "volt", "volts", "v", "전압" }),
        ("current", new[] { "current", "amp", "amps", "amperage", "i", "전류" }),
        ("torque", new[] { "torque", "trq", "nm", "토크" }),
        ("rpm", new[] { "rpm", "revolution", "revs", "speed", "회전", "회전수" }),
        ("angle", new[] { "angle", "deg", "degree", "degrees", "각도" }),
        ("pressure", new[] { "pressure", "press", "bar", "psi", "kpa", "압력" }),
        ("force", new[] { "force", "load", "n", "하중", "힘" }),
        ("temp", new[] { "temp", "temperature", "℃", "온도" }),
        ("result", new[] { "result", "pass", "passfail", "verdict", "judge", "ok", "판정", "결과", "양품" }),
    };

    private static readonly string[] PassTokens = { "pass", "ok", "1", "true", "good", "양품", "합격", "정상" };

    // ══════════════════════ CSV 파싱 ══════════════════════

    /// <summary>CSV 텍스트를 텔레메트리 시계열로 파싱한다(헤더 별칭 자동 매핑 · 실패해도 예외 없이 빈 시계열).</summary>
    public static TelemetrySeries ParseCsv(string? csvText)
    {
        var empty = new TelemetrySeries(Array.Empty<TelemetrySample>(), Array.Empty<string>(), 0, true);
        if (string.IsNullOrWhiteSpace(csvText)) return empty;

        var lines = csvText.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return empty;

        // 1) 헤더 → 컬럼별 정규명(미상 컬럼은 null).
        var headers = SplitCsv(lines[0]);
        var colCanon = headers.Select(MapHeader).ToArray();
        var timeCol = Array.IndexOf(colCanon, "time");
        var resultCol = Array.IndexOf(colCanon, "result");
        var timeIsMs = timeCol >= 0 && headers[timeCol].Trim().Equals("ms", StringComparison.OrdinalIgnoreCase);

        // 등장 지표(표시 우선순위 순 · 중복 제거 · time/result 제외).
        var metricNames = colCanon
            .Where(c => c is not null && c != "time" && c != "result" && MetricStyles.ContainsKey(c))
            .Select(c => c!).Distinct()
            .OrderBy(c => MetricStyles[c].Priority).ToList();

        // 2) 데이터 행.
        var samples = new List<TelemetrySample>();
        var allPass = true; var anyResult = false;
        for (var r = 1; r < lines.Count; r++)
        {
            var cells = SplitCsv(lines[r]);
            if (cells.Length == 0) continue;

            var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var c = 0; c < cells.Length && c < colCanon.Length; c++)
            {
                var canon = colCanon[c];
                if (canon is null || canon == "time" || canon == "result") continue;
                if (!MetricStyles.ContainsKey(canon)) continue;
                if (double.TryParse(cells[c].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    metrics[canon] = v;
            }

            double time;
            if (timeCol >= 0 && timeCol < cells.Length && double.TryParse(cells[timeCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tv))
                time = timeIsMs ? tv / 1000.0 : tv;
            else
                time = samples.Count;   // time 컬럼 부재 → 행 인덱스를 초로(1 sample/sec 가정)

            bool? pass = null;
            if (resultCol >= 0 && resultCol < cells.Length)
            {
                var tok = cells[resultCol].Trim().ToLowerInvariant();
                pass = PassTokens.Contains(tok);
                anyResult = true;
                if (pass == false) allPass = false;
            }

            if (metrics.Count > 0 || pass is not null)
                samples.Add(new TelemetrySample(time, metrics, pass));
        }

        if (samples.Count == 0) return empty;
        var dur = Math.Max(0.1, samples[^1].TimeSec - samples[0].TimeSec);
        if (dur < 0.1) dur = Math.Max(1, samples.Count);
        return new TelemetrySeries(samples, metricNames, dur, anyResult ? allPass : true);
    }

    /// <summary>CSV 파일을 파싱한다(없으면 빈 시계열).</summary>
    public static TelemetrySeries ParseCsvFile(string? path)
    {
        try { return path is not null && File.Exists(path) ? ParseCsv(File.ReadAllText(path)) : ParseCsv(null); }
        catch (Exception ex) { Program.Log("TelemetryHud.ParseFile", ex); return ParseCsv(null); }
    }

    // 최소 CSV 분해(따옴표 미지원 · 단순 콤마 분리로 충분).
    private static string[] SplitCsv(string line) => line.Split(',');

    private static string? MapHeader(string raw)
    {
        var h = raw.Trim().ToLowerInvariant();
        if (h.Length == 0) return null;
        foreach (var (canon, al) in Aliases)
            if (al.Contains(h)) return canon;
        return null;
    }

    // ══════════════════════ HUD 필터 빌드 ══════════════════════

    /// <summary>
    /// 시계열 + 스타일 + 출력 규격으로 계기 HUD의 <c>-vf</c> 필터 체인(drawbox/drawtext 콤마 연결)을 만든다.
    ///   샘플은 <see cref="MaxKeyframes"/>로 균등 리샘플, 각 샘플은 <c>enable=between</c> 시간 게이트. 데이터 없으면 빈 문자열(HUD 없음).
    /// </summary>
    public static string BuildHudFilter(TelemetrySeries series, string? style, int width, int height, string? escapedFontPath)
    {
        if (series is null || !series.HasData) return "";
        var s = Math.Clamp(width / 1080.0, 0.5, 3.0);                   // 1080 기준 픽셀 스케일
        int P(double v) => (int)Math.Round(v * s);
        var st = (style ?? "").Trim().ToLowerInvariant();
        var metrics = series.MetricNames.Take(MaxMetrics).ToList();
        var frames = Resample(series.Samples, MaxKeyframes);
        var fontHead = escapedFontPath is not null ? $"fontfile={escapedFontPath}:" : "";

        int px = P(28), py = P(28);
        int rowH = P(46), fs = P(30), titleFs = P(22);
        int panelW = P(390), panelH = P(28) + (metrics.Count + 1) * rowH + (st == "gauge" ? P(24) : 0) + (st == "oscilloscope" ? P(90) : 0);

        var f = new List<string>();

        // 1) 반투명 패널 + 사이언 테두리.
        f.Add($"drawbox=x={px}:y={py}:w={panelW}:h={panelH}:color=black@0.42:t=fill");
        f.Add($"drawbox=x={px}:y={py}:w={panelW}:h={panelH}:color=0x22D3EE@0.55:t={Math.Max(1, P(2))}");

        // 2) 타이틀.
        var title = st switch { "gauge" => "TELEMETRY - GAUGE", "oscilloscope" => "TELEMETRY - SCOPE", "dyno" => "TELEMETRY - DYNO", _ => "TELEMETRY" };
        f.Add($"drawtext={fontHead}text={title}:fontcolor=0xE6EDFF:fontsize={titleFs}:x={px + P(16)}:y={py + P(12)}");

        int contentY = py + P(46);

        // 3) 지표 리드아웃(스타일별 강조) — 각 키프레임을 시간 게이트.
        var isDyno = st == "dyno";
        for (var m = 0; m < metrics.Count; m++)
        {
            var name = metrics[m];
            var ms = MetricStyles[name];
            int y = contentY + m * rowH;
            int labelFs = isDyno && m == 0 ? P(26) : P(22);
            int valFs = isDyno && m == 0 ? P(46) : fs;   // 다이나모: 첫 지표(주로 RPM) 대형
            f.Add($"drawtext={fontHead}text={ms.Label}:fontcolor=0x9AA6C8:fontsize={labelFs}:x={px + P(18)}:y={y}");
            foreach (var (fr, t0, t1) in frames)
            {
                if (!fr.Metrics.TryGetValue(name, out var val)) continue;
                var txt = FmtVal(val) + (ms.Unit.Length > 0 ? " " + ms.Unit : "");
                f.Add($"drawtext={fontHead}text={txt}:fontcolor={ms.Color}:fontsize={valFs}:x={px + P(150)}:y={y - (valFs - fs) / 2}:enable=between(t\\,{Num(t0)}\\,{Num(t1)})");
            }
        }

        // 4) 스타일별 플로리시.
        if (st == "gauge" && metrics.Count > 0)
        {
            // 첫 지표를 min..max 정규화한 수평 바 게이지(키프레임별 채움 폭).
            var name = metrics[0];
            var (min, max) = Range(series.Samples, name);
            int barY = contentY + metrics.Count * rowH + P(4), barX = px + P(18), barW = panelW - P(36), barH = P(14);
            f.Add($"drawbox=x={barX}:y={barY}:w={barW}:h={barH}:color=0x1A1D2B@0.9:t=fill");
            foreach (var (fr, t0, t1) in frames)
            {
                if (!fr.Metrics.TryGetValue(name, out var val)) continue;
                var norm = max > min ? Math.Clamp((val - min) / (max - min), 0, 1) : 0.5;
                int fill = Math.Max(P(2), (int)Math.Round(barW * norm));
                f.Add($"drawbox=x={barX}:y={barY}:w={fill}:h={barH}:color=0x22D3EE@0.9:t=fill:enable=between(t\\,{Num(t0)}\\,{Num(t1)})");
            }
        }
        else if (st == "oscilloscope")
        {
            // 파형 스트립 + 좌→우 스윕 플레이헤드(t 함수 · 반복 2s).
            int gY = contentY + metrics.Count * rowH + P(6), gX = px + P(18), gW = panelW - P(36), gH = P(70);
            f.Add($"drawbox=x={gX}:y={gY}:w={gW}:h={gH}:color=0x0B0D14@0.85:t=fill");
            f.Add($"drawbox=x={gX}:y={gY + gH / 2}:w={gW}:h={Math.Max(1, P(1))}:color=0x39FF14@0.4:t=fill");   // 기준선
            f.Add($"drawbox=x={gX}+{gW}*mod(t\\,2)/2:y={gY}:w={Math.Max(2, P(3))}:h={gH}:color=0x39FF14@0.95:t=fill");   // 스윕 라인
        }

        // 5) PASS/FAIL 종합 판정 배지(우측 상단).
        var pass = series.OverallPass;
        var badgeTxt = pass ? "PASS" : "FAIL";
        var badgeCol = pass ? "0x22C55E" : "0xEF4444";
        int bW = P(150), bH = P(52), bX = width - bW - P(28), bY = P(28), bFs = P(34);
        f.Add($"drawbox=x={bX}:y={bY}:w={bW}:h={bH}:color={badgeCol}@0.22:t=fill");
        f.Add($"drawbox=x={bX}:y={bY}:w={bW}:h={bH}:color={badgeCol}@0.95:t={Math.Max(1, P(2))}");
        f.Add($"drawtext={fontHead}text={badgeTxt}:fontcolor={badgeCol}:fontsize={bFs}:x={bX}+({bW}-text_w)/2:y={bY}+({bH}-text_h)/2");

        return string.Join(",", f);
    }

    // ══════════════════════ 번인 ══════════════════════

    /// <summary>
    /// 최종 영상 위에 텔레메트리 HUD를 번인한다. CSV/입력/ffmpeg 부재 또는 데이터 결함 시 false(호출부가 원본 유지 · Graceful).
    /// </summary>
    public async Task<bool> BurnAsync(string input, string output, string? csvPathOrText, string? style, int width, int height, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return false;

        var series = csvPathOrText is not null && File.Exists(csvPathOrText) ? ParseCsvFile(csvPathOrText) : ParseCsv(csvPathOrText);
        if (!series.HasData) return false;

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;

        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var font = FontResolver.FindKoreanFont(bold: true);
            var escFont = font is not null ? FontResolver.EscapeDrawPath(font) : null;
            var vf = BuildHudFilter(series, style, width, height, escFont);
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
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("TelemetryHud.Burn", ex); return false; }
    }

    // ══════════════════════ 헬퍼 ══════════════════════

    // 샘플을 최대 max개로 균등 리샘플 → (샘플, 시작초, 종료초) 시간창.
    private static List<(TelemetrySample Fr, double T0, double T1)> Resample(IReadOnlyList<TelemetrySample> src, int max)
    {
        var picks = new List<TelemetrySample>();
        if (src.Count <= max) picks.AddRange(src);
        else
        {
            for (var i = 0; i < max; i++)
            {
                var idx = (int)Math.Round(i * (src.Count - 1) / (double)(max - 1));
                picks.Add(src[Math.Clamp(idx, 0, src.Count - 1)]);
            }
        }
        var outp = new List<(TelemetrySample, double, double)>(picks.Count);
        for (var i = 0; i < picks.Count; i++)
        {
            var t0 = i == 0 ? 0.0 : (picks[i].TimeSec + picks[i - 1].TimeSec) / 2.0;
            var t1 = i == picks.Count - 1 ? 1e6 : (picks[i].TimeSec + picks[i + 1].TimeSec) / 2.0;
            outp.Add((picks[i], Math.Max(0, t0), Math.Max(t0 + 0.05, t1)));
        }
        return outp;
    }

    private static (double Min, double Max) Range(IReadOnlyList<TelemetrySample> src, string metric)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in src)
            if (s.Metrics.TryGetValue(metric, out var v)) { if (v < min) min = v; if (v > max) max = v; }
        return min <= max ? (min, max) : (0, 1);
    }

    private static string FmtVal(double v) => Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

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
