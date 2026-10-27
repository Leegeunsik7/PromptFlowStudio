using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Platform;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 알리바바 통의만상(Wan 2.1) 영상생성 API(DashScope) 서버측 연동 — 텍스트→영상(t2v).
///   BCL <see cref="HttpClient"/> + <see cref="System.Text.Json"/> 만 사용(외부 NuGet 0 · JusoAddressService 와 동일 방침).
///   비동기 2단계 REST: ① 태스크 제출(<c>X-DashScope-Async: enable</c>) → task_id  ② tasks/{id} 폴링 → SUCCEEDED 시 video_url.
///   ⚠ 모델·엔드포인트(BaseUrl)·API키는 반드시 같은 지역이어야 한다(교차지역 호출 실패). 기본 BaseUrl 은 국제(싱가포르) 엔드포인트.
///   ⚠ video_url 은 발급 후 24시간 뒤 만료 — 영구 보관하려면 즉시 다운로드해야 한다(추후 렌더/보관 단계에서 처리).
///   ▸ v15.15.0 Zero-Copy 응답 파이프라인: 본문을 <see cref="PooledUtf8Body"/>(ArrayPool)로 직접 수신 → <see cref="WanWire"/>
///     (<see cref="Utf8JsonReader"/> 단일 패스) 로 필요한 필드만 추출 — 응답 string·DOM 할당 0, 원문 텍스트는 오류 메시지에서만 지연 생성.
///     폴링은 <see cref="PollAsync"/>(<see cref="IAsyncEnumerable{T}"/>) 스트림으로 소비.
/// </summary>
public sealed class WanApiService(IWanConfigService cfg, QuotaRateGuardService guard, WanTokenVault vault,
    HttpMessageHandler? handler = null, IVideoGenerationEngine? localFallback = null,
    PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService? cost = null)
    : IWanApiService, IVideoGenerationEngine
{
    // ── 엔진 추상화(IVideoGenerationEngine) ── GenerateAsync/UploadImageAsync/Enabled 는 기존 멤버를 그대로 사용.
    public string Key => "Wan";
    public string DisplayName => "Alibaba Wan 2.1";

    // 개별 호출(제출/폴링/검증)은 짧게 끝난다. 전체 생성 대기는 폴링 루프(MaxWaitSeconds)가 관리하므로
    // HttpClient 타임아웃은 "요청 1건" 기준으로만 짧게 둔다(전체 대기시간으로 오해하지 말 것).
    //   v15.13.0: handler 주입 시(테스트 모의) 해당 핸들러 사용 — DI 는 기본값(null) 경로.
    private readonly HttpClient _http = handler is null
        ? new() { Timeout = TimeSpan.FromSeconds(30) }
        : new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    // v16.3.1: OSS 대용량 업로드 전용 — 시간 상한은 요청별 CTS(크기 비례)가 통제(30s 일괄 타임아웃으로 5MB+ 가 끊기지 않게).
    private readonly HttpClient _uploadHttp = handler is null
        ? new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan }
        : new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>i2v 기본 모델 — 요청에 ImagePath 가 있으면(모델 미지정 시) 이 모델로 제출.</summary>
    private const string DefaultI2vModel = "wan2.1-i2v-turbo";

    /// <summary>v15.12.0: 402/429 시 Vault 키 로테이션 즉시 재시도 최대 횟수.</summary>
    public const int MaxKeyRotations = 3;

    /// <summary>v15.13.0 교차 페일오버 순서 — 앞 프로바이더 키 전멸(402/429·활성 키 없음) 시 다음으로 우회.
    ///   Custom 은 DashScope 호환 프로토콜 — v15.14.0: Vault 의 Custom BaseUrl(미설정 시 기본 BaseUrl).</summary>
    public static readonly string[] FailoverChain = { WanTokenVault.DashScope, WanTokenVault.SiliconFlow, WanTokenVault.Custom };

    /// <summary>SiliconFlow Wan 2.1 엔드포인트(OpenAI 호환 · /v1/video/submit · /v1/video/status).</summary>
    public const string SiliconFlowBaseUrl = "https://api.siliconflow.cn";

    /// <summary>v15.14.0: SiliconFlow 인라인(base64) 이미지 상한 — 초과 시 Graceful 거부(LOH·서버 거절 방지).</summary>
    public const long MaxInlineImageBytes = 10L * 1024 * 1024;

    // v15.12.0: 태스크는 계정 바운드 → 제출에 쓴 키로 조회해야 한다. v15.13.0: 프로바이더까지 고정(task_id → route).
    private readonly record struct TaskRoute(string Provider, string Key);
    private readonly ConcurrentDictionary<string, TaskRoute> _taskRoutes = new(StringComparer.Ordinal);

    // ── 구성값은 IWanConfigService(라이브)에서 읽는다 → 관리자 저장 즉시 재시작 없이 반영 ──
    //   v15.12.0: API 키는 WanTokenVault(DashScope 라운드로빈) 우선 → 비어 있으면 IWanConfigService 단일 키 폴백.
    private string ApiKey       => vault.TryGetNextKey(WanTokenVault.DashScope, out var k) ? k : cfg.ApiKey;
    private string BaseUrl      => cfg.BaseUrl;   // 이미 정규화(끝 '/' 제거)
    private string DefaultModel => cfg.Model;
    private string DefaultSize  => cfg.Size;
    private int PollIntervalSec => cfg.PollIntervalSeconds;
    private int MaxWaitSec      => cfg.MaxWaitSeconds;

    /// <inheritdoc/>
    public bool Enabled => cfg.IsConfigured
        || vault.GetKeyCount(WanTokenVault.DashScope) > 0
        || vault.GetKeyCount(WanTokenVault.SiliconFlow) > 0
        || vault.GetKeyCount(WanTokenVault.Custom) > 0;   // 키 저장 즉시 true 로 전환(라이브)

    // v15.14.0: 프로바이더별 DashScope 호환 엔드포인트(Custom 은 Vault BaseUrl 우선).
    private string BaseFor(string provider)
        => provider == WanTokenVault.Custom && vault.GetProviderEndpoint(WanTokenVault.Custom) is { } custom ? custom : BaseUrl;

    /// <summary>v15.14.0: 체인 전체에 획득 가능한 클라우드 키가 하나라도 있는지(전멸 판정 · 0B).</summary>
    public bool HasActiveCloudKey()
    {
        for (int p = 0; p < FailoverChain.Length; p++)
            if (vault.GetActiveKeyCount(FailoverChain[p]) > 0) return true;
        return cfg.IsConfigured && !vault.IsExhausted(WanTokenVault.DashScope, cfg.ApiKey);
    }

    // 클라우드 전멸 + 로컬 엔진 사용 가능(자기 자신 제외 · 재귀 방지).
    private bool CanFallBackLocal
        => localFallback is { Enabled: true } l && !ReferenceEquals(l, this) && !HasActiveCloudKey();

    // 프로바이더 활성 키 획득 — DashScope 는 Vault 전멸 시 IWanConfigService 단일 키 폴백(소진 표시된 동일 키는 제외).
    private bool TryAcquireKey(string provider, out string key)
    {
        if (vault.TryGetNextKey(provider, out key)) return true;
        if (provider == WanTokenVault.DashScope && cfg.IsConfigured && !vault.IsExhausted(provider, cfg.ApiKey))
        {
            key = cfg.ApiKey;
            return true;
        }
        key = "";
        return false;
    }

    // ══ [1단계] 태스크 제출 (t2v · i2v 자동 분기 · v15.13.0 교차 프로바이더 페일오버) ══════
    /// <inheritdoc/>
    public async Task<WanSubmitResult> SubmitAsync(WanVideoRequest request, CancellationToken ct = default)
    {
        if (!Enabled)
            return new(false, null, WanTaskStatus.Unknown, "Wan API 키가 구성되지 않았습니다 — 관리자 설정 › 영상(Wan)/키 볼트에서 등록하세요.");
        if (cost?.CheckVideoAllowed() is { } budgetBlock)   // v16.18.0 예산 한도 도달 → 신규 제출 거부(과금 0)
            return new(false, null, WanTaskStatus.Unknown, budgetBlock);

        request = MotionVectorPromptEngine.Process(request);   // v16.5.0: I2V 역방향 방지 네거티브 + 폐쇄 공간 Dolly Out(t2v 무변경)
        request = ContextAwarePromptGuard.Process(request);    // v16.9.0: 컨텍스트 가드레일(해부학/물리) + 시간적 일관성 + 미세 모션(I2V/T2V)
        var dualKeyframe = MotionVectorPromptEngine.IsDualKeyframe(request);
        var isI2v = !string.IsNullOrWhiteSpace(request.ImagePath) || !string.IsNullOrWhiteSpace(request.ImageUrl);   // 이미지(경로/사전업로드URL) 있으면 이미지→영상
        var prompt = (request.Prompt ?? "").Trim();
        if (!isI2v && prompt.Length == 0)
            return new(false, null, WanTaskStatus.Unknown, "프롬프트가 비어 있습니다.");   // t2v 는 프롬프트 필수(i2v 는 선택)
        if (request.Tuning is WanTuning tuning) prompt = DashScopeTuningAdapter.Apply(prompt, tuning);   // v15.4.1: 튜너 → 품질 토큰(근사)
        if (prompt.Length > 800) prompt = prompt[..800];             // 서버 제한(800자)

        // v15.13.0: DashScope → SiliconFlow → Custom. 성공·비쿼터 오류는 즉시 반환(기존 동작), 쿼터(402/429) 전멸만 다음 프로바이더로 우회.
        WanSubmitResult? last = null;
        for (int p = 0; p < FailoverChain.Length; p++)
        {
            var provider = FailoverChain[p];
            if (dualKeyframe && provider == WanTokenVault.SiliconFlow) continue;   // v16.5.0: SiliconFlow 는 kf2v 미지원 → 건너뜀
            if (!TryAcquireKey(provider, out var key)) continue;
            var (result, quotaHit) = provider == WanTokenVault.SiliconFlow
                ? await SubmitSiliconFlowAsync(request, prompt, isI2v, key, ct).ConfigureAwait(false)
                : await SubmitDashScopeAsync(request, prompt, isI2v, provider, key, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                // v16.18.0 과금 기록(제출 성공 = 과금 확정 근사 · 생산 스코프에 귀속).
                cost?.RecordWanSubmit(provider, string.IsNullOrWhiteSpace(request.Model) ? (isI2v ? DefaultI2vModel : DefaultModel) : request.Model);
                return result;
            }
            if (!quotaHit) return result;
            last = result;
        }
        return last ?? new(false, null, WanTaskStatus.Unknown, "사용 가능한 활성 API 키가 없습니다(전 프로바이더 소진) — 키 볼트를 확인하세요.");
    }

    // DashScope 프로토콜 제출(DashScope · Custom). i2v 는 키 로테이션마다 새 계정으로 이미지 재업로드(oss:// 는 계정 바운드).
    private async Task<(WanSubmitResult Result, bool QuotaHit)> SubmitDashScopeAsync(
        WanVideoRequest request, string prompt, bool isI2v, string provider, string initialKey, CancellationToken ct)
    {
        // v15.16.0: 고정 스키마 DTO + WanJsonContext(소스 생성) — 리플렉션 0 · null 필드는 생략되어 조건부 포함 의미 유지.
        var input = new DashScopeInput();
        var parameters = new DashScopeParameters();
        string model;

        var dualKeyframe = isI2v && MotionVectorPromptEngine.IsDualKeyframe(request);
        if (isI2v)
        {
            model = string.IsNullOrWhiteSpace(request.Model)
                ? (dualKeyframe ? MotionVectorPromptEngine.KeyframeModel : DefaultI2vModel) : request.Model!;
            if (prompt.Length > 0) input.Prompt = prompt;        // i2v 프롬프트는 선택
            parameters.Resolution = (request.Size?.Contains("480") ?? false) ? "480P" : "720P";
            parameters.PromptExtend = request.PromptExtend;
            parameters.Seed = WanVideoRequest.ToCloudSeed(request.Seed);
        }
        else
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel : request.Model!;
            input.Prompt = prompt;
            parameters.Size = string.IsNullOrWhiteSpace(request.Size) ? DefaultSize : request.Size!;
            parameters.PromptExtend = request.PromptExtend;
            parameters.Watermark = request.Watermark;
            parameters.Seed = WanVideoRequest.ToCloudSeed(request.Seed);
        }

        var negative = (request.NegativePrompt ?? "").Trim();
        if (negative.Length > 0) input.NegativePrompt = negative.Length > 500 ? negative[..500] : negative;

        var payload = new DashScopeSubmitBody { Model = model, Input = input, Parameters = parameters };
        var baseUrl = BaseFor(provider);
        var url = $"{baseUrl}/api/v1/services/aigc/video-generation/video-synthesis";

        // 사전 업로드 URL(앵커 OSS 캐시)은 DashScope 첫 키에서만 재사용 — 그 외(로테이션·Custom 계정)는 로컬 원본을 재업로드.
        var preUrl = provider == WanTokenVault.DashScope ? request.ImageUrl?.Trim() : null;
        var preLastUrl = provider == WanTokenVault.DashScope ? request.LastFrameUrl?.Trim() : null;
        var uploadModel = dualKeyframe ? model : request.Model;
        string? uploadErr = null;

        try
        {
            var (sent, usedKey) = await SendWithKeyRotationAsync(_http, vault, provider, initialKey, MaxKeyRotations, async (key, c) =>
            {
                if (isI2v)
                {
                    string imgUrl;
                    if (preUrl is { Length: > 0 } && string.Equals(key, initialKey, StringComparison.Ordinal))
                    {
                        imgUrl = preUrl;
                    }
                    else if (!string.IsNullOrWhiteSpace(request.ImagePath))
                    {
                        // getPolicy 도 402/429 로테이션 — 업로드 계정 키(upKey)로 제출해야 oss:// 가 해석된다.
                        var (up, upKey) = await UploadImageCoreAsync(request.ImagePath!, uploadModel, provider, key, c).ConfigureAwait(false);
                        key = upKey;
                        if (!up.Ok || up.OssUrl is null) { uploadErr = up.Message; return (null, key); }
                        imgUrl = up.OssUrl;
                    }
                    else
                    {
                        uploadErr = "사전 업로드 URL 은 다른 계정 키로 재사용할 수 없습니다(로컬 원본 이미지 없음).";
                        return (null, key);
                    }
                    if (dualKeyframe)
                    {
                        // v16.5.0: 끝 프레임도 같은 계정 키로 확보(첫 프레임 업로드로 로테이션됐으면 그 키 그대로).
                        string lastUrl;
                        if (preLastUrl is { Length: > 0 } && string.Equals(key, initialKey, StringComparison.Ordinal))
                            lastUrl = preLastUrl;
                        else if (!string.IsNullOrWhiteSpace(request.LastFramePath))
                        {
                            var (upLast, lastKey) = await UploadImageCoreAsync(request.LastFramePath!, uploadModel, provider, key, c).ConfigureAwait(false);
                            if (!upLast.Ok || upLast.OssUrl is null) { uploadErr = upLast.Message; return (null, key); }
                            if (!string.Equals(lastKey, key, StringComparison.Ordinal))
                            {
                                uploadErr = "끝 프레임 업로드 중 키가 교체되어 첫 프레임과 계정이 달라졌습니다 — 다시 시도하세요.";
                                return (null, lastKey);
                            }
                            lastUrl = upLast.OssUrl;
                        }
                        else
                        {
                            uploadErr = "끝 프레임 사전 업로드 URL 은 다른 계정 키로 재사용할 수 없습니다(로컬 원본 이미지 없음).";
                            return (null, key);
                        }
                        input.FirstFrameUrl = imgUrl;
                        input.LastFrameUrl = lastUrl;
                    }
                    else input.ImgUrl = imgUrl;
                }

                var r = new HttpRequestMessage(HttpMethod.Post, url);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                r.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");   // ⚠ 비동기 호출 필수 헤더
                if (isI2v)
                    r.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");   // ⚠ oss:// 해석 필수
                r.Content = new StringContent(JsonSerializer.Serialize(payload, WanJsonContext.Default.DashScopeSubmitBody), Encoding.UTF8, "application/json");
                return (r, key);
            }, ct).ConfigureAwait(false);

            if (sent is null)
                return (new(false, null, WanTaskStatus.Failed, uploadErr is not null && uploadErr.StartsWith("이미지 업로드 실패", StringComparison.Ordinal) ? uploadErr : "이미지 업로드 실패: " + uploadErr), false);

            using var resp = sent;
            using var body = await PooledUtf8Body.ReadAsync(resp.Content, ct).ConfigureAwait(false);
            var sc = (int)resp.StatusCode;
            var wire = WanWire.Parse(body.Span);

            if (!resp.IsSuccessStatusCode)
            {
                // DashScope 오류 본문: { code, message, request_id }
                var code = wire.Code;
                var msg  = wire.Message;
                return (new(false, null, WanTaskStatus.Failed,
                    $"제출 실패({provider} · HTTP {sc}{(code.Length > 0 ? $" · {code}" : "")}): {(msg.Length > 0 ? msg : body.Text)}"), IsQuota(sc));
            }

            wire.RequireObject();
            if (wire.HasOutput)
            {
                var taskId = wire.TaskId;
                var status = WanStatusEx.Parse(wire.TaskStatus);
                var reqId  = wire.RequestId;
                if (taskId.Length > 0)
                {
                    _taskRoutes[taskId] = new(provider, usedKey);
                    return (new(true, taskId, status, provider == WanTokenVault.DashScope ? "제출됨" : $"제출됨({provider} 페일오버)",
                        reqId.Length > 0 ? reqId : null), false);
                }
            }
            return (new(false, null, WanTaskStatus.Unknown, "제출 응답에 task_id 가 없습니다: " + body.Text), false);
        }
        catch (OperationCanceledException)
        {
            return (new(false, null, WanTaskStatus.Canceled, "제출이 취소되었습니다."), false);
        }
        catch (Exception ex)
        {
            return (new(false, null, WanTaskStatus.Unknown, "제출 실패(네트워크): " + ex.Message), false);
        }
    }

    // SiliconFlow Wan 2.1 제출 — i2v 는 매 요청 base64 data URI 로 이미지 동봉(계정 바운드 업로드 불필요 → 로테이션 시 자동 재전송).
    private async Task<(WanSubmitResult Result, bool QuotaHit)> SubmitSiliconFlowAsync(
        WanVideoRequest request, string prompt, bool isI2v, string initialKey, CancellationToken ct)
    {
        var body = new SiliconFlowSubmitBody
        {
            Model = SiliconFlowModel(isI2v, request.Model),
            Prompt = prompt,
            ImageSize = SiliconFlowSize(string.IsNullOrWhiteSpace(request.Size) ? DefaultSize : request.Size!),
            Seed = WanVideoRequest.ToCloudSeed(request.Seed),
        };
        var negative = (request.NegativePrompt ?? "").Trim();
        if (negative.Length > 0) body.NegativePrompt = negative.Length > 500 ? negative[..500] : negative;

        try
        {
            string? imagePath = null;
            long imageBytes = 0;
            if (isI2v)
            {
                if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
                    return (new(false, null, WanTaskStatus.Failed, "SiliconFlow i2v 는 로컬 원본 이미지가 필요합니다(사전 업로드 oss:// 재사용 불가)."), false);
                imagePath = request.ImagePath!;
                imageBytes = new FileInfo(imagePath).Length;
                if (imageBytes > MaxInlineImageBytes)
                    return (new(false, null, WanTaskStatus.Failed,
                        $"SiliconFlow 인라인 이미지 상한 초과({imageBytes / 1048576.0:0.0}MB > {MaxInlineImageBytes / 1048576}MB) — 해상도를 낮춰 다시 시도하세요."), false);
            }

            // v15.14.0: base64 는 문자열로 만들지 않고 전송 시 ArrayPool 청크 스트리밍(LOH 할당 0 · 로테이션마다 파일 재스트림).
            var json = JsonSerializer.Serialize(body, WanJsonContext.Default.SiliconFlowSubmitBody);
            var mime = imagePath is null ? "" : GuessImageContentType(imagePath);
            var (sent, usedKey) = await SendWithKeyRotationAsync(_http, vault, WanTokenVault.SiliconFlow, initialKey, MaxKeyRotations, key =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"{SiliconFlowBaseUrl}/v1/video/submit");
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                r.Content = imagePath is null
                    ? new StringContent(json, Encoding.UTF8, "application/json")
                    : new Base64ImageJsonContent(json, mime, imagePath, imageBytes);
                return r;
            }, ct).ConfigureAwait(false);

            using var resp = sent;
            using var text = await PooledUtf8Body.ReadAsync(resp.Content, ct).ConfigureAwait(false);
            var sc = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
                return (new(false, null, WanTaskStatus.Failed, $"제출 실패(SiliconFlow · HTTP {sc}): {text.Text}"), IsQuota(sc));

            var id = WanWire.Parse(text.Span).RequestIdCamel;
            if (id.Length == 0)
                return (new(false, null, WanTaskStatus.Unknown, "SiliconFlow 응답에 requestId 가 없습니다: " + text.Text), false);
            _taskRoutes[id] = new(WanTokenVault.SiliconFlow, usedKey);
            return (new(true, id, WanTaskStatus.Pending, "제출됨(SiliconFlow 페일오버)"), false);
        }
        catch (OperationCanceledException)
        {
            return (new(false, null, WanTaskStatus.Canceled, "제출이 취소되었습니다."), false);
        }
        catch (Exception ex)
        {
            return (new(false, null, WanTaskStatus.Unknown, "제출 실패(SiliconFlow 네트워크): " + ex.Message), false);
        }
    }

    private static bool IsQuota(int statusCode) => statusCode is 402 or 429;

    /// <summary>DashScope 모델명 → SiliconFlow 모델명(이미 "Wan-AI/" 형식이면 그대로).</summary>
    private static string SiliconFlowModel(bool isI2v, string? requested)
        => requested is { Length: > 0 } m && m.StartsWith("Wan-AI/", StringComparison.Ordinal) ? m
         : isI2v ? "Wan-AI/Wan2.1-I2V-14B-720P" : "Wan-AI/Wan2.1-T2V-14B";

    /// <summary>"가로*세로" → SiliconFlow 지원 해상도(1280x720 · 720x1280 · 960x960) 중 화면비 근사.</summary>
    public static string SiliconFlowSize(string size)
    {
        var s = size.AsSpan();
        int sep = s.IndexOfAny('*', 'x');
        if (sep > 0
            && int.TryParse(s[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(s[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            return w > h ? "1280x720" : w < h ? "720x1280" : "960x960";
        return "1280x720";
    }

    // ══ [2단계] 태스크 상태 조회(폴링 1회) ════════════════════════════════
    /// <inheritdoc/>
    public async Task<WanTaskResult> QueryAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new(false, WanTaskStatus.Unknown, null, null, "task_id 가 비어 있습니다.");

        var routed = _taskRoutes.TryGetValue(taskId, out var route);
        if (routed && route.Provider == WanTokenVault.SiliconFlow)
            return await QuerySiliconFlowAsync(taskId, route.Key, ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{(routed ? BaseFor(route.Provider) : BaseUrl)}/api/v1/tasks/{Uri.EscapeDataString(taskId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", routed ? route.Key : ApiKey);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            using var body = await PooledUtf8Body.ReadAsync(resp.Content, ct).ConfigureAwait(false);
            var wire = WanWire.Parse(body.Span);

            if (!resp.IsSuccessStatusCode)
            {
                var code = wire.Code;
                var msg  = wire.Message;
                return new(false, WanTaskStatus.Unknown, null, code.Length > 0 ? code : null,
                    $"조회 실패(HTTP {(int)resp.StatusCode}): {(msg.Length > 0 ? msg : body.Text)}");
            }

            wire.RequireObject();
            if (!wire.HasOutput)
                return new(false, WanTaskStatus.Unknown, null, null, "조회 응답에 output 이 없습니다.");

            var status = WanStatusEx.Parse(wire.TaskStatus);
            switch (status)
            {
                case WanTaskStatus.Succeeded:
                    _taskRoutes.TryRemove(taskId, out _);
                    var url = wire.VideoUrl;
                    return url.Length > 0
                        ? new(true, status, url, null, "완료")
                        : new(false, status, null, null, "SUCCEEDED 이지만 video_url 이 없습니다.");
                case WanTaskStatus.Failed:
                    _taskRoutes.TryRemove(taskId, out _);
                    return new(false, status, null, wire.OutputCode, "실패: " + wire.OutputMessage);
                case WanTaskStatus.Canceled:
                    _taskRoutes.TryRemove(taskId, out _);
                    return new(false, status, null, null, "취소됨");
                default: // Pending / Running / Unknown → 계속 폴링
                    return new(true, status, null, null, status.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            return new(false, WanTaskStatus.Canceled, null, null, "조회가 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return new(false, WanTaskStatus.Unknown, null, null, "조회 실패(네트워크): " + ex.Message);
        }
    }

    // SiliconFlow 상태 조회 — { status: InQueue|InProgress|Succeed|Failed, reason, results.videos[0].url }
    private async Task<WanTaskResult> QuerySiliconFlowAsync(string requestId, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SiliconFlowBaseUrl}/v1/video/status");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(JsonSerializer.Serialize(new SiliconFlowStatusBody(requestId), WanJsonContext.Default.SiliconFlowStatusBody), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            using var body = await PooledUtf8Body.ReadAsync(resp.Content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new(false, WanTaskStatus.Unknown, null, null, $"조회 실패(SiliconFlow · HTTP {(int)resp.StatusCode}): {body.Text}");

            var wire = WanWire.Parse(body.Span);
            switch (wire.Status)
            {
                case "Succeed":
                    _taskRoutes.TryRemove(requestId, out _);
                    var url = wire.FirstVideoUrl;
                    return url.Length > 0
                        ? new(true, WanTaskStatus.Succeeded, url, null, "완료")
                        : new(false, WanTaskStatus.Succeeded, null, null, "Succeed 이지만 video url 이 없습니다.");
                case "Failed":
                    _taskRoutes.TryRemove(requestId, out _);
                    return new(false, WanTaskStatus.Failed, null, null, "실패: " + wire.Reason);
                case "InProgress":
                    return new(true, WanTaskStatus.Running, null, null, "Running");
                default:   // InQueue
                    return new(true, WanTaskStatus.Pending, null, null, "Pending");
            }
        }
        catch (OperationCanceledException)
        {
            return new(false, WanTaskStatus.Canceled, null, null, "조회가 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return new(false, WanTaskStatus.Unknown, null, null, "조회 실패(SiliconFlow 네트워크): " + ex.Message);
        }
    }

    // ══ [연결 테스트] 저장 없이 인증만 확인(과금 없음) ════════════════════
    /// <inheritdoc/>
    public async Task<(bool Ok, string Message)> ValidateKeyAsync(string apiKey, string baseUrl, CancellationToken ct = default)
    {
        var key = (apiKey ?? "").Trim();
        if (key.Length == 0) return (false, "API 키를 입력하세요.");
        var host = (string.IsNullOrWhiteSpace(baseUrl) ? "https://dashscope-intl.aliyuncs.com" : baseUrl.Trim()).TrimEnd('/');

        // 더미 task_id 조회로 인증만 검증한다 — 영상 생성/과금 없음.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{host}/api/v1/tasks/promptflow-validate-0000");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            using var body = await PooledUtf8Body.ReadAsync(resp.Content, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"인증 실패(HTTP {(int)resp.StatusCode}) — API 키 또는 지역(BaseUrl)을 확인하세요.");

            var code = ReadRootCode(body.Span);
            if (LooksLikeAuthError(code))
                return (false, $"인증 실패({code}) — API 키를 확인하세요.");

            // 그 외(200 · 400 InvalidParameter · 404 task not found 등)는 키 인증 통과로 판정.
            return (true, "연결 성공 — API 키가 유효합니다.");
        }
        catch (OperationCanceledException) { return (false, "연결 테스트가 취소되었습니다."); }
        catch (Exception ex) { return (false, "연결 실패(네트워크): " + ex.Message); }
    }

    // ══ [I2V] 로컬 이미지 업로드(getPolicy → OSS 멀티파트) → oss:// ═════════
    /// <inheritdoc/>
    public async Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
        => (await UploadImageCoreAsync(localImagePath, model, WanTokenVault.DashScope, ApiKey, ct).ConfigureAwait(false)).Result;

    // v15.14.0: getPolicy 도 402/429 시 소진 표시 + 키 로테이션. 반환 Key = 실제 업로드 계정 키(제출도 이 키로).
    private async Task<(WanUploadResult Result, string Key)> UploadImageCoreAsync(
        string localImagePath, string? model, string provider, string apiKey, CancellationToken ct)
    {
        if (!Enabled) return (new(false, null, "Wan API 키가 구성되지 않았습니다."), apiKey);
        if (string.IsNullOrWhiteSpace(localImagePath) || !File.Exists(localImagePath))
            return (new(false, null, "이미지 파일을 찾을 수 없습니다: " + Path.GetFileName(localImagePath), WanUploadFailure.InvalidFile), apiKey);

        // v16.3.1: 업로드 전 사전 검증 — 크기(0B·32MB 초과) · 실제 이미지 시그니처(확장자 위장/손상 차단).
        long fileLength;
        string contentType;
        try
        {
            fileLength = new FileInfo(localImagePath).Length;
            if (fileLength == 0) return (new(false, null, "이미지 업로드 실패: 빈 파일입니다.", WanUploadFailure.InvalidFile), apiKey);
            if (fileLength > MaxUploadImageBytes)
                return (new(false, null, $"이미지 업로드 실패: 이미지가 너무 큽니다({fileLength / (1024.0 * 1024.0):0.0}MB · 최대 {MaxUploadImageBytes / (1024 * 1024)}MB).", WanUploadFailure.TooLarge), apiKey);
            contentType = SniffImageContentType(localImagePath) ?? "";
            if (contentType.Length == 0)
                return (new(false, null, "이미지 업로드 실패: 지원하지 않는 이미지 형식입니다(JPG·PNG·WEBP·BMP·GIF).", WanUploadFailure.InvalidFile), apiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new(false, null, "이미지 업로드 실패: 이미지 파일을 읽을 수 없습니다(다른 프로그램에서 사용 중이거나 권한 없음).", WanUploadFailure.InvalidFile), apiKey);
        }

        var useModel = string.IsNullOrWhiteSpace(model) ? DefaultI2vModel : model!;
        try
        {
            // ① 업로드 정책(getPolicy) — 모델 스코프의 임시 OSS 자격 획득.
            var policyUrl = $"{BaseFor(provider)}/api/v1/uploads?action=getPolicy&model={Uri.EscapeDataString(useModel)}";
            var (sentPolicy, policyKey) = await SendWithKeyRotationAsync(_http, vault, provider, apiKey, MaxKeyRotations, k =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, policyUrl);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
                return r;
            }, ct).ConfigureAwait(false);
            apiKey = policyKey;
            using var polResp = sentPolicy;
            using var polBody = await PooledUtf8Body.ReadAsync(polResp.Content, ct).ConfigureAwait(false);
            if (!polResp.IsSuccessStatusCode)
            {
                Program.Log("WanApiService.UploadPolicy", new HttpRequestException($"HTTP {(int)polResp.StatusCode}: {polBody.Text}"));
                var status = (int)polResp.StatusCode;
                return (new(false, null, $"이미지 업로드 실패: 업로드 정책 요청이 거부되었습니다(HTTP {status}).",
                    status >= 500 || status == 429 ? WanUploadFailure.ServerError : WanUploadFailure.PolicyRejected), apiKey);
            }

            var pol = WanWire.Parse(polBody.Span);
            pol.RequireObject();
            if (!pol.HasData)
            {
                Program.Log("WanApiService.UploadPolicy", new InvalidDataException("data 없음: " + polBody.Text));
                return (new(false, null, "이미지 업로드 실패: 업로드 정책 응답이 올바르지 않습니다.", WanUploadFailure.ServerError), apiKey);
            }

            var host      = pol.UploadHost;
            var dir       = pol.UploadDir;
            var accessId  = pol.AccessKeyId;
            var signature = pol.Signature;
            var policy    = pol.Policy;
            var acl       = pol.ObjectAcl;
            var forbid    = pol.ForbidOverwrite;
            if (host.Length == 0 || dir.Length == 0 || policy.Length == 0 || signature.Length == 0 || accessId.Length == 0)
            {
                Program.Log("WanApiService.UploadPolicy", new InvalidDataException("정책 필드 불완전: " + polBody.Text));
                return (new(false, null, "이미지 업로드 실패: 업로드 정책 정보가 불완전합니다.", WanUploadFailure.ServerError), apiKey);
            }
            if (pol.MaxFileSizeMb > 0 && fileLength > pol.MaxFileSizeMb * 1024L * 1024L)
                return (new(false, null, $"이미지 업로드 실패: 이미지가 업로드 허용 용량({pol.MaxFileSizeMb}MB)을 초과했습니다.", WanUploadFailure.TooLarge), apiKey);

            // ② OSS PostObject 멀티파트 — 객체 키·filename 은 ASCII 안전형(유니코드/공백 파일명 → 헤더·키 깨짐 방지).
            //    고유 접두(8hex)로 같은 이름 재업로드 시 forbid-overwrite 충돌 방지. 'file' 필드는 OssPostObjectContent 가 마지막에 둔다.
            var safeName = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()
                + "_" + OssPostObjectContent.SafeAsciiFileName(Path.GetFileName(localImagePath));
            var key = $"{dir.TrimEnd('/')}/{safeName}";
            var fields = new List<KeyValuePair<string, string>>(8)
            {
                new("key", key),
                new("policy", policy),
                new("OSSAccessKeyId", accessId),
                new("Signature", signature),
                new("x-oss-content-type", contentType),
                new("success_action_status", "200"),
            };
            if (acl.Length > 0)    fields.Add(new("x-oss-object-acl", acl));
            if (forbid.Length > 0) fields.Add(new("x-oss-forbid-overwrite", forbid));

            // 업로드 타임아웃: 크기 비례(60s + 8s/MB · 최대 5분) — 요청 1건 30s 클라이언트와 분리.
            var budget = TimeSpan.FromSeconds(Math.Min(300, 60 + 8 * Math.Ceiling(fileLength / (1024.0 * 1024.0))));
            WanUploadResult? last = null;
            for (int attempt = 0; attempt < UploadAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500 << attempt), ct).ConfigureAwait(false);   // 지수 백오프 1s · 2s
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(budget);
                try
                {
                    using var form = new OssPostObjectContent(CollectionsMarshal.AsSpan(fields), safeName, contentType, localImagePath);
                    using var upResp = await _uploadHttp.PostAsync(host, form, timeout.Token).ConfigureAwait(false);
                    if (upResp.IsSuccessStatusCode) return (new(true, $"oss://{key}", "업로드 완료"), apiKey);

                    using var upBody = await PooledUtf8Body.ReadAsync(upResp.Content, timeout.Token).ConfigureAwait(false);
                    var raw = upBody.Text;
                    Program.Log("WanApiService.UploadOss", new HttpRequestException($"HTTP {(int)upResp.StatusCode} ({fileLength}B, {contentType}): {raw}"));   // 원문 XML 은 로그 전용
                    var (kind, msg) = OssErrorMapper.Map((int)upResp.StatusCode, OssErrorMapper.ParseXmlError(raw).Code);
                    last = new(false, null, msg, kind);
                    if (!OssErrorMapper.IsTransient(kind)) return (last, apiKey);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new(false, null, $"이미지 업로드 실패: 업로드 시간이 초과되었습니다({budget.TotalSeconds:0}초). 네트워크 상태를 확인해 주세요.", WanUploadFailure.Timeout);
                }
                catch (HttpRequestException hx)
                {
                    Program.Log("WanApiService.UploadOss", hx);
                    last = new(false, null, "이미지 업로드 실패: 네트워크 연결이 불안정하여 업로드 서버에 연결하지 못했습니다.", WanUploadFailure.Network);
                }
            }
            return (last ?? new(false, null, "이미지 업로드 실패", WanUploadFailure.Network), apiKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (new(false, null, "업로드가 취소되었습니다."), apiKey); }
        catch (OperationCanceledException)
        {
            return (new(false, null, "이미지 업로드 실패: 업로드 정책 요청 시간이 초과되었습니다. 네트워크 상태를 확인해 주세요.", WanUploadFailure.Timeout), apiKey);
        }
        catch (HttpRequestException hx)
        {
            Program.Log("WanApiService.UploadImageAsync", hx);
            return (new(false, null, "이미지 업로드 실패: 네트워크 연결이 불안정하여 업로드 정책을 받지 못했습니다.", WanUploadFailure.Network), apiKey);
        }
        catch (Exception ex)
        {
            Program.Log("WanApiService.UploadImageAsync", ex);
            return (new(false, null, "이미지 업로드 실패: " + ErrorDialogService.CleanMessage(ex.Message), WanUploadFailure.ServerError), apiKey);
        }
    }

    /// <summary>v16.3.1 업로드 이미지 상한(32MB) — UI 선택기·서비스 사전 검증 공통.</summary>
    public const long MaxUploadImageBytes = 32L * 1024 * 1024;
    private const int UploadAttempts = 3;

    /// <summary>파일 시그니처(매직 바이트)로 이미지 MIME 판별 — 확장자 무관. 지원 외/손상이면 null.</summary>
    internal static string? SniffImageContentType(string path)
    {
        Span<byte> h = stackalloc byte[12];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            n = fs.ReadAtLeast(h, h.Length, throwOnEndOfStream: false);
        h = h[..n];
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        if (h.Length >= 4 && h[0] == 0x89 && h[1..4].SequenceEqual("PNG"u8)) return "image/png";
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (h.Length >= 6 && h[..3].SequenceEqual("GIF"u8)) return "image/gif";
        if (h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M') return "image/bmp";
        return null;
    }

    // ══ [고수준] 제출 + 폴링 오케스트레이션 ═══════════════════════════════
    /// <inheritdoc/>
    public async Task<WanGenerationResult> GenerateAsync(
        WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // v15.14.0: 클라우드 키 전멸(0개/전부 Exhausted) → 로컬 GPU(ComfyUI) 다이렉트 위임.
        if (CanFallBackLocal) return await RunLocalFallbackAsync(request, progress, ct).ConfigureAwait(false);

        // ── [제출] 속도 제한(429/쓰로틀) 자동 방어 — 지수 백오프 재시도(QuotaRateGuardService). 정상 흐름엔 무영향(Graceful) ──
        progress?.Report(new(WanTaskStatus.Pending, "생성 요청 제출 중…", 5, 0, sw.Elapsed, null));
        WanSubmitResult submit;
        var submitTry = 0;
        while (true)
        {
            await guard.WaitIfCoolingAsync(ct).ConfigureAwait(false);   // 다른 작업이 건 공유 쿨다운 동반 대기(병렬 안전)
            submit = await SubmitAsync(request, ct).ConfigureAwait(false);
            if (submit.Ok && submit.TaskId is not null) { guard.ReportSuccess(); break; }
            if (CanFallBackLocal) return await RunLocalFallbackAsync(request, progress, ct).ConfigureAwait(false);   // 로테이션 끝단 전멸

            // 속도 제한 계열이면 지수 백오프 후 자동 재시도(상한 내에서). 그 외 오류는 즉시 실패 반환(기존 동작).
            if (QuotaRateGuardService.LooksRateLimited(null, null, submit.Message) && submitTry < guard.MaxRetryAttempts && !ct.IsCancellationRequested)
            {
                submitTry++;
                progress?.Report(new(WanTaskStatus.Pending, $"⚠️ API 속도 제한 — 쿨다운 후 재시도 ({submitTry}/{guard.MaxRetryAttempts})", 6, 0, sw.Elapsed, null));
                await guard.CoolDownAsync(null, "생성 제출 속도 제한(429/쓰로틀)", ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return new(false, WanTaskStatus.Canceled, null, "취소됨", null, sw.Elapsed);
                continue;
            }

            progress?.Report(new(WanTaskStatus.Failed, submit.Message, 0, 0, sw.Elapsed, null));
            return new(false, WanTaskStatus.Failed, null, submit.Message, submit.TaskId, sw.Elapsed);
        }

        var taskId = submit.TaskId;
        progress?.Report(new(WanTaskStatus.Pending, "대기열에서 순서를 기다리는 중…", 15, 0, sw.Elapsed, taskId));

        var interval = TimeSpan.FromSeconds(PollIntervalSec);
        var deadline = TimeSpan.FromSeconds(MaxWaitSec);
        var poll = 0;

        await foreach (var q in PollAsync(taskId, interval, ct).ConfigureAwait(false))
        {
            poll++;

            // 폴링 중 속도 제한(429/쓰로틀) 자동 방어 — 정상 응답은 백오프 리셋, 쓰로틀은 지수 대기 후 계속 폴링(작업은 서버에서 진행).
            if (q.Ok) guard.ReportSuccess();
            else if (!q.Status.IsTerminal() && QuotaRateGuardService.LooksRateLimited(null, q.Code, q.Message))
            {
                progress?.Report(new(WanTaskStatus.Pending, "⚠️ API 쿨다운 — 상태 조회 대기 중…", Math.Min(90, 25 + poll * 5), poll, sw.Elapsed, taskId));
                await guard.CoolDownAsync(null, "상태 조회 속도 제한(429/쓰로틀)", ct).ConfigureAwait(false);
            }

            if (q.Status == WanTaskStatus.Succeeded && q.VideoUrl is not null)
            {
                progress?.Report(new(WanTaskStatus.Succeeded, "완성!", 100, poll, sw.Elapsed, taskId));
                return new(true, WanTaskStatus.Succeeded, q.VideoUrl, "완료", taskId, sw.Elapsed);
            }
            if (q.Status.IsTerminal())   // Failed / Canceled
            {
                progress?.Report(new(q.Status, q.Message, 0, poll, sw.Elapsed, taskId));
                return new(false, q.Status, null, q.Message, taskId, sw.Elapsed);
            }

            // 진행 중(Pending/Running) — 서버 백분율이 없어 상태·폴링수로 추정치(최대 90%)를 만든다.
            var pct   = q.Status == WanTaskStatus.Running ? Math.Min(90, 25 + poll * 5) : Math.Min(20, 15 + poll);
            var phase = q.Status == WanTaskStatus.Running ? "영상 렌더링 중…" : "대기열에서 순서를 기다리는 중…";
            progress?.Report(new(q.Status, phase, pct, poll, sw.Elapsed, taskId));

            if (sw.Elapsed >= deadline)
            {
                var msg = $"시간 초과({MaxWaitSec}s) — 서버에서 아직 완료되지 않았습니다(task_id={taskId}).";
                progress?.Report(new(WanTaskStatus.Unknown, msg, pct, poll, sw.Elapsed, taskId));
                return new(false, WanTaskStatus.Unknown, null, msg, taskId, sw.Elapsed);
            }
        }

        // 스트림 종료 = 폴링 대기 중 취소.
        progress?.Report(new(WanTaskStatus.Canceled, "사용자가 취소했습니다.", 0, poll, sw.Elapsed, taskId));
        return new(false, WanTaskStatus.Canceled, null, "취소됨", taskId, sw.Elapsed);
    }

    /// <summary>
    /// v15.15.0 폴링 스트림 — <paramref name="interval"/> 대기 후 <see cref="QueryAsync"/> 결과를 순차 방출(소비자가 종료 판정).
    /// 대기 중 취소되면 예외 없이 스트림을 끝낸다(조회 중 취소는 <see cref="WanTaskStatus.Canceled"/> 결과로 방출).
    /// </summary>
    public async IAsyncEnumerable<WanTaskResult> PollAsync(
        string taskId, TimeSpan interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            bool canceled = false;
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            if (canceled) yield break;
            yield return await QueryAsync(taskId, ct).ConfigureAwait(false);
        }
    }

    private Task<WanGenerationResult> RunLocalFallbackAsync(WanVideoRequest request, IProgress<WanProgress>? progress, CancellationToken ct)
    {
        var local = localFallback!;   // CanFallBackLocal 통과 시에만 호출
        progress?.Report(new(WanTaskStatus.Pending, $"☁️ 클라우드 키 전멸 → 로컬 GPU({local.DisplayName}) 폴백", 5, 0, TimeSpan.Zero, null));
        return local.GenerateAsync(request, progress, ct);
    }

    // ── 유틸 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// v15.12.0 무중단 키 로테이션 송신 — HTTP 402(Payment Required)/429(Too Many Requests) 응답이면
    /// 해당 키를 <see cref="WanTokenVault.MarkExhausted"/>(402 영구 · 429 쿨다운) 처리하고
    /// <see cref="WanTokenVault.RotateKey"/> 로 다음 활성 키를 받아 최대 <paramref name="maxRotations"/> 회 즉시 재시도.
    /// 대체 키가 없거나 한도 소진 시 마지막 응답을 그대로 반환(호출부가 기존 오류 처리/QuotaRateGuard 쿨다운 수행).
    /// </summary>
    public static async Task<(HttpResponseMessage Response, string Key)> SendWithKeyRotationAsync(
        HttpClient http, WanTokenVault vault, string provider, string initialKey, int maxRotations,
        Func<string, HttpRequestMessage> buildRequest, CancellationToken ct)
    {
        var (resp, key) = await SendWithKeyRotationAsync(http, vault, provider, initialKey, maxRotations,
            (k, _) => new ValueTask<(HttpRequestMessage?, string)>((buildRequest(k), k)), ct).ConfigureAwait(false);
        return (resp!, key);   // 동기 빌더는 null 을 반환하지 않음
    }

    /// <summary>
    /// v15.13.0 비동기 빌더 버전 — 로테이션마다 새 키로 요청을 재구성(i2v 이미지 재업로드 등).
    /// 빌더가 반환한 Key 가 실제 송신 키(v15.14.0: 업로드 단계 로테이션 반영). 요청이 null 이면(재구성 불가) 직전 응답(없으면 null)을 반환한다.
    /// </summary>
    public static async Task<(HttpResponseMessage? Response, string Key)> SendWithKeyRotationAsync(
        HttpClient http, WanTokenVault vault, string provider, string initialKey, int maxRotations,
        Func<string, CancellationToken, ValueTask<(HttpRequestMessage? Request, string Key)>> buildRequest, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        var lastKey = initialKey;
        var key = initialKey;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                (var req, key) = await buildRequest(key, ct).ConfigureAwait(false);
                if (req is null) return (last, lastKey);

                HttpResponseMessage resp;
                using (req)
                    resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                last?.Dispose();
                last = resp;
                lastKey = key;

                var sc = (int)resp.StatusCode;
                if (!IsQuota(sc)) return (resp, key);
                vault.MarkExhausted(provider, key, sc == 429 ? WanTokenVault.RateLimitCooldown : null);
                if (attempt >= maxRotations || !vault.RotateKey(provider, key, out var next))   // 활성 대체 키 없음 → 마지막 응답 반환
                    return (resp, key);
                ZenithEventSource.Log.OnKeyRotated(provider);   // v15.17.0 ETW(0B)
                key = next;
            }
        }
        catch
        {
            last?.Dispose();
            throw;
        }
    }

    /// <summary>응답 본문 루트의 code 필드를 안전하게 읽는다(파싱 실패 시 빈 문자열 · Zero-Copy).</summary>
    private static string ReadRootCode(ReadOnlySpan<byte> utf8)
    {
        try { return WanWire.Parse(utf8).Code; }
        catch { return ""; }
    }

    /// <summary>DashScope 오류 code 가 인증 실패 계열인지.</summary>
    private static bool LooksLikeAuthError(string code) =>
        code.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase)
        || code.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || code.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
        || code.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>확장자로 이미지 MIME 추정(업로드 x-oss-content-type 용).</summary>
    private static string GuessImageContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png"               => "image/png",
        ".jpg" or ".jpeg"    => "image/jpeg",
        ".webp"              => "image/webp",
        ".bmp"               => "image/bmp",
        ".gif"               => "image/gif",
        _                    => "application/octet-stream",
    };

    /// <summary>
    /// v15.14.0 메모리 세이프 SiliconFlow 본문 — <c>{...,"image":"data:mime;base64,&lt;스트림&gt;"}</c>.
    /// 파일을 48KB(3의 배수) ArrayPool 청크로 읽어 <see cref="Base64.EncodeToUtf8"/> 로 즉시 기록 — 거대 base64 문자열/LOH 할당 없음.
    /// </summary>
    internal sealed class Base64ImageJsonContent : HttpContent
    {
        private const int ChunkBytes = 3 * 16384;
        private readonly byte[] _head;
        private readonly byte[] _tail = "\"}"u8.ToArray();
        private readonly string _path;
        private readonly long _fileBytes;

        public Base64ImageJsonContent(string jsonWithoutImage, string mime, string path, long fileBytes)
        {
            // jsonWithoutImage 는 비어있지 않은 객체("{...}") — 끝 '}' 를 떼고 image 필드를 이어 붙인다.
            _head = Encoding.UTF8.GetBytes(jsonWithoutImage[..^1] + ",\"image\":\"data:" + mime + ";base64,");
            _path = path;
            _fileBytes = fileBytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync(_head, ct).ConfigureAwait(false);
            var src = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            var dst = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(ChunkBytes));
            try
            {
                await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int carry = 0;
                while (true)
                {
                    int n = await fs.ReadAsync(src.AsMemory(carry, ChunkBytes - carry), ct).ConfigureAwait(false);
                    int total = carry + n;
                    bool final = n == 0;
                    if (total == 0) break;
                    int encodeLen = final ? total : total - total % 3;   // 중간 청크는 3바이트 경계까지만(패딩 방지)
                    Base64.EncodeToUtf8(src.AsSpan(0, encodeLen), dst, out _, out int written, isFinalBlock: final);
                    await stream.WriteAsync(dst.AsMemory(0, written), ct).ConfigureAwait(false);
                    carry = total - encodeLen;
                    if (carry > 0) Buffer.BlockCopy(src, encodeLen, src, 0, carry);
                    if (final) break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(src);
                ArrayPool<byte>.Shared.Return(dst);
            }
            await stream.WriteAsync(_tail, ct).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _head.Length + (_fileBytes + 2) / 3 * 4 + _tail.Length;
            return true;
        }
    }
}
