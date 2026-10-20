using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.17.0 QA 판정 — 통과 · 경고(게시 허용) · 거부(게시 차단). 순서 = 심각도.</summary>
public enum QaVerdict { Pass, Warn, Reject }

/// <summary>v16.17.0 결함 종류.</summary>
public enum QaDefectKind { BlackFrame, FrozenFrame, AudioClipping, AudioHeadroom, AudioSilence, SubtitleOcclusion }

/// <summary>v16.17.0 결함 1건 — 문제 구간(초) · 심각도 · 설명.</summary>
public readonly record struct QaDefect(QaDefectKind Kind, QaVerdict Severity, double StartSeconds, double EndSeconds, string Detail);

/// <summary>v16.17.0 자막 가림 검사 입력 — 번인 레이아웃 · 큐 · 게시 대상 플랫폼 세이프존.</summary>
public sealed record QaSubtitleProbe(SubtitleLayout Layout, IReadOnlyList<CaptionCue> Cues, SafePlatform Target);

/// <summary>v16.17.0 검수 리포트. Scanned=false 는 ffmpeg 부재 등으로 미검사(경고 · 게시 허용).</summary>
public sealed record QaInspectionReport(
    QaVerdict Verdict, bool Scanned, double DurationSeconds, double PeakDbfs, double RmsDbfs,
    IReadOnlyList<QaDefect> Defects, string Message, DateTimeOffset At)
{
    public bool CanPublish => Verdict != QaVerdict.Reject;

    /// <summary>검수 대상 영상 경로(UI 가 현재 프로젝트 결과인지 대조).</summary>
    public string? VideoPath { get; init; }
}

/// <summary>
/// v16.17.0 ffmpeg stderr(blackdetect · freezedetect · astats · Duration) 라인 파서 — Span 슬라이싱(Substring/Split 0) · 순수 · 테스트 대상.
///   ▸ freeze_end 없이 끝난 정지 구간은 <see cref="Complete"/> 에서 영상 길이로 닫는다.
///   ▸ astats 는 채널별 + Overall 섹션 — 피크는 전 섹션 최대, RMS 는 Overall 값 우선.
/// </summary>
public sealed class FfmpegQaLogParser
{
    public readonly List<(double Start, double End)> Black = new(8);
    public readonly List<(double Start, double End)> Freeze = new(8);
    public double DurationSeconds { get; private set; }
    public double PeakDbfs { get; private set; } = double.NegativeInfinity;
    public double RmsDbfs { get; private set; } = double.NegativeInfinity;
    public bool HasAudioStats { get; private set; }

    private double _freezeStart = -1;
    private bool _overall;

    public void Feed(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return;
        if (line.IndexOf("black_start:", StringComparison.Ordinal) >= 0)
        {
            if (TryField(line, "black_start:", out var s) && TryField(line, "black_end:", out var e)) Black.Add((s, e));
            return;
        }
        if (line.IndexOf("freezedetect.freeze_start:", StringComparison.Ordinal) >= 0)
        {
            if (TryField(line, "freeze_start:", out var s)) _freezeStart = s;
            return;
        }
        if (line.IndexOf("freezedetect.freeze_end:", StringComparison.Ordinal) >= 0)
        {
            if (_freezeStart >= 0 && TryField(line, "freeze_end:", out var e)) Freeze.Add((_freezeStart, e));
            _freezeStart = -1;
            return;
        }
        var d = line.IndexOf("Duration: ", StringComparison.Ordinal);
        if (d >= 0 && DurationSeconds <= 0)
        {
            if (TryClock(line.Slice(d + 10), out var dur)) DurationSeconds = dur;
            return;
        }
        if (line.IndexOf("Parsed_astats", StringComparison.Ordinal) < 0) return;
        if (line.EndsWith("Overall", StringComparison.Ordinal)) { _overall = true; return; }
        if (TryField(line, "Peak level dB:", out var peak))
        {
            HasAudioStats = true;
            if (peak > PeakDbfs) PeakDbfs = peak;
            return;
        }
        if (TryField(line, "RMS level dB:", out var rms))
        {
            HasAudioStats = true;
            if (_overall || double.IsNegativeInfinity(RmsDbfs) || rms > RmsDbfs) RmsDbfs = rms;
        }
    }

