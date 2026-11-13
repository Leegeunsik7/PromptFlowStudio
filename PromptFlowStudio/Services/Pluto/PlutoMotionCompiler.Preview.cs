using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞 실시간 프리뷰 계약 1구간 — <see cref="PlutoMotionCompiler"/> 내부 정규화 세그먼트(프레임 격자 스냅 · 6자리 양자화 · 초점 체인)의 공개 스냅샷.</summary>
public sealed record PlutoMotionPreviewSegment
{
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public double Z0 { get; init; }
    public double Z1 { get; init; }
    public double X0 { get; init; }
    public double Y0 { get; init; }
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double OrbitX { get; init; }
    public double OrbitY { get; init; }
    public double OrbitOmega { get; init; }
    public PlutoEasingFunction Easing { get; init; }
    /// <summary>이징 다항식 계수 c0..c5 — <c>S(u) = Σ c_k·u^k</c>(JS 는 Horner 로 평가 → 이징 종류 분기 없이 C# 과 동일 곡선).</summary>
    public double[] EaseCoefficients { get; init; } = Array.Empty<double>();
}

/// <summary>🎞 실시간 프리뷰 계약 — <c>wwwroot/js/pluto-motion-previewer.js</c> 가 소비하는 JSON 의 C# 원본.</summary>
public sealed record PlutoMotionPreviewContract
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public int Width { get; init; } = PlutoMotionCompiler.DefaultWidth;
    public int Height { get; init; } = PlutoMotionCompiler.DefaultHeight;
    public int Fps { get; init; } = PlutoMotionCompiler.DefaultFps;
    public double DurationSec { get; init; }
    public double MinZoom { get; init; } = PlutoMotionCompiler.MinZoom;
    public double MaxZoom { get; init; } = PlutoMotionCompiler.MaxZoom;
    public IReadOnlyList<PlutoMotionPreviewSegment> Segments { get; init; } = Array.Empty<PlutoMotionPreviewSegment>();
    /// <summary>🔔 SFX 큐(비어 있음 → JS 기본 15초 큐 시트 · 비트 싱크 시 스냅된 큐).</summary>
    public IReadOnlyList<PlutoMotionPreviewSfx> Sfx { get; init; } = Array.Empty<PlutoMotionPreviewSfx>();
}

/// <summary>🔔 프리뷰 SFX 1큐 — <c>Kind</c> = <see cref="PlutoSfxType"/> 순번(JS <c>synthSfx</c> 인덱스) · 시작 초 · 볼륨.</summary>
public readonly record struct PlutoMotionPreviewSfx(int Kind, double AtSec, double Volume);

/// <summary>🥁 비트 싱크 프리뷰 — 스냅된 궤적/SFX 큐 + 검출 BPM · 비트 수. <see cref="Applied"/> = false → 원본 그대로(폴백 격자).</summary>
public sealed record PlutoBeatSyncedPreview(PlutoMotionTrajectory Trajectory, IReadOnlyList<PlutoSfxCue> SfxCues, double Bpm, int BeatCount, bool Applied);

/// <summary>🎞 프리뷰 1샘플 — 줌 · 초점 · 클램프된 뷰포트 좌상단(모두 프레임 대비 정규화 0~1).</summary>
public readonly record struct PlutoMotionPreviewSample(double Zoom, double FocalX, double FocalY, double X, double Y);

/// <summary>💬 프리뷰 자막 1막(초 단위 표시 구간 + KO/EN).</summary>
public readonly record struct PlutoMotionPreviewSubtitle(double StartSec, double EndSec, string Korean, string English);

public static partial class PlutoMotionCompiler
{
    /// <summary>이징 다항식 계수 c0..c5(<see cref="Ease"/> 와 동일 곡선).</summary>
    public static double[] EaseCoefficients(PlutoEasingFunction easing) => easing switch
    {
        PlutoEasingFunction.Linear => new[] { 0.0, 1.0, 0.0, 0.0, 0.0, 0.0 },
        PlutoEasingFunction.HermiteSmootherstep => new[] { 0.0, 0.0, 0.0, 10.0, -15.0, 6.0 },
        _ => new[] { 0.0, 0.0, 3.0, -2.0, 0.0, 0.0 },
    };

