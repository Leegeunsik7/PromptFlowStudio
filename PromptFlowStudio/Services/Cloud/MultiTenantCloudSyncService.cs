using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Cloud;

/// <summary>테넌트 사용자 역할.</summary>
public enum TenantRole { Creator, Director, Admin }

/// <summary>동기화 상태.</summary>
public enum SyncState { Idle, Syncing, Synced, RemoteLocked, Conflict, Offline }

/// <summary>테넌트 컨텍스트 — 식별자·사용자·역할·API 키·서버 주소.</summary>
public sealed record TenantContext(string TenantId, string UserId, TenantRole Role, string CloudApiKey, string BaseUrl);

/// <summary>원격 편집 잠금 — 소유자·획득 시각·TTL(초).</summary>
public sealed record RemoteLock(string Owner, DateTimeOffset AcquiredAt, int TtlSeconds)
{
    public bool IsExpired => DateTimeOffset.Now > AcquiredAt.AddSeconds(TtlSeconds);
    public bool IsHeldBy(string user) => !IsExpired && string.Equals(Owner, user, StringComparison.OrdinalIgnoreCase);
}

/// <summary>동기화 결과 — 성공·상태·버전·메시지.</summary>
public sealed record SyncResult(bool Ok, SyncState State, int Version, string Message);

