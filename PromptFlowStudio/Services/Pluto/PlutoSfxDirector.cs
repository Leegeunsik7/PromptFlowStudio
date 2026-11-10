using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔔 플루토 시네마틱 효과음 종류(코드 합성 · 외부 에셋 0).</summary>
public enum PlutoSfxType { ShutterClick, ChimeSparkle, BedtimeTone }

/// <summary>🔔 타임라인 효과음 큐 — 종류 · 시작 오프셋(ms) · 볼륨 배율(0~1).</summary>
public readonly record struct PlutoSfxCue(PlutoSfxType SfxType, int OffsetMs, double VolumeRatio);

/// <summary>
/// 🔔 플루토 타임라인 SFX 사운드 디렉터 — 15초 아기/반려동물 3막 서사(1막 인트로 · 2막 미소 클라이맥스 · 3막 잠자리 엔딩)에 맞춘 큐 시트.
/// 상태 없음(스레드 안전) · DI 싱글톤은 <see cref="Shared"/> 를 등록해 내보내기와 동일 인스턴스 공유.
/// </summary>
public sealed class PlutoSfxDirector
{
    public static readonly PlutoSfxDirector Shared = new();

    /// <summary>🔔 큐 볼륨 하한/상한(시네마틱 범위 — BGM 0.25 대비 존재감 · 클리핑 여유).</summary>
    public const double MinVolumeRatio = 0.3, MaxVolumeRatio = 0.85;

    private static readonly PlutoSfxCue[] s_default15s =
    {
        new(PlutoSfxType.ShutterClick, 800, 0.7),     // 1막 인트로 — 카메라 포커스 · 셔터
        new(PlutoSfxType.ChimeSparkle, 5500, 0.6),    // 2막 클라이맥스 미소 — 오르골 차임
        new(PlutoSfxType.BedtimeTone, 13200, 0.55),   // 3막 엔딩 — 따뜻한 잠자리 벨
    };

    /// <summary>🎬 기본 15초 3막 큐 시트(800ms · 5500ms · 13200ms).</summary>
    public IReadOnlyList<PlutoSfxCue> BuildDefault15sCueSheet() => s_default15s;

    /// <summary>⏱️ 영상 길이 맞춤 큐 시트 — 길이 미상(≤0)이면 기본 전체, 기지 시 효과음이 길이 안에서 끝나는 큐만.</summary>
    public IReadOnlyList<PlutoSfxCue> BuildCueSheet(double durationSec)
    {
        if (!double.IsFinite(durationSec) || durationSec <= 0) return s_default15s;
        var limitMs = durationSec * 1000.0;
        var keep = 0;
        for (var i = 0; i < s_default15s.Length; i++)
            if (s_default15s[i].OffsetMs + PlutoAudioSynthesizer.DurationMs(s_default15s[i].SfxType) <= limitMs) keep++;
        if (keep == s_default15s.Length) return s_default15s;
        var list = new PlutoSfxCue[keep];
        for (int i = 0, k = 0; i < s_default15s.Length; i++)
            if (s_default15s[i].OffsetMs + PlutoAudioSynthesizer.DurationMs(s_default15s[i].SfxType) <= limitMs) list[k++] = s_default15s[i];
        return list;
    }

    public static string Label(PlutoSfxType t) => t switch
    {
        PlutoSfxType.ShutterClick => "📸 셔터",
        PlutoSfxType.ChimeSparkle => "✨ 차임",
        _ => "🌙 잠자리 벨",
    };
}
