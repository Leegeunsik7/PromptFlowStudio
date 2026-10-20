using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Cad;

namespace PromptFlowStudio.Services.Trade;

// ═══════════════════════════════════════════════════════════════════════════════════
//  [PromptFlow Studio] v3.6.0 인터랙티브 피치 고도화 (클라우드 자율 고도화)
//    바이어 미팅에서 "렌더링 대기 없이" 즉석 시연하기 위한 두 개의 순수 C# BCL 계산 모듈.
//    ▸ StlExplodedView   : CAD 3D 분해도(exploded view) · 축(X/Y/Z) 제어 오버레이 계산기
//    ▸ TelemetrySimulator: 텔레메트리 HUD 실시간 경고 펄스(정상/경고/위험) 시뮬레이터
//    외부 3D/ML 라이브러리 0 — 기하/수치 계산만 수행하고 결과(수치·상태)를 UI(CSS)가 소비.
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>분해도 전개 축.</summary>
public enum ExplodeAxis { X, Y, Z }

/// <summary>분해도 1개 파트(축 방향 밴드) — 원본 중심, 전개 오프셋, 전개 후 중심.</summary>
public sealed record ExplodedPart(int Index, Vec3 Centroid, Vec3 Offset, Vec3 ExplodedCentroid);

/// <summary>
/// [StlExplodedView] STL 메시를 전개 축 기준으로 N개 밴드(파트)로 분할하고, 각 파트를 조립 중심에서
/// 바깥으로 밀어내는 <b>분해도(exploded view)</b>를 계산한다(순수 기하 · 외부 라이브러리 0).
///   ▸ <see cref="Explode"/> — 실제 <see cref="CadMesh"/> 정점을 축 좌표로 밴드 분할 → 파트별 중심 이동량 산출.
///   ▸ <see cref="FaceOffsetPx"/> — 피치 캔버스의 CSS 3D 큐브 데모용 면(面) 전개 거리(px) 매핑.
///   ▸ factor 0 → 완전 조립(오프셋 0), factor 1 → 최대 전개. 중심 기준 대칭 전개(부호 보존).
/// </summary>
public static class StlExplodedView
{
    /// <summary>CSS 데모 큐브의 최대 면 전개 거리(px) — factor=1 일 때.</summary>
    public const double DemoSpreadPx = 90.0;

    /// <summary>전개 시 파트 이동량 배율(중심 대비 오프셋 확대) — 시각적 분리 확보.</summary>
    private const float SpreadGain = 1.5f;

    /// <summary>UI 축 토글용 (축, 한글 라벨) 목록.</summary>
    public static readonly IReadOnlyList<(ExplodeAxis Axis, string Label)> Axes = new[]
    {
        (ExplodeAxis.X, "X축 좌우"),
        (ExplodeAxis.Y, "Y축 상하"),
        (ExplodeAxis.Z, "Z축 전후"),
    };

    /// <summary>축 한글 라벨.</summary>
    public static string AxisLabel(ExplodeAxis axis) => axis switch
    {
        ExplodeAxis.X => "X축 좌우",
        ExplodeAxis.Y => "Y축 상하",
        _ => "Z축 전후",
    };

    /// <summary>축 단위 벡터.</summary>
    public static Vec3 AxisVector(ExplodeAxis axis) => axis switch
    {
        ExplodeAxis.X => new Vec3(1f, 0f, 0f),
        ExplodeAxis.Y => new Vec3(0f, 1f, 0f),
        _ => new Vec3(0f, 0f, 1f),
    };

    /// <summary>CSS 데모 큐브의 면 전개 거리(px) — factor(0..1) 선형 매핑.</summary>
    public static double FaceOffsetPx(double factor) => Clamp01(factor) * DemoSpreadPx;

    /// <summary>축 방향 좌표 추출.</summary>
    private static float Coord(Vec3 v, ExplodeAxis axis) => axis switch
    {
        ExplodeAxis.X => v.X,
        ExplodeAxis.Y => v.Y,
        _ => v.Z,
    };

    /// <summary>
    /// 메시 정점을 전개 축 좌표 기준으로 <paramref name="parts"/>개 밴드로 나누고, 각 밴드 중심을
    /// 조립 중심에서 축 방향으로 밀어낸 분해도를 계산한다(빈 밴드는 제외 · 무효 메시는 빈 목록).
    /// </summary>
    public static IReadOnlyList<ExplodedPart> Explode(CadMesh? mesh, ExplodeAxis axis, double factor, int parts = 4)
    {
        if (mesh is null || mesh.Vertices.Count == 0) return Array.Empty<ExplodedPart>();

        var f = (float)Clamp01(factor);
        var band = Math.Max(1, parts);

        // 축 좌표 범위(정점 기반) — 밴드 경계 산정.
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in mesh.Vertices)
        {
            var c = Coord(v, axis);
            if (c < min) min = c;
            if (c > max) max = c;
        }
        var range = max - min;

        // 밴드별 정점 누적(합/개수) → 중심.
        var sum = new Vec3[band];
        var cnt = new int[band];
        foreach (var v in mesh.Vertices)
        {
            int b = range <= 0 ? 0 : (int)((Coord(v, axis) - min) / range * band);
            if (b >= band) b = band - 1;
            if (b < 0) b = 0;
            sum[b] += v;
            cnt[b]++;
        }

        var center = mesh.Center;
        var centerCoord = Coord(center, axis);
        var unit = AxisVector(axis);