/// <summary>
/// [PromptFlowStudio] 엔터프라이즈 멀티 테넌트 중앙 클라우드 동기화(v2.9.0 · 관찰 가능 싱글턴 · 순수 BCL <see cref="HttpClient"/> · 외부 NuGet 0).
///   ▸ <b>테넌트 격리</b>: 모든 요청을 <c>/tenants/{TenantId}/projects/{id}</c> + <c>X-Tenant-Id</c>/<c>Authorization: Bearer</c> 로 스코프한다.
///   ▸ <b>역할 기반 권한</b>: Creator(편집) · Director(편집+게시) · Admin(테넌트 관리) — <see cref="CanEdit"/>/<see cref="CanPublish"/>/<see cref="CanManageTenant"/>.
///   ▸ <b>분산 낙관적 잠금</b>: Push 전 원격 버전과 로컬 <see cref="StoryboardProject.SyncVersion"/> 비교(<see cref="DetectConflict"/>)로 충돌 방지 · 원격 편집 잠금(<see cref="RemoteLock"/>).
///   ▸ 서버 부재/오프라인은 <see cref="SyncState.Offline"/> 로 Graceful(로컬 작업 지속). 자산/JSON 양방향 Push/Pull.
/// </summary>
public sealed class MultiTenantCloudSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>상태 변경 방송(동기화 아이콘/잠금 표시).</summary>
    public event Action? OnChange;

    /// <summary>현재 테넌트 컨텍스트(미구성 시 null).</summary>
    public TenantContext? Context { get; private set; }
    /// <summary>현재 동기화 상태.</summary>
    public SyncState State { get; private set; } = SyncState.Idle;
    /// <summary>마지막 메시지.</summary>
    public string LastMessage { get; private set; } = "미구성";
    /// <summary>현재 원격 잠금(있으면).</summary>
    public RemoteLock? Lock { get; private set; }
    /// <summary>구성 완료 여부.</summary>
    public bool Configured => Context is not null && Context.TenantId.Length > 0;

    /// <summary>테넌트 컨텍스트를 설정한다.</summary>
    public void Configure(TenantContext ctx)
    {
        Context = ctx;
        State = SyncState.Idle;
        LastMessage = $"테넌트 {ctx.TenantId} · {ctx.Role} 구성됨";
        OnChange?.Invoke();
    }

    // ══════════════════════ 역할 권한(순수 · 테스트 대상) ══════════════════════

    /// <summary>편집 권한 — 전 역할 가능.</summary>
    public static bool CanEdit(TenantRole role) => true;
    /// <summary>게시(중앙 Push) 권한 — Director 이상.</summary>
    public static bool CanPublish(TenantRole role) => role is TenantRole.Director or TenantRole.Admin;
    /// <summary>테넌트 관리(사용자/키) 권한 — Admin 전용.</summary>
    public static bool CanManageTenant(TenantRole role) => role == TenantRole.Admin;
    /// <summary>원격 잠금 획득 권한 — Director 이상.</summary>
    public static bool CanAcquireLock(TenantRole role) => role is TenantRole.Director or TenantRole.Admin;

    // ══════════════════════ 충돌 감지 / 격리 경로(순수) ══════════════════════

    /// <summary>낙관적 잠금 충돌 — 원격 버전이 로컬보다 앞서면 충돌(다른 사용자가 먼저 Push).</summary>
    public static bool DetectConflict(int localVersion, int remoteVersion) => remoteVersion > localVersion;

    /// <summary>테넌트 격리 프로젝트 엔드포인트.</summary>
    public static string BuildProjectEndpoint(string baseUrl, string tenantId, string projectId)
        => $"{baseUrl.TrimEnd('/')}/tenants/{Uri.EscapeDataString(tenantId)}/projects/{Uri.EscapeDataString(projectId)}";

    /// <summary>프로젝트 JSON 의 SHA-256(변경 감지용).</summary>
    public static string ContentHash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json ?? "")));

    // ══════════════════════ 원격 잠금 ══════════════════════

    /// <summary>원격 편집 잠금을 획득한다(권한/충돌 검사 · 오프라인 Graceful).</summary>
    public async Task<SyncResult> AcquireLockAsync(StoryboardProject project, int ttlSeconds = 300, CancellationToken ct = default)
    {
        if (!Configured) return Set(false, SyncState.Idle, project.SyncVersion, "테넌트 미구성");
        if (!CanAcquireLock(Context!.Role)) return Set(false, State, project.SyncVersion, $"{Context.Role}는 잠금 권한이 없습니다(Director 이상).");
        try
        {
            using var req = NewRequest(HttpMethod.Put, project.Id, "lock");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) { Lock = new RemoteLock(Context.UserId, DateTimeOffset.Now, ttlSeconds); return Set(true, SyncState.Synced, project.SyncVersion, "원격 잠금 획득"); }
            if ((int)resp.StatusCode == 423) { return Set(false, SyncState.RemoteLocked, project.SyncVersion, "다른 사용자가 편집 중(원격 잠금)"); }
            return Set(false, SyncState.Offline, project.SyncVersion, $"잠금 실패({(int)resp.StatusCode})");
        }
        catch (Exception ex) { Program.Log("Cloud.Lock", ex); return Set(false, SyncState.Offline, project.SyncVersion, "오프라인 — 로컬 편집 계속"); }
    }

    /// <summary>원격 잠금을 해제한다(로컬 상태만이라도 Graceful).</summary>
    public async Task<SyncResult> ReleaseLockAsync(StoryboardProject project, CancellationToken ct = default)
    {
        if (!Configured) return Set(false, SyncState.Idle, project.SyncVersion, "테넌트 미구성");
        try
        {
            using var req = NewRequest(HttpMethod.Delete, project.Id, "lock");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Program.Log("Cloud.Unlock", ex); }
        Lock = null;
        return Set(true, SyncState.Idle, project.SyncVersion, "잠금 해제");
    }

    // ══════════════════════ Push / Pull ══════════════════════

    /// <summary>로컬 프로젝트를 중앙 서버로 Push 한다(게시 권한 + 낙관적 잠금 검사 · 성공 시 SyncVersion 증가).</summary>
    public async Task<SyncResult> PushAsync(StoryboardProject project, CancellationToken ct = default)
    {
        if (!Configured) return Set(false, SyncState.Idle, project.SyncVersion, "테넌트 미구성");
        if (!CanPublish(Context!.Role)) return Set(false, State, project.SyncVersion, $"{Context.Role}는 Push 권한이 없습니다(Director 이상).");
        if (Lock is { } lk && !lk.IsHeldBy(Context.UserId)) return Set(false, SyncState.RemoteLocked, project.SyncVersion, "원격 잠금 미보유 — Push 차단");

        Set(true, SyncState.Syncing, project.SyncVersion, "Push 중…");
        try
        {
            // 1) 원격 버전 확인 → 충돌 감지.
            var remoteVer = await FetchRemoteVersionAsync(project.Id, ct).ConfigureAwait(false);
            if (remoteVer is int rv && DetectConflict(project.SyncVersion, rv))
                return Set(false, SyncState.Conflict, project.SyncVersion, $"충돌: 원격 v{rv} > 로컬 v{project.SyncVersion} — 먼저 Pull 하세요.");

            // 2) JSON Push.
            var json = JsonSerializer.Serialize(project, PromptFlowJsonContext.Default.StoryboardProject);
            using var req = NewRequest(HttpMethod.Put, project.Id, null);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("X-Content-Hash", ContentHash(json));
            req.Headers.TryAddWithoutValidation("If-Match", project.SyncVersion.ToString());
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) { project.SyncVersion += 1; return Set(true, SyncState.Synced, project.SyncVersion, $"Push 완료 · v{project.SyncVersion}"); }
            if ((int)resp.StatusCode == 409) return Set(false, SyncState.Conflict, project.SyncVersion, "충돌(409) — Pull 후 재시도");
            return Set(false, SyncState.Offline, project.SyncVersion, $"Push 실패({(int)resp.StatusCode})");
        }
        catch (Exception ex) { Program.Log("Cloud.Push", ex); return Set(false, SyncState.Offline, project.SyncVersion, "오프라인 — 변경분 로컬 보관(재연결 시 Push)"); }
    }

    /// <summary>중앙 서버에서 프로젝트 JSON 을 Pull 한다(오프라인 Graceful).</summary>
    public async Task<(SyncResult Result, string? Json)> PullAsync(string projectId, CancellationToken ct = default)
    {
        if (!Configured) return (Set(false, SyncState.Idle, 0, "테넌트 미구성"), null);
        Set(true, SyncState.Syncing, 0, "Pull 중…");
        try
        {
            using var req = NewRequest(HttpMethod.Get, projectId, null);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (Set(false, SyncState.Offline, 0, $"Pull 실패({(int)resp.StatusCode})"), null);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (Set(true, SyncState.Synced, 0, "Pull 완료"), json);
        }
        catch (Exception ex) { Program.Log("Cloud.Pull", ex); return (Set(false, SyncState.Offline, 0, "오프라인"), null); }
    }

    private async Task<int?> FetchRemoteVersionAsync(string projectId, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Head, projectId, null);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.Headers.TryGetValues("X-Sync-Version", out var vs) && int.TryParse(System.Linq.Enumerable.FirstOrDefault(vs), out var v)) return v;
        }
        catch { }
        return null;   // 확인 불가 → 충돌 아님으로 진행(서버가 최종 If-Match 로 판정)
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string projectId, string? suffix)
    {
        var url = BuildProjectEndpoint(Context!.BaseUrl, Context.TenantId, projectId) + (suffix is null ? "" : "/" + suffix);
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Tenant-Id", Context.TenantId);
        req.Headers.TryAddWithoutValidation("X-User-Id", Context.UserId);
        if (!string.IsNullOrWhiteSpace(Context.CloudApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Context.CloudApiKey);
        return req;
    }

    private SyncResult Set(bool ok, SyncState state, int version, string msg)
    {
        State = state; LastMessage = msg; OnChange?.Invoke();
        return new SyncResult(ok, state, version, msg);
    }
}