    /// <summary>🎞 궤적 → 프리뷰 계약(FFmpeg 수식과 같은 <c>Resolve</c> 결과를 그대로 노출 → 프리뷰/렌더 수학적 동치).</summary>
    public static PlutoMotionPreviewContract ExportPreviewContract(PlutoMotionTrajectory? trajectory, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps,
        IReadOnlyList<PlutoSfxCue>? sfxCues = null)
    {
        width = ClampDim(width);
        height = ClampDim(height);
        fps = ClampFps(fps);
        var segs = Resolve(trajectory, width, height, fps);
        var list = new PlutoMotionPreviewSegment[segs.Length];
        for (var i = 0; i < segs.Length; i++)
        {
            ref readonly var s = ref segs[i];
            list[i] = new PlutoMotionPreviewSegment
            {
                StartFrame = s.StartFrame, EndFrame = s.EndFrame,
                StartSec = s.StartFrame / (double)fps, EndSec = s.EndFrame / (double)fps,
                Z0 = s.Z0, Z1 = s.Z1, X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1,
                OrbitX = s.OrbitX, OrbitY = s.OrbitY, OrbitOmega = s.OrbitOmega,
                Easing = s.Easing, EaseCoefficients = EaseCoefficients(s.Easing),
            };
        }
        return new PlutoMotionPreviewContract
        {
            Width = width, Height = height, Fps = fps,
            DurationSec = segs.Length == 0 ? 0.0 : segs[^1].EndFrame / (double)fps,
            Segments = list,
            Sfx = ToPreviewSfx(sfxCues),
        };
    }

    private static PlutoMotionPreviewSfx[] ToPreviewSfx(IReadOnlyList<PlutoSfxCue>? cues)
    {
        if (cues is not { Count: > 0 }) return Array.Empty<PlutoMotionPreviewSfx>();
        var arr = new PlutoMotionPreviewSfx[cues.Count];
        for (var i = 0; i < arr.Length; i++)
        {
            var c = cues[i];
            arr[i] = new PlutoMotionPreviewSfx((int)c.SfxType, Math.Max(0, c.OffsetMs) / 1000.0,
                Math.Clamp(double.IsFinite(c.VolumeRatio) ? c.VolumeRatio : 0, 0, 1));
        }
        return arr;
    }

    /// <summary>🎞 프리뷰 계약 JSON(camelCase · 리플렉션 없는 <see cref="Utf8JsonWriter"/> — 트리밍/AOT 안전).</summary>
    public static string ExportPreviewContractJson(PlutoMotionTrajectory? trajectory, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps,
        IReadOnlyList<PlutoSfxCue>? sfxCues = null)
        => PlutoMotionPreviewModel.ToJson(ExportPreviewContract(trajectory, width, height, fps, sfxCues));

    /// <summary>
    /// 🥁 BGM WAV → 비트 그리드 → 막 경계 · SFX 큐 스냅(익스포터 <c>TryBeatSyncTrajectoryAsync</c> 와 동일 규칙 → 프리뷰/렌더 컷 일치).
    /// 파싱 실패 · 무음 · 폴백 격자 → <see cref="PlutoBeatSyncedPreview.Applied"/> = false(원본 궤적 · 기본 큐).
    /// </summary>
    public static PlutoBeatSyncedPreview BuildBeatSyncedPreview(PlutoMotionTrajectory? baseTrajectory, ReadOnlySpan<byte> bgmWav,
        Dsp.PlutoAudioBeatDetector? detector = null, Dsp.IPlutoBeatSyncQuantizer? quantizer = null)
    {
        var traj = baseTrajectory ?? PlutoMotionTrajectory.Empty;
        var dur = traj.DurationSec;
        var baseCues = PlutoSfxDirector.Shared.BuildCueSheet(dur > 0 ? dur : FourActDurationSec);
        var grid = (detector ?? Dsp.PlutoAudioBeatDetector.Shared).Analyze(bgmWav);
        if (grid.IsFallback || grid.Beats.Count == 0) return new PlutoBeatSyncedPreview(traj, baseCues, 0, 0, false);
        var q = quantizer ?? Dsp.PlutoBeatSyncQuantizer.Shared;
        return new PlutoBeatSyncedPreview(q.QuantizeTrajectory(traj, grid.Beats), q.QuantizeSfxCues(baseCues, grid.Beats), grid.Bpm, grid.Beats.Count, true);
    }