        var result = new List<ExplodedPart>(band);
        for (int b = 0; b < band; b++)
        {
            if (cnt[b] == 0) continue;
            var centroid = sum[b] * (1f / cnt[b]);
            var displace = (Coord(centroid, axis) - centerCoord) * f * SpreadGain;   // 중심 대비 부호 보존
            var offset = unit * displace;
            result.Add(new ExplodedPart(b, centroid, offset, centroid + offset));
        }
        return result;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}

// ─────────────────────────────────────────────────────────────────────────────────

/// <summary>텔레메트리 경고 수준.</summary>
public enum TelemetryLevel { Normal, Warn, Critical }

/// <summary>경고 펄스 1틱 — 수준/정규화값(0..1)/펄스강도(0..1)/한글 라벨/색상 HEX.</summary>
public sealed record TelemetryPulse(TelemetryLevel Level, double Norm, double Pulse, string Label, string Hex);

/// <summary>
/// [TelemetrySimulator] 텔레메트리 HUD의 <b>실시간 경고 펄스</b>를 계산하는 순수 BCL 시뮬레이터.
///   ▸ <see cref="Classify"/>       — 값 → 정상/경고/위험 판정(임계 warn/crit).
///   ▸ <see cref="PulseIntensity"/> — 경고 구간에서 0→1 로 상승하는 펄스 강도(위험 진입 후 가속).
///   ▸ <see cref="Evaluate"/>       — 프레젠터가 슬라이더를 움직이면 즉시 펄스(색/강도/라벨) 산출.
///   ▸ <see cref="Waveform"/> / <see cref="Simulate"/> — 결정론적 데모 파형/타임라인(재생용).
/// UI 는 반환된 색상(Hex)·강도(Pulse)를 CSS 변수로 받아 발광/펄스 애니메이션을 구동한다.
/// </summary>
public static class TelemetrySimulator
{
    /// <summary>정상/경고/위험 판정(warn≤값&lt;crit → 경고, 값≥crit → 위험).</summary>
    public static TelemetryLevel Classify(double value, double warn, double crit)
    {
        if (crit <= warn) crit = warn + 1;               // 방어(임계 역전)
        if (value >= crit) return TelemetryLevel.Critical;
        if (value >= warn) return TelemetryLevel.Warn;
        return TelemetryLevel.Normal;
    }

    /// <summary>펄스 강도(0..1) — 정상 0, 경고 구간 0→0.6 선형, 위험 진입 후 0.6→1.0 가속.</summary>
    public static double PulseIntensity(double value, double warn, double crit)
    {
        if (crit <= warn) crit = warn + 1;
        if (value < warn) return 0.0;
        var span = crit - warn;
        if (value < crit) return 0.6 * ((value - warn) / span);
        var over = (value - crit) / span;               // 위험 초과분(임계폭 기준)
        return Math.Min(1.0, 0.6 + 0.4 * over);
    }

    /// <summary>정규화값(0..1) — 게이지 채움 비율용.</summary>
    public static double Normalize(double value, double max)
    {
        if (max <= 0) return 0.0;
        var n = value / max;
        return n < 0 ? 0 : n > 1 ? 1 : n;
    }

    /// <summary>수준 한글 라벨.</summary>
    public static string LevelLabel(TelemetryLevel level) => level switch
    {
        TelemetryLevel.Critical => "위험",
        TelemetryLevel.Warn => "경고",
        _ => "정상",
    };

    /// <summary>수준 색상 HEX(정상 green / 경고 amber / 위험 red).</summary>
    public static string LevelHex(TelemetryLevel level) => level switch
    {
        TelemetryLevel.Critical => "#ef4444",
        TelemetryLevel.Warn => "#f59e0b",
        _ => "#22c55e",
    };

    /// <summary>현재 값 → 경고 펄스(색/강도/라벨) 즉시 산출.</summary>
    public static TelemetryPulse Evaluate(double value, double warn, double crit, double max)
    {
        var level = Classify(value, warn, crit);
        return new TelemetryPulse(level, Normalize(value, max), PulseIntensity(value, warn, crit), LevelLabel(level), LevelHex(level));
    }

    /// <summary>결정론적 데모 파형(사인 기반) — 재생용 샘플 n개([min,max] 범위, seed 로 위상 이동).</summary>
    public static IReadOnlyList<double> Waveform(int n, double min, double max, int seed = 0)
    {
        if (n <= 0) return Array.Empty<double>();
        if (max < min) (min, max) = (max, min);
        var amp = (max - min) / 2.0;
        var mid = min + amp;
        var phase = seed * 0.37;
        var outp = new double[n];
        for (int i = 0; i < n; i++)
        {
            // 이중 사인 합성으로 완만한 상승/스파이크 혼합(경고·위험 구간을 결정론적으로 포함).
            var t = (double)i / Math.Max(1, n - 1);
            var wave = 0.65 * Math.Sin(2 * Math.PI * (t * 1.5) + phase) + 0.35 * Math.Sin(2 * Math.PI * (t * 4.0) + phase);
            outp[i] = mid + amp * wave;
        }
        return outp;
    }

    /// <summary>파형을 경고 타임라인(프레임별 <see cref="TelemetryPulse"/>)으로 변환(재생 시연용).</summary>
    public static IReadOnlyList<TelemetryPulse> Simulate(IReadOnlyList<double> samples, double warn, double crit, double max)
    {
        if (samples is null || samples.Count == 0) return Array.Empty<TelemetryPulse>();
        return samples.Select(v => Evaluate(v, warn, crit, max)).ToList();
    }
}
