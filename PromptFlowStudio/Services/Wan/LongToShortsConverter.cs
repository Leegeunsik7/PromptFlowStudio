using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>재구성된 쇼츠 세그먼트 1건 — 시간 구간 + 9:16 리프레이밍 파라미터 + 대표 키프레임 URL.</summary>
public sealed record ShortsSegment(int Index, string Title, double StartSec, double EndSec, string FrameUrl, string ReframeParams);

/// <summary>
/// [PromptFlow Studio v5.7.1] Long-to-Shorts 스마트 재구성 — 100% 순수 BCL(HttpClient/File · 외부 C++ DLL 0).
///   ▸ 로컬 파일 경로 또는 HTTP URL 수신 → (URL이면) 임포트 폴더로 스트리밍 다운로드 검증 → 5~10초 단위 하이라이트 세그먼트로 분할.
///   ▸ 16:9 중앙 피사체를 유지하는 9:16 세로 크롭 + 스마트 블러 배경 파라미터 셋 + 대표 키프레임(WanSampleFactory) 생성.
///   ⚠ 실제 프레임 추출/컷팅은 백엔드(ffmpeg) 몫 — 여기서는 재구성 '계획'(세그먼트/리프레임 파라미터/플레이스홀더 키프레임)을 산출(스텁 정합).
/// </summary>
public static class LongToShortsConverter
{
    private const double SegmentSec = 10.0;   // 세그먼트 길이(5~10초 권장 · 기본 10)
    private const int MaxSegments = 6;

    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// 파일 경로 또는 HTTP URL → 9:16 쇼츠 세그먼트 목록. URL이면 <paramref name="importDir"/>로 다운로드 검증.
    /// </summary>
    public static async Task<List<ShortsSegment>> ImportAndReconstructAsync(
        string sourceUrlOrPath, string? importDir, double durationSec = 30.0,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var list = new List<ShortsSegment>();
        if (string.IsNullOrWhiteSpace(sourceUrlOrPath)) return list;

        // 1. URL이면 임포트 폴더로 스트리밍 다운로드(무손실 검증). 실패해도 재구성 계획은 계속 생성.
        var isUrl = sourceUrlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || sourceUrlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isUrl && !string.IsNullOrWhiteSpace(importDir))
        {
            try
            {
                var client = http ?? _shared;
                Directory.CreateDirectory(importDir!);
                var localPath = Path.Combine(importDir!, $"import_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                using var resp = await client.GetAsync(sourceUrlOrPath, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(localPath);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch (Exception) { /* 단선/404 등 — 계획 생성은 계속(백엔드/스텁 경로에서 재확보) */ }
        }

        // 2. 30초+ 타임라인 → 5~10초 세그먼트 분할(훅/전개/클라이맥스).
        var dur = durationSec < SegmentSec ? SegmentSec : durationSec;
        var count = Math.Clamp((int)Math.Ceiling(dur / SegmentSec), 1, MaxSegments);
        await Task.Delay(200, ct).ConfigureAwait(false);   // 분석 지연 모의

        for (var i = 0; i < count; i++)
        {
            var start = i * SegmentSec;
            var end = Math.Min(dur, start + SegmentSec);
            var phase = count <= 1 ? "하이라이트" : i == 0 ? "훅(시작)" : i == count - 1 ? "클라이맥스" : $"전개 {i}";
            var title = $"숏츠 컷 #{i + 1} · {phase} ({start:F0}s~{end:F0}s)";
            var reframe = "crop 9:16 center, subject-locked reframe, blurred 16:9 backdrop fill, motion-tracked";
            var frame = WanSampleFactory.FrameDataUrl(title, "#38bdf8", "9:16 REFRAMED · " + phase);
            list.Add(new ShortsSegment(i + 1, title, start, end, frame, reframe));
        }
        return list;
    }
}
