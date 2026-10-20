using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;       // IMobileOfflineStore · SyncItemKind · RemoteRenderClient(헬퍼)
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject
using PromptFlowStudio.Services.Ui;           // UserPreferenceService (품질 가드레일 설정)

namespace PromptFlowStudio.Services.Wan;

/// <summary>Wan 3.0 렌더 요청 본문(JSON 페이로드) — 프롬프트 + 모션 가중치 + 포맷 메타 + (v5.5.3) i2v 소스 이미지.</summary>
public sealed record WanRenderRequest(
    string JobId, string Prompt, string NegativePrompt, int MotionIntensity,
    int Width, int Height, int Fps, int SceneCount, string ProjectTitle, int SchemaVersion,
    string? ImageUrl = null,    // 지정 시 Image-to-Video 모드(로컬/커스텀 Wan 백엔드가 디코드)
    // v15.1.0: 거버너 지정 생성 스텝(0 = 미지정 → 직렬화 생략, 기존 페이로드와 바이트 동일).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int InferenceSteps = 0,
    // v16.7.0: 테마 시네마틱 메타(카메라 무빙 · 조명 · 권장 SFX). 미적용 프로젝트는 null → 직렬화 생략(기존 페이로드와 바이트 동일).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CameraMotion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Lighting = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Sfx = null);

/// <summary>Wan 렌더 전송 결과 — 성공/큐적재/잡ID/모션강도/메시지 + (v5.5.1) 재생 URL/상태 + (v5.5.4) 마지막 프레임 URL(체이닝).</summary>
public sealed record WanDispatchResult(bool Ok, bool Queued, string JobId, int MotionIntensity, string Message, string? VideoUrl = null, string Status = "", string? LastFrameUrl = null);

/// <summary>[v5.5.1] Wan 비디오 출력 참조 — 프리뷰 노드 인라인 재생(video_url + job_id + 상태) + (v5.5.4) 마지막 프레임 URL(자동 체인).</summary>
public sealed record WanVideoRef(string? VideoUrl, string JobId, string Status, string? LastFrameUrl = null)
{
    /// <summary>재생 가능한 URL 이 있는지.</summary>
    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoUrl);
    /// <summary>아직 생성 중(URL 없음)인 대기 참조.</summary>
    public static WanVideoRef Pending(string jobId, string status = "running") => new(null, jobId, status);
}

/// <summary>
/// [PromptFlow Studio v5.4.1 · Task2] Wan 3.0 렌더 클라이언트 직결 — 순수 BCL(System.Text.Json · 외부 SDK 0).
///   ▸ <see cref="WanPromptCompiler"/> 가 만든 프롬프트 + 모션 가중치를 JSON 페이로드로 백엔드 REST 로 POST.
///   ▸ 전송 실패(단선/5xx/타임아웃)는 <b>지수 백오프 재시도</b> 후 v5.2 <see cref="IMobileOfflineStore"/> 큐에 적재 → 데이터 유실 0.
///     (오프라인 큐는 네트워크 복구 시 <see cref="OfflineSyncPipeline"/> 가 <c>api/wan/render</c> 로 자동 재전송.)
///   ▸ Null 방어(빌드/스토어/URL) · CancellationToken 이식 · AES 전송규약은 오프라인 경로가 재사용.
/// </summary>
public sealed class WanRenderClient
{
    /// <summary>페이로드 스키마 버전(백엔드 호환 협상).</summary>
    public const int SchemaVersion = 1;
    private const int MaxAttempts = 3;
    private const string DefaultNegative = "blurry, low quality, watermark, distorted, deformed, extra limbs";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IMobileOfflineStore? _store;
    private readonly WanLocalStubEngine? _stub;
    private readonly UserPreferenceService? _prefs;   // v5.6.4 품질 가드레일 설정(null 이면 기본값=On/Balanced)
    private string? _backendUrl;

    /// <summary>v13.0.0: 전송 직전 사용자 프롬프트 변조 훅(예: VisionReactionBinder.ApplyPromptModulation). null 이면 무변조.
    ///   품질 가드레일(AppendCore) 단일 주입점보다 먼저 적용 - 가드레일 동작은 무변경.</summary>
    public Func<string, string>? PromptModulator { get; set; }

    /// <summary>v15.1.0 WAN-OPT-1: 품질·속도 거버너(null = 무조율). 지정 시 요청마다 스텝·해상도를 현재 레벨로 조율하고,
    ///   성공 왕복 지연·요청 타임아웃을 추론 지연 샘플로 보고한다.</summary>
    public PromptFlowStudio.Services.AI.Wan.WanQualitySpeedGovernor? Governor { get; set; }