    /// <summary>🎵 기지 비트(커스텀 BGM 하이라이트 상대 비트) → 막 경계 · SFX 큐 스냅. 빈 비트 → Applied=false(원본 궤적 · 기본 큐).</summary>
    public static PlutoBeatSyncedPreview BuildBeatSyncedPreview(PlutoMotionTrajectory? baseTrajectory, IReadOnlyList<double>? beats, double bpm,
        Dsp.IPlutoBeatSyncQuantizer? quantizer = null)
    {
        var traj = baseTrajectory ?? PlutoMotionTrajectory.Empty;
        var dur = traj.DurationSec;
        var baseCues = PlutoSfxDirector.Shared.BuildCueSheet(dur > 0 ? dur : FourActDurationSec);
        if (beats is not { Count: > 0 }) return new PlutoBeatSyncedPreview(traj, baseCues, 0, 0, false);
        var q = quantizer ?? Dsp.PlutoBeatSyncQuantizer.Shared;
        return new PlutoBeatSyncedPreview(q.QuantizeTrajectory(traj, beats), q.QuantizeSfxCues(baseCues, beats), bpm, beats.Count, true);
    }
}

/// <summary>
/// 🎞 프리뷰어 궤적 모델 — <c>pluto-motion-previewer.js</c> 의 <c>evalInto</c> 와 줄 단위로 같은 연속 시간 평가기(무할당).
/// 프레임 격자(<c>t = on / fps</c>)에서 <see cref="PlutoMotionCompiler.EvaluateFrame"/> · FFmpeg zoompan 수식과 일치한다.
/// </summary>
public static class PlutoMotionPreviewModel
{
    /// <summary>프리뷰 3막 자막 구간(초) — Act 1 0-4 · Act 2 4-12 · Act 3 12-15.</summary>
    public static readonly (double Start, double End)[] SubtitleWindows = { (0, 4), (4, 12), (12, 15) };
    /// <summary>막 사이 알파 크로스페이드 길이(초).</summary>
    public const double CrossfadeSec = 0.150;
    /// <summary>세그먼트 없는 궤적의 프리뷰 재생 길이(초).</summary>
    public const double DefaultDurationSec = PlutoMotionCompiler.FourActDurationSec;