    /// <summary>스트림 종료 — 열린 정지 구간을 영상 끝으로 닫는다.</summary>
    public void Complete()
    {
        if (_freezeStart >= 0) Freeze.Add((_freezeStart, Math.Max(_freezeStart, DurationSeconds)));
        _freezeStart = -1;
    }

    private static bool TryField(ReadOnlySpan<char> line, string key, out double v)
    {
        v = 0;
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return false;
        var rest = line.Slice(i + key.Length).TrimStart();
        var end = 0;
        while (end < rest.Length && rest[end] != ' ' && rest[end] != ',' && rest[end] != '\t') end++;
        var num = rest.Slice(0, end);
        if (num.SequenceEqual("-inf")) { v = double.NegativeInfinity; return true; }
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>"HH:MM:SS.ff" → 초.</summary>
    private static bool TryClock(ReadOnlySpan<char> s, out double seconds)
    {
        seconds = 0;
        var c1 = s.IndexOf(':');
        if (c1 <= 0) return false;
        var c2 = s.Slice(c1 + 1).IndexOf(':');
        if (c2 <= 0) return false;
        var secPart = s.Slice(c1 + c2 + 2);
        var end = secPart.IndexOf(',');
        if (end >= 0) secPart = secPart.Slice(0, end);
        if (!int.TryParse(s.Slice(0, c1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(s.Slice(c1 + 1, c2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return false;
        if (!double.TryParse(secPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return false;
        seconds = h * 3600 + m * 60 + sec;
        return true;
    }
}

/// <summary>
/// [PromptFlowStudio] v16.17.0 🔍 자동 비디오/오디오 품질 검수기 — 최종 렌더/OSMU 영상을 ffmpeg 1패스(<c>-f null</c>)로 스캔(순수 BCL · 외부 NuGet 0).
///   ▸ 비디오: <c>blackdetect</c>(블랙 구간) · <c>freezedetect</c>(정지 구간) / 오디오: <c>astats</c>(피크 dBFS · RMS) — stderr 를 <see cref="FfmpegQaLogParser"/> 로 파싱.
///   ▸ 자막: 번인 레이아웃(<see cref="SubtitleLayout"/>)의 자막 박스가 게시 대상 플랫폼 세이프존(<see cref="MultiPlatformSafeZoneService"/>) 밖으로 나가면 가림 결함.
///   ▸ 판정: 블랙 ≥1초 · 정지 ≥4초 · 피크 ≥-0.1dBFS(클리핑) · 자막 50%↑ 가림 → 거부 / 그 미만 결함 → 경고 / 결함 0 → 통과. 결함마다 문제 구간 타임스탬프 기록.
///   ▸ ffmpeg 부재·디코드 실패·취소 → Scanned=false 경고(검수를 이유로 파이프라인을 멈추지 않음 · Graceful). 동시 스캔 1건(재진입 차단).
/// </summary>
public sealed class AutomatedVideoQaInspector
{
    public const double BlackMinSeconds = 0.1;
    public const double BlackRejectSeconds = 1.0;
    public const double FreezeMinSeconds = 1.0;
    public const double FreezeRejectSeconds = 4.0;
    public const double ClipPeakDbfs = -0.1;
    public const double HeadroomPeakDbfs = -1.0;
    public const double SilenceRmsDbfs = -60.0;
    public const double OcclusionRejectRatio = 0.5;
    private const double LineHeightFactor = 1.25;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string?> _ffmpeg;
    private int _busy;
    private QaInspectionReport? _last;

    public AutomatedVideoQaInspector(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public AutomatedVideoQaInspector(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public event Action? OnChange;
    public bool IsAvailable => _ffmpeg() is not null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public QaInspectionReport? LastReport => Volatile.Read(ref _last);

    /// <summary>ffmpeg 스캔 인자(순수 함수 · 공백 경로 안전 리스트).</summary>
    public static List<string> BuildArgs(string input) => new(20)
    {
        "-hide_banner", "-nostdin", "-i", input,
        "-vf", "blackdetect=d=0.1:pix_th=0.10,freezedetect=n=-60dB:d=1",
        "-af", "astats=metadata=0:reset=0",
        "-f", "null", "-",
    };

    /// <summary>영상 1개 검수(예외 0). subtitles 가 있으면 자막 가림도 함께 판정.</summary>
    public async Task<QaInspectionReport> InspectAsync(string videoPath, QaSubtitleProbe? subtitles = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Publish(Unscanned("검수 대상 영상이 없습니다.", subtitles));
        var exe = _ffmpeg();
        if (exe is null) return Publish(Unscanned("ffmpeg 없음 — 프레임/오디오 검수 생략", subtitles) with { VideoPath = videoPath });
        if (Interlocked.Exchange(ref _busy, 1) != 0) return Unscanned("다른 검수가 진행 중입니다.", subtitles);
        try
        {
            var parser = new FfmpegQaLogParser();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ScanTimeout);
            await RunScanAsync(exe, videoPath, parser, cts.Token).ConfigureAwait(false);
            parser.Complete();
            return Publish(Evaluate(parser, subtitles) with { VideoPath = videoPath });
        }
        catch (OperationCanceledException) { return Publish(Unscanned("검수 취소/시간 초과 — 생략", subtitles) with { VideoPath = videoPath }); }
        catch (Exception ex) { Program.Log("VideoQa.Inspect", ex); return Publish(Unscanned("검수 오류 — 생략", subtitles) with { VideoPath = videoPath }); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>파싱 결과 + 자막 검사 → 리포트(순수 · 테스트 대상).</summary>
    public static QaInspectionReport Evaluate(FfmpegQaLogParser scan, QaSubtitleProbe? subtitles = null)
    {
        var defects = new List<QaDefect>(8);
        foreach (var (s, e) in scan.Black)
        {
            var d = e - s;
            if (d < BlackMinSeconds) continue;
            defects.Add(new(QaDefectKind.BlackFrame, d >= BlackRejectSeconds ? QaVerdict.Reject : QaVerdict.Warn, s, e,
                "블랙 프레임 " + F1(d) + "초"));
        }
        foreach (var (s, e) in scan.Freeze)
        {
            var d = e - s;
            if (d < FreezeMinSeconds) continue;
            defects.Add(new(QaDefectKind.FrozenFrame, d >= FreezeRejectSeconds ? QaVerdict.Reject : QaVerdict.Warn, s, e,
                "정지 프레임 " + F1(d) + "초"));
        }
        var dur = scan.DurationSeconds;
        if (scan.HasAudioStats)
        {
            if (scan.PeakDbfs >= ClipPeakDbfs)
                defects.Add(new(QaDefectKind.AudioClipping, QaVerdict.Reject, 0, dur, "오디오 클리핑(피크 " + F1(scan.PeakDbfs) + "dBFS)"));
            else if (scan.PeakDbfs >= HeadroomPeakDbfs)
                defects.Add(new(QaDefectKind.AudioHeadroom, QaVerdict.Warn, 0, dur, "헤드룸 부족(피크 " + F1(scan.PeakDbfs) + "dBFS)"));
            if (scan.RmsDbfs <= SilenceRmsDbfs)
                defects.Add(new(QaDefectKind.AudioSilence, QaVerdict.Warn, 0, dur, "무음/저음량 의심(RMS " + F1(scan.RmsDbfs) + "dBFS)"));
        }
        if (subtitles is not null) CheckSubtitles(subtitles, defects);
        return Build(true, dur, scan.HasAudioStats ? scan.PeakDbfs : double.NegativeInfinity,
            scan.HasAudioStats ? scan.RmsDbfs : double.NegativeInfinity, defects);
    }

    /// <summary>자막 박스가 대상 세이프존 밖으로 벗어난 비율(0~1 · 순수). 세로 밴드 + 우측 액션 레일 기준.</summary>
    public static double OcclusionRatio(SubtitleLayout layout, int lineCount, SafePlatform target)
    {
        var h = (double)Math.Max(2, layout.Height);
        var w = (double)Math.Max(2, layout.Width);
        var boxH = Math.Max(1, lineCount) * layout.FontSize * LineHeightFactor + 2 * layout.Outline;
        var bottom = (h - layout.MarginV) / h;
        var top = Math.Max(0, bottom - boxH / h);
        var zone = SafeZoneOf(target);
        var span = Math.Max(1e-6, bottom - top);
        var outside = Math.Max(0, bottom - zone.BottomPct) + Math.Max(0, zone.TopPct - top);
        var vertical = Math.Clamp(outside / span, 0, 1);
        // 좌우 대칭 여백(중앙 정렬)이 대상 레일 폭보다 좁으면 그 차이만큼 자막 가로폭이 레일에 깔린다.
        var rail = AutoSubtitleGenerator.RightRailFraction(target);
        var margin = layout.MarginR / w;
        var textW = Math.Max(1e-6, 1 - (layout.MarginL + layout.MarginR) / w);
        var horizontal = margin >= rail ? 0 : Math.Clamp((rail - margin) / textW, 0, 1);
        return Math.Max(vertical, horizontal);
    }

    private static void CheckSubtitles(QaSubtitleProbe probe, List<QaDefect> defects)
    {
        if (probe.Cues is null) return;
        foreach (var cue in probe.Cues)
        {
            var ratio = OcclusionRatio(probe.Layout, LineCount(cue.Text), probe.Target);
            if (ratio <= 0.001) continue;
            defects.Add(new(QaDefectKind.SubtitleOcclusion, ratio >= OcclusionRejectRatio ? QaVerdict.Reject : QaVerdict.Warn,
                cue.Start, cue.End, "자막 #" + cue.Index.ToString(CultureInfo.InvariantCulture) + " UI 가림 " + ((int)Math.Round(ratio * 100)).ToString(CultureInfo.InvariantCulture) + "%"));
        }
    }

    /// <summary>줄 수 — 실제 개행 또는 ASS 강제 개행(\N) 기준.</summary>
    public static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var span = text.AsSpan();
        var n = 1;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '\n') n++;
            else if (span[i] == '\\' && i + 1 < span.Length && span[i + 1] == 'N') { n++; i++; }
        }
        return n;
    }

    /// <summary>
    /// 최종 MP4 옆 ASS 사이드카(<see cref="AutoSubtitleGenerator.WriteAsync"/> 산출물)에서 번인 레이아웃 + 큐 복원 → 대상 플랫폼 가림 검사 입력.
    /// 사이드카 없음/형식 오류는 null(자막 검사 생략).
    /// </summary>
    public static QaSubtitleProbe? TryLoadSidecarProbe(string? videoPath, SafePlatform target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return null;
            var ass = Path.ChangeExtension(videoPath, ".ass");
            return File.Exists(ass) ? ParseAss(File.ReadAllText(ass), target) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>ASS 텍스트 → 자막 검사 입력(PlayRes · 첫 Style 의 Fontsize/Outline/Margin · Dialogue 시간/텍스트 · 순수).</summary>
    public static QaSubtitleProbe? ParseAss(string text, SafePlatform target)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int w = 0, h = 0, font = 0, ml = 0, mr = 0, mv = 0;
        double outline = 0;
        var styled = false;
        var cues = new List<CaptionCue>(32);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("PlayResX:", StringComparison.Ordinal)) int.TryParse(line.AsSpan(9).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out w);
            else if (line.StartsWith("PlayResY:", StringComparison.Ordinal)) int.TryParse(line.AsSpan(9).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h);
            else if (!styled && line.StartsWith("Style:", StringComparison.Ordinal))
            {
                var f = line.Substring(6).Split(',');
                if (f.Length < 23) continue;
                styled = int.TryParse(f[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out font)
                    & double.TryParse(f[16].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out outline)
                    & int.TryParse(f[19].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ml)
                    & int.TryParse(f[20].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mr)
                    & int.TryParse(f[21].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mv);
            }
            else if (line.StartsWith("Dialogue:", StringComparison.Ordinal))
            {
                var f = line.Substring(9).Split(',', 10);
                if (f.Length < 10 || !TryAssTime(f[1], out var start) || !TryAssTime(f[2], out var end)) continue;
                cues.Add(new CaptionCue(cues.Count + 1, start, end, f[9]));
            }
        }
        if (!styled || w <= 0 || h <= 0 || cues.Count == 0) return null;
        return new QaSubtitleProbe(new SubtitleLayout(target, w, h, mv, ml, mr, font, outline), cues, target);
    }

    /// <summary>"H:MM:SS.cc" → 초.</summary>
    private static bool TryAssTime(string s, out double seconds)
    {
        seconds = 0;
        var p = s.Trim().Split(':');
        if (p.Length != 3) return false;
        if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)) return false;
        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)) return false;
        if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ss)) return false;
        seconds = hh * 3600 + mm * 60 + ss;
        return true;
    }

    /// <summary>결함 종류 표시명(UI 배지용 · 리터럴).</summary>
    public static string LabelOf(QaDefectKind k) => k switch
    {
        QaDefectKind.BlackFrame => "⬛ 블랙 프레임",
        QaDefectKind.FrozenFrame => "🧊 정지 프레임",
        QaDefectKind.AudioClipping => "📢 오디오 클리핑",
        QaDefectKind.AudioHeadroom => "🔊 헤드룸 부족",
        QaDefectKind.AudioSilence => "🔇 무음/저음량",
        _ => "💬 자막 가림",
    };

    /// <summary>초 → "m:ss.f"(UI 타임스탬프 · InvariantCulture).</summary>
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var m = (int)(seconds / 60);
        var s = seconds - m * 60;
        return m.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString("00.0", CultureInfo.InvariantCulture);
    }