    /// <summary>거버너 현재 레벨의 스텝·해상도(16 배수)를 요청에 반영.</summary>
    internal static WanRenderRequest ApplyGovernor(WanRenderRequest req, PromptFlowStudio.Services.AI.Wan.WanQualitySpeedGovernor governor)
    {
        var (w, h) = governor.ScaleResolution(req.Width, req.Height);
        return req with { Width = w, Height = h, InferenceSteps = governor.CurrentSteps };
    }

    public WanRenderClient(IMobileOfflineStore? store, WanLocalStubEngine? stub = null, UserPreferenceService? prefs = null)
    {
        _store = store;   // null 가능(Graceful) — Enqueue 에서 방어
        _stub = stub;     // v5.6: 백엔드 미구성 시 로컬 스텁 모의 렌더 폴백(개발/오프라인)
        _prefs = prefs;   // v5.6.4: 사용자 품질 가드레일(On/Off·프리셋·커스텀) 실시간 참조
    }

    // v5.6.4: 렌더 요청마다 최신 설정을 읽어 실시간 반영(null=기본값).
    private bool GuardEnabled => _prefs?.EnableGlobalQualityGuardrail ?? true;
    private QualityGuardrailPreset GuardPreset => _prefs?.QualityGuardrailPreset ?? QualityGuardrailPreset.Balanced;
    private string? GuardCustom => _prefs?.CustomQualityPrompt;

    /// <summary>백엔드 기본 주소 구성(미구성 시 project.RemoteWorkerUrl 사용, 그것도 없으면 큐 보관).</summary>
    public void Configure(string? backendUrl)
        => _backendUrl = string.IsNullOrWhiteSpace(backendUrl) ? null : backendUrl!.Trim();