    /// <summary>재생 시각 <paramref name="tSec"/> 의 줌/초점/뷰포트(정규화).</summary>
    public static PlutoMotionPreviewSample Evaluate(PlutoMotionPreviewContract contract, double tSec)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var segs = contract.Segments;
        double zoom = contract.MinZoom, fx = 0.5, fy = 0.5;
        var n = segs.Count;
        if (n > 0)
        {
            if (!double.IsFinite(tSec)) tSec = 0;
            var i = 0;
            while (i < n - 1 && tSec > segs[i].EndSec) i++;   // if(lte(on,End_i), …) 와 같은 선택
            var s = segs[i];
            var span = s.EndSec - s.StartSec;
            var u = span > 0 ? Math.Clamp((tSec - s.StartSec) / span, 0.0, 1.0) : 1.0;
            var e = Horner(s.EaseCoefficients, u);
            zoom = s.Z0 + (s.Z1 - s.Z0) * e;
            fx = s.X0 + (s.X1 - s.X0) * e;
            fy = s.Y0 + (s.Y1 - s.Y0) * e;
            if (s.OrbitX > 0)
            {
                var env = Math.Sin(Math.PI * u);
                var ang = s.OrbitOmega * u;
                fx += s.OrbitX * env * Math.Cos(ang);
                fy += s.OrbitY * env * Math.Sin(ang);
            }
        }
        zoom = Math.Max(contract.MinZoom, Math.Min(contract.MaxZoom, zoom));
        var view = 1.0 / zoom;   // 뷰포트 변(정규화) — 가로/세로 동일 비율
        var x = Math.Max(0.0, Math.Min(1.0 - view, fx - view / 2));
        var y = Math.Max(0.0, Math.Min(1.0 - view, fy - view / 2));
        return new PlutoMotionPreviewSample(zoom, fx, fy, x, y);
    }

    /// <summary>다항식 Horner 평가(계수 c0..cN).</summary>
    public static double Horner(double[] coefficients, double u)
    {
        if (coefficients is null || coefficients.Length == 0) return u;
        var acc = 0.0;
        for (var k = coefficients.Length - 1; k >= 0; k--) acc = acc * u + coefficients[k];
        return acc;
    }

    /// <summary>💬 편지 스크립트 → 프리뷰 3막(구간은 <see cref="SubtitleWindows"/>). null → 빈 배열.</summary>
    public static PlutoMotionPreviewSubtitle[] BuildSubtitles(PlutoSubtitleScript? script)
    {
        if (script is null) return Array.Empty<PlutoMotionPreviewSubtitle>();
        return new[]
        {
            new PlutoMotionPreviewSubtitle(SubtitleWindows[0].Start, SubtitleWindows[0].End, script.Act1Text ?? "", script.Act1En ?? ""),
            new PlutoMotionPreviewSubtitle(SubtitleWindows[1].Start, SubtitleWindows[1].End, script.Act2Text ?? "", script.Act2En ?? ""),
            new PlutoMotionPreviewSubtitle(SubtitleWindows[2].Start, SubtitleWindows[2].End, script.Act3Text ?? "", script.Act3En ?? ""),
        };
    }

    /// <summary>
    /// 💬 막 <paramref name="index"/> 의 자막 알파(0~1) — 인접 막 경계를 중심으로 <see cref="CrossfadeSec"/> 선형 크로스페이드
    /// (두 막 알파 합 = 1). 타임라인 양 끝(첫 막 시작 · 마지막 막 종료)은 페이드 없음.
    /// </summary>
    public static double SubtitleAlpha(IReadOnlyList<PlutoMotionPreviewSubtitle> acts, int index, double tSec)
    {
        if (acts is null || (uint)index >= (uint)acts.Count) return 0;
        const double h = CrossfadeSec / 2;
        var a = acts[index];
        var fadeIn = index == 0 ? (tSec >= a.StartSec ? 1.0 : 0.0) : Math.Clamp((tSec - (a.StartSec - h)) / CrossfadeSec, 0.0, 1.0);
        var fadeOut = index == acts.Count - 1 ? (tSec <= a.EndSec ? 1.0 : 0.0) : Math.Clamp(((a.EndSec + h) - tSec) / CrossfadeSec, 0.0, 1.0);
        return fadeIn * fadeOut;
    }

    /// <summary>프리뷰 계약 JSON.</summary>
    public static string ToJson(PlutoMotionPreviewContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", contract.Version);
            w.WriteNumber("width", contract.Width);
            w.WriteNumber("height", contract.Height);
            w.WriteNumber("fps", contract.Fps);
            w.WriteNumber("durationSec", contract.DurationSec);
            w.WriteNumber("minZoom", contract.MinZoom);
            w.WriteNumber("maxZoom", contract.MaxZoom);
            w.WriteStartArray("segments");
            for (var i = 0; i < contract.Segments.Count; i++)
            {
                var s = contract.Segments[i];
                w.WriteStartObject();
                w.WriteNumber("startFrame", s.StartFrame);
                w.WriteNumber("endFrame", s.EndFrame);
                w.WriteNumber("startSec", s.StartSec);
                w.WriteNumber("endSec", s.EndSec);
                w.WriteNumber("z0", s.Z0);
                w.WriteNumber("z1", s.Z1);
                w.WriteNumber("x0", s.X0);
                w.WriteNumber("y0", s.Y0);
                w.WriteNumber("x1", s.X1);
                w.WriteNumber("y1", s.Y1);
                w.WriteNumber("orbitX", s.OrbitX);
                w.WriteNumber("orbitY", s.OrbitY);
                w.WriteNumber("orbitOmega", s.OrbitOmega);
                w.WriteString("easing", s.Easing.ToString());
                w.WriteStartArray("ease");
                for (var k = 0; k < s.EaseCoefficients.Length; k++) w.WriteNumberValue(s.EaseCoefficients[k]);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (contract.Sfx.Count > 0)   // 🔔 비트 스냅 큐(없으면 키 생략 → JS 기본 큐)
            {
                w.WriteStartArray("sfx");
                for (var i = 0; i < contract.Sfx.Count; i++)
                {
                    var f = contract.Sfx[i];
                    w.WriteStartObject();
                    w.WriteNumber("kind", f.Kind);
                    w.WriteNumber("at", f.AtSec);
                    w.WriteNumber("vol", f.Volume);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>프리뷰 자막 JSON(<c>{"crossfadeSec":0.15,"acts":[{startSec,endSec,ko,en}…]}</c>).</summary>
    public static string SubtitlesJson(PlutoSubtitleScript? script) => WriteSubtitlesJson(BuildSubtitles(script));

    /// <summary>🎞 정밀 큐(몽타주 클립 경계 정렬 · 소수 초) → 프리뷰 자막 JSON. 유효 큐만(빈 KO · 역전 구간 제외).</summary>
    public static string CueSubtitlesJson(IReadOnlyList<PromptFlowStudio.Services.Export.SubtitleCue>? cues) => WriteSubtitlesJson(BuildCueSubtitles(cues));

    /// <summary>🎞 정밀 큐 → 프리뷰 자막 배열(실효 초 · null/빈 → 빈 배열).</summary>
    public static PlutoMotionPreviewSubtitle[] BuildCueSubtitles(IReadOnlyList<PromptFlowStudio.Services.Export.SubtitleCue>? cues)
    {
        if (cues is null || cues.Count == 0) return Array.Empty<PlutoMotionPreviewSubtitle>();
        var n = 0;
        for (var i = 0; i < cues.Count; i++)
            if (!string.IsNullOrWhiteSpace(cues[i].Text) && cues[i].EffectiveEndSec > cues[i].EffectiveStartSec) n++;
        var arr = new PlutoMotionPreviewSubtitle[n];
        n = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EffectiveEndSec <= c.EffectiveStartSec) continue;
            arr[n++] = new PlutoMotionPreviewSubtitle(c.EffectiveStartSec, c.EffectiveEndSec, c.Text, c.SecondaryText ?? "");
        }
        return arr;
    }

    private static string WriteSubtitlesJson(PlutoMotionPreviewSubtitle[] acts)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("crossfadeSec", CrossfadeSec);
            w.WriteStartArray("acts");
            for (var i = 0; i < acts.Length; i++)
            {
                w.WriteStartObject();
                w.WriteNumber("startSec", acts[i].StartSec);
                w.WriteNumber("endSec", acts[i].EndSec);
                w.WriteString("ko", acts[i].Korean);
                w.WriteString("en", acts[i].English);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>프리뷰 계약 JSON 역직렬화(스키마 즉시 검증 — 버전/필드 누락 → <see cref="JsonException"/> 또는 <see cref="KeyNotFoundException"/>).</summary>
    public static PlutoMotionPreviewContract FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != PlutoMotionPreviewContract.CurrentVersion)
            throw new JsonException("Unsupported Pluto motion preview contract version.");
        var arr = root.GetProperty("segments");
        var list = new PlutoMotionPreviewSegment[arr.GetArrayLength()];
        var idx = 0;
        foreach (var s in arr.EnumerateArray())
        {
            var ease = s.GetProperty("ease");
            var coeff = new double[ease.GetArrayLength()];
            var k = 0;
            foreach (var c in ease.EnumerateArray()) coeff[k++] = c.GetDouble();
            if (!Enum.TryParse<PlutoEasingFunction>(s.GetProperty("easing").GetString(), out var easing))
                throw new JsonException("Unknown easing function.");
            list[idx++] = new PlutoMotionPreviewSegment
            {
                StartFrame = s.GetProperty("startFrame").GetInt32(), EndFrame = s.GetProperty("endFrame").GetInt32(),
                StartSec = s.GetProperty("startSec").GetDouble(), EndSec = s.GetProperty("endSec").GetDouble(),
                Z0 = s.GetProperty("z0").GetDouble(), Z1 = s.GetProperty("z1").GetDouble(),
                X0 = s.GetProperty("x0").GetDouble(), Y0 = s.GetProperty("y0").GetDouble(),
                X1 = s.GetProperty("x1").GetDouble(), Y1 = s.GetProperty("y1").GetDouble(),
                OrbitX = s.GetProperty("orbitX").GetDouble(), OrbitY = s.GetProperty("orbitY").GetDouble(),
                OrbitOmega = s.GetProperty("orbitOmega").GetDouble(),
                Easing = easing, EaseCoefficients = coeff,
            };
        }
        var sfx = Array.Empty<PlutoMotionPreviewSfx>();
        if (root.TryGetProperty("sfx", out var sfxArr))
        {
            sfx = new PlutoMotionPreviewSfx[sfxArr.GetArrayLength()];
            var j = 0;
            foreach (var f in sfxArr.EnumerateArray())
                sfx[j++] = new PlutoMotionPreviewSfx(f.GetProperty("kind").GetInt32(), f.GetProperty("at").GetDouble(), f.GetProperty("vol").GetDouble());
        }
        return new PlutoMotionPreviewContract
        {
            Sfx = sfx,
            Version = root.GetProperty("version").GetInt32(),
            Width = root.GetProperty("width").GetInt32(), Height = root.GetProperty("height").GetInt32(), Fps = root.GetProperty("fps").GetInt32(),
            DurationSec = root.GetProperty("durationSec").GetDouble(),
            MinZoom = root.GetProperty("minZoom").GetDouble(), MaxZoom = root.GetProperty("maxZoom").GetDouble(),
            Segments = list,
        };
    }
}
