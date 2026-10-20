using System;
using System.Globalization;

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] B2B 3D 엠보스 로고 워터마크 파이프라인 — 순수 FFmpeg 필터 문자열(외부 NuGet 0).
///   ▸ <see cref="VideoMergeService"/> 최종 병합의 워터마크 오버레이 자리에 <b>인라인 결선</b>되어, 로고를 <c>convolution</c> 양각 커널로 입체화한 뒤
///     우측 상단 세이프존(마진 24px)에 3D 엠보싱 워터마크로 합성한다. B2B 프로젝트(<see cref="StoryboardProject.EmbossWatermark"/>)에서만 사용.
///   ▸ 내부 상수 커널만 사용(외부 입력 미개입) · 알파(rgba) 유지 · 살짝 블러로 베벨 음영을 부드럽게.
/// </summary>
public static class EmbossWatermarkPipeline
{
    /// <summary>3×3 엠보스 컨볼루션 커널(양각 · 좌상→우하 음영).</summary>
    public const string RaisedKernel = "-2 -1 0 -1 1 1 0 1 2";

    /// <summary>알파 채널은 원본 유지용 항등 커널(엠보스가 투명도를 깨지 않도록).</summary>
    private const string AlphaIdentity = "0 0 0 0 1 0 0 0 0";

    /// <summary>병합 filter_complex 용 엠보스 오버레이 조각(끝에 ';' 없음). 로고 폭 = 출력 폭의 ~1/7.</summary>
    public static string BuildOverlay(int logoInputIdx, int outWidth, string baseLabel, string outLabel)
    {
        var logoW = Math.Max(48, outWidth / 7).ToString(CultureInfo.InvariantCulture);
        return
            $"[{logoInputIdx}:v]scale={logoW}:-1,format=rgba,"
            + $"convolution='{RaisedKernel}':'{RaisedKernel}':'{RaisedKernel}':'{AlphaIdentity}',"
            + $"gblur=sigma=0.4[embwm];"
            + $"[{baseLabel}][embwm]overlay=W-w-24:24:format=auto[{outLabel}]";
    }
}