    private static MultiPlatformSafeZoneService.SafeZone SafeZoneOf(SafePlatform p)
    {
        foreach (var z in Zones.All) if (z.Platform == p) return z;
        return Zones.All[0];
    }

    private static readonly MultiPlatformSafeZoneService Zones = new();

    private static QaInspectionReport Build(bool scanned, double dur, double peak, double rms, List<QaDefect> defects)
    {
        var verdict = scanned ? QaVerdict.Pass : QaVerdict.Warn;
        int warn = 0, reject = 0;
        foreach (var d in defects)
        {
            if (d.Severity == QaVerdict.Reject) reject++;
            else if (d.Severity == QaVerdict.Warn) warn++;
            if (d.Severity > verdict) verdict = d.Severity;
        }
        defects.Sort(static (a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        var msg = verdict switch
        {
            QaVerdict.Pass => "✅ QA 통과 · 결함 0",
            QaVerdict.Warn => "⚠️ QA 경고 · 경고 " + warn.ToString(CultureInfo.InvariantCulture) + "건(게시 가능)",
            _ => "⛔ QA 거부 · 거부 " + reject.ToString(CultureInfo.InvariantCulture) + "건 / 경고 " + warn.ToString(CultureInfo.InvariantCulture) + "건",
        };
        return new(verdict, scanned, dur, peak, rms, defects, msg, DateTimeOffset.Now);
    }

    private static QaInspectionReport Unscanned(string reason, QaSubtitleProbe? subtitles)
    {
        var defects = new List<QaDefect>(4);
        if (subtitles is not null) CheckSubtitles(subtitles, defects);
        var r = Build(false, 0, double.NegativeInfinity, double.NegativeInfinity, defects);
        return r with { Message = r.Message + " · " + reason };
    }

    private QaInspectionReport Publish(QaInspectionReport r)
    {
        Volatile.Write(ref _last, r);
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("VideoQa.OnChange", ex); }
        return r;
    }

    private static async Task RunScanAsync(string exe, string input, FfmpegQaLogParser parser, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in BuildArgs(input)) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        if (!proc.StartCompat()) throw new InvalidOperationException("ffmpeg 시작 실패");
        try
        {
            var drainOut = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                parser.Feed(line.AsSpan());
            await drainOut.ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.KillCompat(true); } catch { }
            throw;
        }
    }

    private static string F1(double v) => double.IsNegativeInfinity(v) ? "-inf" : v.ToString("0.0", CultureInfo.InvariantCulture);
}
