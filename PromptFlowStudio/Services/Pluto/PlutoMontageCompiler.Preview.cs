using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎨 브라우저 <c>ctx.filter</c> 근사 색보정 — brightness · contrast · saturate 배율 + 웜 틴트 오버레이 알파.</summary>
public sealed record PlutoPreviewColorGrade(double Brightness, double Contrast, double Saturation, double WarmTintAlpha);

/// <summary>🎞 멀티컷 프리뷰 1클립 — 출력 타임라인 [StartSec, EndSec) · 다음 클립으로의 xfade(초) · 클립 로컬 시간 기준 궤적 계약.</summary>
public sealed record PlutoMontagePreviewClip(int Index, string ImageUrl, double StartSec, double EndSec, double TransitionOutSec, string TransitionType,
    PlutoMotionPreviewContract Trajectory, PlutoPreviewColorGrade ColorGrade);

/// <summary>🎞 멀티컷 몽타주 프리뷰 계약 — <c>pluto-motion-previewer.js</c> <c>setMontagePlan</c> 이 소비.</summary>
public sealed record PlutoMontagePreviewPlan(double TotalDurationSec, IReadOnlyList<PlutoMontagePreviewClip> Clips, IReadOnlyList<double> BeatTimestampsSec)
{
    public const int CurrentVersion = 1;

    /// <summary>프리뷰 JSON(camelCase · 리플렉션 없는 <see cref="Utf8JsonWriter"/>).</summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteNumber("totalDurationSec", TotalDurationSec);
            w.WriteStartArray("clips");
            for (var i = 0; i < Clips.Count; i++)
            {
                var c = Clips[i];
                w.WriteStartObject();
                w.WriteNumber("index", c.Index);
                w.WriteString("imageUrl", c.ImageUrl);
                w.WriteNumber("startSec", c.StartSec);
                w.WriteNumber("endSec", c.EndSec);
                w.WriteNumber("transitionOutSec", c.TransitionOutSec);
                w.WriteString("transitionType", c.TransitionType);
                w.WritePropertyName("trajectory");
                w.WriteRawValue(PlutoMotionPreviewModel.ToJson(c.Trajectory), skipInputValidation: true);
                w.WriteStartObject("colorGrade");
                w.WriteNumber("brightness", c.ColorGrade.Brightness);
                w.WriteNumber("contrast", c.ColorGrade.Contrast);
                w.WriteNumber("saturation", c.ColorGrade.Saturation);
                w.WriteNumber("warmTintAlpha", c.ColorGrade.WarmTintAlpha);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("beats");
            for (var i = 0; i < BeatTimestampsSec.Count; i++)
                if (double.IsFinite(BeatTimestampsSec[i])) w.WriteNumberValue(BeatTimestampsSec[i]);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

public sealed partial class PlutoMontageCompiler
{
    /// <summary>🎨 FFmpeg 색보정 프로파일 → 브라우저 필터 스칼라(NaN/∞ → 중립).</summary>
    public static PlutoPreviewColorGrade ToPreviewColorGrade(PlutoColorGradeProfile? profile)
    {
        if (profile is null) return new PlutoPreviewColorGrade(1.0, 1.0, 1.0, 0.0);
        static double F(double v, double fallback) => double.IsFinite(v) ? v : fallback;
        return new PlutoPreviewColorGrade(
            Math.Max(0, 1.0 + F(profile.BrightnessOffset, 0)),
            Math.Max(0, F(profile.ContrastMultiplier, 1)),
            Math.Max(0, F(profile.SaturationMultiplier, 1)),
            Math.Clamp(Math.Max(0, F(profile.RedShadowShift, 0) * 0.8), 0, 1));
    }

    public PlutoMontagePreviewPlan BuildMontagePreviewPlan(PlutoMontagePlan plan, IReadOnlyList<string> imageUrls, IReadOnlyList<double>? beatTimestampsSec = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(imageUrls);
        var fps = plan.Fps > 0 ? plan.Fps : PlutoMotionCompiler.DefaultFps;
        var clips = new PlutoMontagePreviewClip[plan.Clips.Count];
        for (var i = 0; i < clips.Length; i++)
        {
            var c = plan.Clips[i];
            var url = i < imageUrls.Count && imageUrls[i] is { } u ? u : "";
            clips[i] = new PlutoMontagePreviewClip(c.Index, url, c.StartSec, c.StartSec + c.DurationSec, c.TransitionOutSec, c.TransitionType,
                PlutoMotionCompiler.ExportPreviewContract(c.Trajectory, fps: fps), ToPreviewColorGrade(c.ColorProfile));
        }
        IReadOnlyList<double> beats = beatTimestampsSec is { Count: > 0 } b ? Copy(b) : Array.Empty<double>();
        return new PlutoMontagePreviewPlan(plan.TotalDurationSec, clips, beats);
    }

    private static double[] Copy(IReadOnlyList<double> src)
    {
        var a = new double[src.Count];
        for (var i = 0; i < a.Length; i++) a[i] = src[i];
        return a;
    }
}