    /// <summary>해상도 문자열("WxH") → (폭,높이). 파싱 실패는 세로 숏츠 기본(1080×1920).</summary>
    public static (int W, int H) ParseResolution(string? res)
    {
        if (!string.IsNullOrWhiteSpace(res))
        {
            var parts = res!.Split('x', 'X', '×');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out var w) && w > 0
                && int.TryParse(parts[1].Trim(), out var h) && h > 0)
                return (w, h);
        }
        return (1080, 1920);
    }

    /// <summary>컴파일된 빌드 + 프로젝트(+ 선택 i2v 이미지) → JSON 요청 본문 구성.</summary>
    public static WanRenderRequest BuildRequest(WanPromptBuild build, StoryboardProject? project, string? imageRef = null)
    {
        var (w, h) = ParseResolution(project?.ExportResolution);
        var isI2v = !string.IsNullOrWhiteSpace(imageRef);   // v16.5.0: i2v 만 역방향 방지 네거티브 + 폐쇄 공간 Dolly Out(t2v 원형 유지)
        return new WanRenderRequest(
            JobId: Guid.NewGuid().ToString("N")[..12],
            Prompt: isI2v ? MotionVectorPromptEngine.ApplyCameraCorrection(build?.Prompt) : build?.Prompt ?? "",   // v5.6.4: 코어 주입은 SubmitAsync 에서 사용자 설정 반영해 단일 수행
            NegativePrompt: isI2v ? MotionVectorPromptEngine.AppendReverseGuard(DefaultNegative) : DefaultNegative,
            // v16.7.0: 빌드에 모션 가중치가 없으면 테마 최적 강도(2~4 → 40~80) 사용.
            MotionIntensity: Math.Clamp(build?.MotionIntensity is > 0 ? build.MotionIntensity : (project?.CinematicMotionIntensity ?? 0) * 20, 0, 100),
            Width: w, Height: h, Fps: 60,
            SceneCount: project?.Scenes?.Count ?? 0,
            ProjectTitle: string.IsNullOrWhiteSpace(project?.Title) ? "PromptFlow" : project!.Title,
            SchemaVersion: SchemaVersion,
            ImageUrl: string.IsNullOrWhiteSpace(imageRef) ? null : imageRef,
            CameraMotion: project?.CinematicCamera,
            Lighting: project?.CinematicLighting,
            Sfx: project?.CinematicSfx is { Count: > 0 } sfx ? sfx : null);
    }

    /// <summary>
    /// 컴파일 빌드를 Wan 3.0 백엔드로 전송한다. 온라인 성공 시 Ok, 실패 시 오프라인 큐 적재(Queued=true · 무손실).
    /// </summary>
    public async Task<WanDispatchResult> SubmitAsync(WanPromptBuild build, StoryboardProject? project, CancellationToken ct = default, string? imageRef = null)
    {
        if (build is null || (string.IsNullOrWhiteSpace(build.Prompt) && string.IsNullOrWhiteSpace(imageRef)))
            return new(false, false, "", 0, "전송할 프롬프트/프레임이 비어 있습니다 — 먼저 모션·음성 또는 프레임을 구성하세요.");

        // v5.6.4: 사용자 품질 가드레일(On/Off·프리셋·커스텀)을 이 시점에 단일 주입(Off=원본 100% 통과). 설정 변경 즉시 반영.
        // v13.0.0: 비전 컨텍스트 프롬프트 변조(가드레일 주입 직전 · null=원본 통과 · 변조기 예외 시 원본으로 폴백).
        var basePrompt = build.Prompt;
        if (PromptModulator is { } modulate) { try { basePrompt = modulate(build.Prompt ?? string.Empty); } catch (Exception ex) { Program.Log("WanRender.PromptModulator", ex); } }
        var guarded = build with { Prompt = WanPromptCompiler.AppendCore(basePrompt, GuardEnabled, GuardPreset, GuardCustom) };
        var req = BuildRequest(guarded, project, imageRef);
        var governor = Governor;
        if (governor is not null) req = ApplyGovernor(req, governor);
        var json = JsonSerializer.Serialize(req, PromptFlowWireJsonContext.Default.WanRenderRequest);

        var baseUrl = FirstValidUrl(_backendUrl, project?.RemoteWorkerUrl);
        if (baseUrl is null)
        {
            if (_stub is not null) return await _stub.SubmitAsync(req, null, ct).ConfigureAwait(false);   // v5.6: 백엔드 미구성 → 로컬 스텁 모의 렌더
            Enqueue(json);
            return new(false, true, req.JobId, req.MotionIntensity, "백엔드 주소 미구성 — 오프라인 큐에 적재(복구 시 자동 전송).");
        }

        var url = RemoteRenderClient.BuildEndpoint(baseUrl, "api/wan/render");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) { Enqueue(json); return new(false, true, req.JobId, req.MotionIntensity, "취소 — 큐 보관."); }
            var attemptStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(25));   // 요청 1건 상한(무응답 방어)
                using var resp = await _http.PostAsync(url, content, linked.Token).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    governor?.RecordLatency(System.Diagnostics.Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds);
                    // 로컬/커스텀 Wan 백엔드 응답 파싱 — 동기 서버는 videoUrl 즉시 반환, 비동기 서버는 jobId/status(폴링).
                    var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    var parsed = TryParse(body);
                    var jobId = string.IsNullOrWhiteSpace(parsed?.JobId) ? req.JobId : parsed!.JobId!;
                    var status = string.IsNullOrWhiteSpace(parsed?.Status) ? (string.IsNullOrEmpty(parsed?.VideoUrl) ? "running" : "completed") : parsed!.Status!;
                    return new(true, false, jobId, req.MotionIntensity,
                        $"✅ Wan 3.0 렌더 요청 · {req.Width}×{req.Height}@{req.Fps}fps · 모션 {req.MotionIntensity}{(req.ImageUrl is not null ? " · i2v" : "")}", parsed?.VideoUrl, status, parsed?.LastFrameUrl);
                }

                var code = (int)resp.StatusCode;
                if (code is >= 400 and < 500)   // 클라이언트 오류 → 재시도 무의미, 큐 보관(무손실)
                {
                    Enqueue(json);
                    return new(false, true, req.JobId, req.MotionIntensity, $"요청 거부({code}) — 오프라인 큐에 보관(백엔드 점검 후 재전송).");
                }
                // 5xx → 백오프 재시도
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                governor?.RecordLatency(System.Diagnostics.Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds);   // 요청 타임아웃 → 재시도(GPU 스톨 신호로 거버너 보고)
            }
            catch (OperationCanceledException) { Enqueue(json); return new(false, true, req.JobId, req.MotionIntensity, "취소 — 큐 보관."); }
            catch (Exception ex) { Program.Log("WanRender.Submit", ex); /* 단선/HttpRequestException → 재시도 */ }

            if (attempt < MaxAttempts)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        Enqueue(json);   // 재시도 소진 → 무손실 큐 적재
        return new(false, true, req.JobId, req.MotionIntensity, "전송 실패 — 오프라인 큐에 적재(네트워크 복구 시 자동 재시도).");
    }

    /// <summary>[v5.5.1] 비동기 Wan 잡 상태 폴링 — <c>api/wan/status/{jobId}</c> GET → 완료 시 videoUrl. 로컬/커스텀 엔드포인트 수용.</summary>
    public async Task<WanDispatchResult> PollStatusAsync(string jobId, StoryboardProject? project, CancellationToken ct = default)
    {
        var baseUrl = FirstValidUrl(_backendUrl, project?.RemoteWorkerUrl);
        if (baseUrl is null || string.IsNullOrWhiteSpace(jobId))
            return new(false, false, jobId ?? "", 0, "상태 조회 불가(백엔드 미구성)", null, "unknown");
        try
        {
            var url = RemoteRenderClient.BuildEndpoint(baseUrl, "api/wan/status/" + Uri.EscapeDataString(jobId));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            var body = await _http.GetStringAsync(url, linked.Token).ConfigureAwait(false);
            var p = TryParse(body);
            var status = string.IsNullOrWhiteSpace(p?.Status) ? "running" : p!.Status!;
            return new(true, false, jobId, 0, "상태: " + status, p?.VideoUrl, status, p?.LastFrameUrl);
        }
        catch (Exception ex) { Program.Log("WanRender.Poll", ex); return new(false, false, jobId, 0, "상태 조회 오류", null, "error"); }
    }

    /// <summary>[v5.5.5] N개 세그먼트 비디오 URL → 백엔드 concat REST(<c>api/wan/concat</c>)로 단일 MP4 병합(비동기면 폴링).</summary>
    public async Task<WanDispatchResult> ConcatAsync(IReadOnlyList<string> videoUrls, StoryboardProject? project, CancellationToken ct = default)
    {
        var urls = (videoUrls ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0) return new(false, false, "", 0, "병합할 세그먼트가 없습니다.");
        if (urls.Count == 1) return new(true, false, "", 0, "세그먼트 1개 — 병합 불필요", urls[0], "completed");

        var baseUrl = FirstValidUrl(_backendUrl, project?.RemoteWorkerUrl);
        if (baseUrl is null)
            return _stub is not null ? await _stub.ConcatAsync(urls, ct).ConfigureAwait(false)
                                     : new(false, false, "", 0, "백엔드 미구성 — 병합 불가(로컬 Wan 서버 필요).");

        var jobId = Guid.NewGuid().ToString("N")[..12];
        var payload = JsonSerializer.Serialize(new ConcatRequest(jobId, urls, SchemaVersion), PromptFlowWireJsonContext.Default.WanConcatRequest);
        try
        {
            var url = RemoteRenderClient.BuildEndpoint(baseUrl, "api/wan/concat");
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMinutes(3));   // 병합은 길 수 있음
            using var resp = await _http.PostAsync(url, content, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new(false, false, jobId, 0, $"병합 요청 실패({(int)resp.StatusCode})");

            var parsed = TryParse(await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false));
            var mergedUrl = parsed?.VideoUrl;
            var status = string.IsNullOrWhiteSpace(parsed?.Status) ? (mergedUrl is null ? "running" : "completed") : parsed!.Status!;
            if (mergedUrl is null)   // 비동기 병합 → 완료까지 폴링(상한 3분)
            {
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(2500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    var poll = await PollStatusAsync(jobId, project, ct).ConfigureAwait(false);
                    status = poll.Status;
                    if (!string.IsNullOrEmpty(poll.VideoUrl)) { mergedUrl = poll.VideoUrl; break; }
                    if (status is "failed" or "error") break;
                }
            }
            return string.IsNullOrEmpty(mergedUrl)
                ? new(false, false, jobId, 0, "병합 진행 중/실패", null, status)
                : new(true, false, jobId, 0, $"✅ {urls.Count}개 세그먼트 병합 완료", mergedUrl, "completed");
        }
        catch (Exception ex) { Program.Log("WanRender.Concat", ex); return new(false, false, jobId, 0, "병합 오류: " + ex.Message); }
    }

    /// <summary>concat 요청 본문(세그먼트 URL 목록).</summary>
    internal sealed record ConcatRequest(string JobId, IReadOnlyList<string> VideoUrls, int SchemaVersion);

    private static WanRenderResponse? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize(body!, PromptFlowWireJsonContext.Default.WanRenderResponse); }
        catch { return null; }
    }

    /// <summary>로컬/커스텀 Wan 백엔드 응답 본문(jobId/status/videoUrl + v5.5.4 lastFrameUrl).</summary>
    internal sealed record WanRenderResponse(string? JobId, string? Status, string? VideoUrl, string? LastFrameUrl);

    private void Enqueue(string json)
    {
        try { _store?.Enqueue(SyncItemKind.WanRender, json); }
        catch (Exception ex) { Program.Log("WanRender.Enqueue", ex); }
    }

    private static string? FirstValidUrl(params string?[] urls)
    {
        foreach (var u in urls)
            if (RemoteRenderClient.IsValidWorkerUrl(u)) return u!.Trim();
        return null;
    }
}
