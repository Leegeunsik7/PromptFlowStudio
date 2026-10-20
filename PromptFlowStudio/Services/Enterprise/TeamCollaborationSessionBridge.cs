using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Enterprise;

/// <summary>v16.27.0 팀 역할(오름차순 권한).</summary>
public enum TeamRole : byte { Reviewer = 0, Editor = 1, Owner = 2 }

public enum CutReviewStatus : byte { Pending, ChangesRequested, Approved }

public enum TeamBridgeMode : byte { Offline, Hosting, Connected }

/// <summary>v16.27.0 참여자(프레즌스).</summary>
public sealed class TeamMember
{
    public string UserId { get; init; } = "";
    public string Display { get; set; } = "";
    public TeamRole Role { get; set; }
    public bool Online { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>아바타 색(사용자 ID 해시 기반 · 결정적).</summary>
    public string Color => TeamCollaborationSessionBridge.ColorOf(UserId);
    public string Initials => Display.Length == 0 ? "?" : Display.Length == 1 ? Display : Display[..2];
}

/// <summary>v16.27.0 컷 리뷰 코멘트(타임코드 · 해결 여부).</summary>
public sealed class ReviewComment
{
    public string Id { get; init; } = "";
    public string CutId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Display { get; init; } = "";
    public string Text { get; init; } = "";
    public double Timecode { get; init; }
    public bool IsChangeRequest { get; init; }
    public DateTimeOffset At { get; init; }
    public bool Resolved { get; set; }
}

/// <summary>v16.27.0 컷 리뷰 상태.</summary>
public sealed class CutReview
{
    public string CutId { get; init; } = "";
    public CutReviewStatus Status { get; set; }
    public HashSet<string> Approvers { get; } = new(StringComparer.Ordinal);
    public List<ReviewComment> Comments { get; } = new();
    public int Revision { get; set; }
}

/// <summary>
/// v16.27.0 와이어 봉투 — t: join|leave|comment|approve|changes|edit|resolve|policy|role.
/// 호스트가 발신자(User)·역할을 인증 정보로 덮어쓰고 Seq 를 부여한 뒤 방송(클라이언트 자칭 권한 무시).
/// </summary>
public sealed record TeamEnvelope(string Type, string Tenant, string User, string? Display = null, string? CutId = null, string? Text = null,
    double Timecode = 0, string? Ref = null, int N = 0, long Seq = 0);

public readonly record struct TeamApplyResult(bool Ok, string? Reason);

/// <summary>초당 토큰 버킷(무할당 값형 · 연결별 레이트 리밋).</summary>
public struct TokenBucket
{
    private double _tokens;
    private long _lastMs;
    public TokenBucket(double burst) { _tokens = burst; _lastMs = 0; }

    public bool TryTake(long nowMs, double ratePerSec, double burst)
    {
        if (_lastMs != 0) _tokens = Math.Min(burst, _tokens + (nowMs - _lastMs) * ratePerSec / 1000.0);
        _lastMs = nowMs;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

/// <summary>
/// [PromptFlowStudio] v16.27.0 👥 실시간 다자간 팀 협업 &amp; RBAC 브리지 — 멀티 테넌트 룸 · Owner/Editor/Reviewer · 컷별 타임코드 리뷰 코멘트 · 승인 워크플로우.
///   ▸ 전송: 순수 BCL WebSocket(SignalR 대체 · 외부 NuGet 0) — 호스트 <c>ws://localhost:{port}/team/</c>(HttpListener) / 참가 <see cref="ClientWebSocket"/>.
///     원격 팀원은 리버스 프록시(TLS) 뒤에서 wss 로 접속. 호스트 권위(host-authoritative): 클라이언트 작업은 호스트가 RBAC 검증 후 Seq 부여 · 같은 테넌트에만 방송.
///   ▸ 인증: 초대 토큰 = base64url(tenant|user|role|exp) + "." + HMAC-SHA256(<see cref="SecureSecretVault"/> 'TEAM_INVITE' 시크릿) · FixedTimeEquals · 만료 검사.
///     역할은 토큰에서만 결정(클라이언트 자칭 무시).
///   ▸ 승인: 승인자 수 ≥ <c>RequiredApprovals</c> 이면 Approved · 수정 요청은 승인 초기화 · 컷 편집(Editor/Owner)은 기존 승인 무효화(Revision+1).
///   ▸ 상한: 테넌트 64 · 테넌트당 멤버 64 · 컷 512 · 컷당 코멘트 500 · 메시지 16KB · 연결당 20msg/s(버스트 40) · 이벤트 로그 2000(합류자 재생용).
/// </summary>
public sealed class TeamCollaborationSessionBridge : IDisposable
{
    public const int MaxTenants = 64;
    public const int MaxMembers = 64;
    public const int MaxCuts = 512;
    public const int MaxCommentsPerCut = 500;
    public const int MaxCommentLength = 2000;
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxLog = 2000;
    public const double RatePerSec = 20, RateBurst = 40;

    private sealed class Room
    {
        public readonly Dictionary<string, TeamMember> Members = new(StringComparer.Ordinal);
        public readonly Dictionary<string, CutReview> Cuts = new(StringComparer.Ordinal);
        public readonly List<TeamEnvelope> Log = new();
        public int RequiredApprovals = 1;
        public long Seq;
    }

    private sealed class Conn
    {
        public required WebSocket Ws;
        public required string Tenant;
        public required string User;
        public TokenBucket Bucket = new(RateBurst);
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    private readonly Func<byte[]> _secret;
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Conn> _conns = new();
    private HttpListener? _listener;
    private ClientWebSocket? _client;
    private CancellationTokenSource? _netCts;
    private long _clientSeq, _denied, _rateLimited, _received;
    private bool _disposed;

    public TeamCollaborationSessionBridge(SecureSecretVault vault) : this(() => vault.ResolveOrCreate("TEAM_INVITE")) { }

    public TeamCollaborationSessionBridge(Func<byte[]> secret)
    {
        _secret = secret;
        Apply(new TeamEnvelope("join", Tenant, UserId, Display, N: (int)Role));   // 오프라인 기본 신원(Owner)
    }

    public event Action? OnChange;

    public TeamBridgeMode Mode { get; private set; }
    public string Tenant { get; private set; } = "default";
    public string UserId { get; private set; } = "owner";
    public string Display { get; private set; } = "Owner";
    public TeamRole Role { get; private set; } = TeamRole.Owner;
    public string? Endpoint { get; private set; }
    public string? LastAction { get; private set; }
    public string? LastError { get; private set; }
    public long DeniedCount => Interlocked.Read(ref _denied);
    public long RateLimitedCount => Interlocked.Read(ref _rateLimited);
    public long ReceivedCount => Interlocked.Read(ref _received);
    public int ConnectionCount => _conns.Count;

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    /// <summary>RBAC 권한 매트릭스.</summary>
    public static bool Can(TeamRole role, string type) => type switch
    {
        "comment" or "resolve" => true,
        "approve" or "changes" => role is TeamRole.Owner or TeamRole.Reviewer,
        "edit" => role >= TeamRole.Editor,
        "policy" or "role" => role == TeamRole.Owner,
        "join" or "leave" => true,
        _ => false,
    };

    public static string RoleLabel(TeamRole r) => r switch { TeamRole.Owner => "Owner", TeamRole.Editor => "Editor", _ => "Reviewer" };

    public static string ColorOf(string id)
    {
        uint h = 2166136261;
        foreach (var ch in id) h = (h ^ ch) * 16777619;
        return (h % 8) switch { 0 => "#0ea5e9", 1 => "#8b5cf6", 2 => "#f43f5e", 3 => "#22c55e", 4 => "#f59e0b", 5 => "#14b8a6", 6 => "#ec4899", _ => "#6366f1" };
    }

    /// <summary>ID 형식 — 영문/숫자/_-.@ · 1~64자.</summary>
    public static bool IsSafeId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        foreach (var ch in s) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '@')) return false;
        return true;
    }

    /// <summary>코멘트 정화 — 제어문자 제거(줄바꿈 유지) · 길이 상한 · 공백만이면 빈 문자열.</summary>
    public static string SanitizeText(string? s, int max = MaxCommentLength)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(ch) && ch != '\n') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>초대 토큰 발급 — <c>base64url(tenant|user|role|expUnix|display).base64url(HMAC)</c>.</summary>
    public static string IssueToken(byte[] secret, string tenant, string user, TeamRole role, DateTimeOffset expires, string? display = null)
    {
        if (!IsSafeId(tenant) || !IsSafeId(user)) throw new ArgumentException("tenant/user 형식 오류");
        var disp = SanitizeText(display ?? user, 32).Replace("|", "", StringComparison.Ordinal);
        var payload = Encoding.UTF8.GetBytes(tenant + "|" + user + "|" + ((int)role).ToString(CultureInfo.InvariantCulture) + "|"
            + expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "|" + disp);
        var mac = HMACSHA256.HashData(secret, payload);
        return B64Url(payload) + "." + B64Url(mac);
    }

    /// <summary>토큰 검증(FixedTimeEquals · 만료 · 형식). 성공 시 테넌트/사용자/역할/표시명.</summary>
    public static bool TryValidateToken(byte[] secret, string? token, DateTimeOffset now, out string tenant, out string user, out TeamRole role, out string display)
    {
        tenant = user = display = ""; role = TeamRole.Reviewer;
        if (string.IsNullOrEmpty(token) || token.Length > 1024) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;
        var payload = FromB64Url(token[..dot]); var mac = FromB64Url(token[(dot + 1)..]);
        if (payload is null || mac is null || mac.Length != 32) return false;
        var expect = HMACSHA256.HashData(secret, payload);
        if (!CryptographicOperations.FixedTimeEquals(expect, mac)) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 5 || !IsSafeId(parts[0]) || !IsSafeId(parts[1])
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r is < 0 or > 2
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var exp) || now.ToUnixTimeSeconds() > exp) return false;
        tenant = parts[0]; user = parts[1]; role = (TeamRole)r; display = parts[4].Length == 0 ? parts[1] : parts[4];
        return true;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(t); } catch (FormatException) { return null; }
    }

    /// <summary>봉투 직렬화(Utf8JsonWriter).</summary>
    public static string Serialize(TeamEnvelope e)
    {
        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", e.Type); w.WriteString("tenant", e.Tenant); w.WriteString("user", e.User);
            if (e.Display is not null) w.WriteString("display", e.Display);
            if (e.CutId is not null) w.WriteString("cut", e.CutId);
            if (e.Text is not null) w.WriteString("text", e.Text);
            if (e.Timecode != 0) w.WriteNumber("tc", e.Timecode);
            if (e.Ref is not null) w.WriteString("ref", e.Ref);
            if (e.N != 0) w.WriteNumber("n", e.N);
            if (e.Seq != 0) w.WriteNumber("seq", e.Seq);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>봉투 파싱 — 스키마 즉시 검증(필수 t/tenant/user · 문자열 타입 · ID 형식 · 크기). 불량 null.</summary>
    public static TeamEnvelope? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxMessageBytes) return null;
        try
        {
            var r = new Utf8JsonReader(utf8);
            using var doc = JsonDocument.ParseValue(ref r);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            var t = Str(o, "t"); var tenant = Str(o, "tenant"); var user = Str(o, "user");
            if (t is null || t.Length > 16 || !IsSafeId(tenant) || !IsSafeId(user)) return null;
            var cut = Str(o, "cut");
            if (cut is not null && !IsSafeId(cut)) return null;
            var refId = Str(o, "ref");
            if (refId is not null && !IsSafeId(refId)) return null;
            double tc = 0; int n = 0; long seq = 0;
            if (o.TryGetProperty("tc", out var tce) && (tce.ValueKind != JsonValueKind.Number || !tce.TryGetDouble(out tc) || !double.IsFinite(tc))) return null;
            if (o.TryGetProperty("n", out var ne) && (ne.ValueKind != JsonValueKind.Number || !ne.TryGetInt32(out n))) return null;
            if (o.TryGetProperty("seq", out var se) && (se.ValueKind != JsonValueKind.Number || !se.TryGetInt64(out seq))) return null;
            return new TeamEnvelope(t, tenant!, user!, Str(o, "display"), cut, Str(o, "text"), tc, refId, n, seq);
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    // ══════════════════════ 상태 리듀서(로컬·호스트 공통) ══════════════════════

    /// <summary>
    /// 봉투 1건 적용 — <paramref name="trusted"/>=false 면 발신자 역할로 RBAC 검증(호스트/로컬), true 면 호스트가 검증한 방송(클라이언트 측) 그대로 반영.
    /// </summary>
    public TeamApplyResult Apply(TeamEnvelope e, bool trusted = false)
    {
        TeamApplyResult res;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(e.Tenant, out var room))
            {
                if (_rooms.Count >= MaxTenants) return Deny("테넌트 상한");
                room = new Room();
                _rooms.Add(e.Tenant, room);
            }
            res = ApplyLocked(room, e, trusted);
            if (res.Ok && e.Type != "leave")
            {
                room.Log.Add(e);
                if (room.Log.Count > MaxLog) room.Log.RemoveRange(0, room.Log.Count - MaxLog);
            }
        }
        if (!res.Ok) Interlocked.Increment(ref _denied);
        Raise();
        return res;
    }

    private static TeamApplyResult Deny(string why) => new(false, why);

    private static TeamApplyResult ApplyLocked(Room room, TeamEnvelope e, bool trusted)
    {
        var now = DateTimeOffset.Now;
        if (e.Type == "join")
        {
            if (!room.Members.TryGetValue(e.User, out var m))
            {
                if (room.Members.Count >= MaxMembers) return Deny("멤버 상한");
                m = new TeamMember { UserId = e.User };
                room.Members.Add(e.User, m);
            }
            m.Display = SanitizeText(e.Display ?? e.User, 32);
            m.Role = (TeamRole)Math.Clamp(e.N, 0, 2);
            m.Online = true; m.LastSeen = now;
            return new(true, null);
        }
        if (e.Type == "leave")
        {
            if (room.Members.TryGetValue(e.User, out var lm)) { lm.Online = false; lm.LastSeen = now; }
            return new(true, null);
        }
        if (!room.Members.TryGetValue(e.User, out var sender)) return Deny("참여자가 아닙니다.");
        sender.LastSeen = now;
        if (!trusted && !Can(sender.Role, e.Type)) return Deny(RoleLabel(sender.Role) + " 권한으로 '" + e.Type + "' 불가");

        if (e.Type == "policy") { room.RequiredApprovals = Math.Clamp(e.N, 1, 10); ReevaluateAll(room); return new(true, null); }
        if (e.Type == "role")
        {
            if (e.Ref is null || !room.Members.TryGetValue(e.Ref, out var target)) return Deny("대상 멤버 없음");
            if (target.UserId == sender.UserId && !trusted) return Deny("자기 역할 변경 불가");
            target.Role = (TeamRole)Math.Clamp(e.N, 0, 2);
            return new(true, null);
        }
        if (e.CutId is null) return Deny("컷 ID 누락");
        if (!room.Cuts.TryGetValue(e.CutId, out var cut))
        {
            if (room.Cuts.Count >= MaxCuts) return Deny("컷 상한");
            cut = new CutReview { CutId = e.CutId };
            room.Cuts.Add(e.CutId, cut);
        }
        switch (e.Type)
        {
            case "comment":
            case "changes":
            {
                var text = SanitizeText(e.Text);
                if (text.Length == 0) return Deny("빈 코멘트");
                if (cut.Comments.Count >= MaxCommentsPerCut) return Deny("코멘트 상한");
                var id = e.Ref ?? ("c" + (cut.Comments.Count + 1).ToString(CultureInfo.InvariantCulture) + "_" + e.User);
                foreach (var c in cut.Comments) if (c.Id == id) return Deny("중복 코멘트");
                var changes = e.Type == "changes";
                cut.Comments.Add(new ReviewComment { Id = id, CutId = cut.CutId, UserId = e.User, Display = sender.Display, Text = text, Timecode = Math.Max(0, e.Timecode), IsChangeRequest = changes, At = now });
                if (changes) { cut.Approvers.Clear(); cut.Status = CutReviewStatus.ChangesRequested; }
                return new(true, null);
            }
            case "resolve":
            {
                foreach (var c in cut.Comments)
                {
                    if (c.Id != e.Ref) continue;
                    if (!trusted && c.UserId != e.User && sender.Role < TeamRole.Editor) return Deny("작성자 또는 Editor 이상만 해결 가능");
                    c.Resolved = true;
                    Reevaluate(room, cut);
                    return new(true, null);
                }
                return Deny("코멘트 없음");
            }
            case "approve":
                if (OpenChangeRequests(cut) > 0) return Deny("미해결 수정 요청이 있습니다.");
                cut.Approvers.Add(e.User);
                Reevaluate(room, cut);
                return new(true, null);
            case "edit":
                cut.Revision++;
                cut.Approvers.Clear();
                cut.Status = CutReviewStatus.Pending;
                return new(true, null);
            default:
                return Deny("알 수 없는 작업");
        }
    }

    private static int OpenChangeRequests(CutReview cut)
    {
        var n = 0;
        foreach (var c in cut.Comments) if (c.IsChangeRequest && !c.Resolved) n++;
        return n;
    }

    private static void Reevaluate(Room room, CutReview cut)
    {
        if (OpenChangeRequests(cut) > 0) { cut.Status = CutReviewStatus.ChangesRequested; return; }
        cut.Status = cut.Approvers.Count >= room.RequiredApprovals ? CutReviewStatus.Approved : CutReviewStatus.Pending;
    }

    private static void ReevaluateAll(Room room) { foreach (var c in room.Cuts.Values) Reevaluate(room, c); }

    // ══════════════════════ 조회 ══════════════════════

    public IReadOnlyList<TeamMember> Members(string? tenant = null)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(tenant ?? Tenant, out var r)) return Array.Empty<TeamMember>();
            var list = new List<TeamMember>(r.Members.Values);
            list.Sort(static (a, b) => b.Online != a.Online ? (b.Online ? 1 : -1) : b.Role.CompareTo(a.Role));
            return list;
        }
    }

    public CutReview? CutOf(string cutId, string? tenant = null)
    {
        lock (_gate) return _rooms.TryGetValue(tenant ?? Tenant, out var r) && r.Cuts.TryGetValue(cutId, out var c) ? c : null;
    }

    public CutReviewStatus StatusOf(string cutId, string? tenant = null) => CutOf(cutId, tenant)?.Status ?? CutReviewStatus.Pending;

    public int RequiredApprovals(string? tenant = null) { lock (_gate) return _rooms.TryGetValue(tenant ?? Tenant, out var r) ? r.RequiredApprovals : 1; }

    /// <summary>컷 목록이 모두 승인됐는지(빈 목록 false).</summary>
    public bool AllApproved(IReadOnlyList<string> cutIds, string? tenant = null)
    {
        if (cutIds.Count == 0) return false;
        foreach (var id in cutIds) if (StatusOf(id, tenant) != CutReviewStatus.Approved) return false;
        return true;
    }

    public (int Approved, int Changes, int Pending, int OpenComments) Summary(IReadOnlyList<string> cutIds, string? tenant = null)
    {
        int a = 0, c = 0, p = 0, open = 0;
        lock (_gate)
        {
            _rooms.TryGetValue(tenant ?? Tenant, out var r);
            foreach (var id in cutIds)
            {
                CutReview? cut = null;
                if (r is not null) r.Cuts.TryGetValue(id, out cut);
                var st = cut?.Status ?? CutReviewStatus.Pending;
                if (st == CutReviewStatus.Approved) a++; else if (st == CutReviewStatus.ChangesRequested) c++; else p++;
                if (cut is not null) foreach (var cm in cut.Comments) if (!cm.Resolved) open++;
            }
        }
        return (a, c, p, open);
    }

    // ══════════════════════ 로컬 사용자 작업 ══════════════════════

    /// <summary>로컬 신원 설정(오프라인/호스트 모드 · 접속 중이면 토큰 신원 유지).</summary>
    public bool SetIdentity(string tenant, string user, string display, TeamRole role)
    {
        if (Mode == TeamBridgeMode.Connected || !IsSafeId(tenant) || !IsSafeId(user)) return false;
        Tenant = tenant; UserId = user; Display = SanitizeText(display, 32); Role = role;
        Apply(new TeamEnvelope("join", tenant, user, Display, N: (int)role));
        return true;
    }

    /// <summary>로컬 작업 — 접속 모드면 호스트로 송신(호스트 검증 후 방송 수신 시 반영), 아니면 즉시 적용 + 접속자 방송.</summary>
    public async Task<TeamApplyResult> ActAsync(string type, string? cutId = null, string? text = null, double timecode = 0, string? refId = null, int n = 0)
    {
        var e = new TeamEnvelope(type, Tenant, UserId, Display, cutId, text, timecode, refId, n);
        if (Mode == TeamBridgeMode.Connected)
        {
            if (!Can(Role, type)) { Interlocked.Increment(ref _denied); return Deny(RoleLabel(Role) + " 권한 없음"); }
            var c = _client;
            if (c is null || c.State != WebSocketState.Open) return Deny("호스트 연결 끊김");
            try
            {
                await c.SendAsync(Encoding.UTF8.GetBytes(Serialize(e)), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                return new(true, null);
            }
            catch (Exception ex) { Program.Log("Team.Send", ex); return Deny("송신 실패"); }
        }
        if (type == "comment" || type == "changes") e = e with { Ref = NewCommentId() };
        var r = HostApplyAndStamp(e);
        if (r.Ok) LastAction = ActionLabel(type);
        return r;
    }

    private static string NewCommentId() => "c" + Guid.NewGuid().ToString("N")[..10];

    private static string ActionLabel(string t) => t switch
    {
        "comment" => "💬 코멘트", "changes" => "✏️ 수정 요청", "approve" => "✅ 승인", "edit" => "🛠 컷 수정(승인 초기화)",
        "resolve" => "☑ 코멘트 해결", "policy" => "⚙ 승인 정책 변경", "role" => "👤 역할 변경", _ => t,
    };

    /// <summary>호스트 측 적용: Seq 부여 → 적용 → 같은 테넌트 접속자에게 방송.</summary>
    private TeamApplyResult HostApplyAndStamp(TeamEnvelope e)
    {
        long seq;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(e.Tenant, out var room)) { room = new Room(); if (_rooms.Count < MaxTenants) _rooms.Add(e.Tenant, room); }
            seq = ++room.Seq;
        }
        var stamped = e with { Seq = seq };
        var r = Apply(stamped);
        if (r.Ok) _ = BroadcastAsync(stamped);
        return r;
    }

    // ══════════════════════ 네트워크: 호스트 ══════════════════════

    /// <summary>호스트 시작(<c>http://localhost:{port}/team/</c> · 이미 실행 중이면 true).</summary>
    public bool StartHost(int port = 8790)
    {
        if (Mode == TeamBridgeMode.Hosting) return true;
        if (Mode == TeamBridgeMode.Connected || _disposed) return false;
        var prefix = "http://localhost:" + Math.Clamp(port, 1024, 65535).ToString(CultureInfo.InvariantCulture) + "/team/";
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            l.Start();
            _listener = l;
            var cts = new CancellationTokenSource();
            _netCts = cts;
            Endpoint = "ws" + prefix[4..];
            Mode = TeamBridgeMode.Hosting;
            LastError = null;
            LastAction = "🛰 협업 호스트 시작";
            Apply(new TeamEnvelope("join", Tenant, UserId, Display, N: (int)Role));
            _ = Task.Run(() => AcceptLoopAsync(l, cts.Token));
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("Team.Host", ex);
            LastError = ex.Message;
            Raise();
            return false;
        }
    }

    private async Task AcceptLoopAsync(HttpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Program.Log("Team.Accept", ex); break; }
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket? ws = null;
        var id = Guid.NewGuid();
        Conn? conn = null;
        try
        {
            if (!ctx.Request.IsWebSocketRequest || _conns.Count >= MaxTenants * MaxMembers)
            {
                ctx.Response.StatusCode = ctx.Request.IsWebSocketRequest ? 503 : 426;
                ctx.Response.Close();
                return;
            }
            ws = (await ctx.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(20)).ConfigureAwait(false)).WebSocket;
            var buf = new byte[MaxMessageBytes];

            // 1) 첫 메시지 = join + 토큰(5초 내) — 신원/역할은 토큰에서만.
            using (var jd = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                jd.CancelAfter(TimeSpan.FromSeconds(5));
                var len = await ReceiveAsync(ws, buf, jd.Token).ConfigureAwait(false);
                var join = len > 0 ? Parse(buf.AsSpan(0, len)) : null;
                if (join is null || join.Type != "join"
                    || !TryValidateToken(_secret(), join.Text, DateTimeOffset.UtcNow, out var tenant, out var user, out var role, out var display))
                {
                    Interlocked.Increment(ref _denied);
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                conn = new Conn { Ws = ws, Tenant = tenant, User = user };
                _conns[id] = conn;
                TeamEnvelope[] replay;
                lock (_gate) replay = _rooms.TryGetValue(tenant, out var room) ? room.Log.ToArray() : Array.Empty<TeamEnvelope>();
                foreach (var e in replay) await SendAsync(conn, Serialize(e), ct).ConfigureAwait(false);
                HostApplyAndStamp(new TeamEnvelope("join", tenant, user, display, N: (int)role));
            }

            // 2) 작업 루프 — 레이트 리밋 · 발신자/테넌트 덮어쓰기 · RBAC 는 Apply 에서.
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var len = await ReceiveAsync(ws, buf, ct).ConfigureAwait(false);
                if (len < 0) break;
                if (len == 0) continue;
                Interlocked.Increment(ref _received);
                if (!conn.Bucket.TryTake(Environment.TickCount64, RatePerSec, RateBurst)) { Interlocked.Increment(ref _rateLimited); continue; }
                var e = Parse(buf.AsSpan(0, len));
                if (e is null || e.Type is "join" or "leave") { Interlocked.Increment(ref _denied); continue; }
                if (e.Type is "comment" or "changes") e = e with { Ref = NewCommentId() };
                HostApplyAndStamp(e with { Tenant = conn.Tenant, User = conn.User, Display = null });
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Program.Log("Team.Conn", ex); }
        finally
        {
            _conns.TryRemove(id, out _);
            if (conn is not null)
            {
                HostApplyAndStamp(new TeamEnvelope("leave", conn.Tenant, conn.User));
                conn.SendLock.Dispose();
            }
            try { ws?.Dispose(); } catch { }
        }
    }

    /// <summary>메시지 1건 수신(단편 결합 · 상한 초과/종료 시 -1).</summary>
    private static async Task<int> ReceiveAsync(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (true)
        {
            if (off >= buf.Length) return -1;
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, off, buf.Length - off), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return -1;
            off += r.Count;
            if (r.EndOfMessage) return r.MessageType == WebSocketMessageType.Text ? off : 0;
        }
    }

    private static async Task SendAsync(Conn c, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await c.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try { if (c.Ws.State == WebSocketState.Open) await c.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { c.SendLock.Release(); }
    }

    private async Task BroadcastAsync(TeamEnvelope e)
    {
        if (_conns.IsEmpty) return;
        var json = Serialize(e);
        foreach (var kv in _conns)
        {
            if (kv.Value.Tenant != e.Tenant) continue;
            try { await SendAsync(kv.Value, json, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
            catch (Exception ex) { Program.Log("Team.Broadcast", ex); }
        }
    }

    /// <summary>호스트(Owner)가 초대 토큰 발급 — 비-Owner/비호스트는 null.</summary>
    public string? CreateInvite(string user, string display, TeamRole role, TimeSpan ttl)
    {
        if (Role != TeamRole.Owner || Mode == TeamBridgeMode.Connected || !IsSafeId(user)) return null;
        return IssueToken(_secret(), Tenant, user, role, DateTimeOffset.UtcNow + ttl, display);
    }

    // ══════════════════════ 네트워크: 참가 ══════════════════════

    /// <summary>호스트 접속(ws/wss · 초대 토큰) — 신원은 토큰 페이로드에서 표시용으로만 해석(검증은 호스트).</summary>
    public async Task<bool> ConnectAsync(string url, string token, CancellationToken ct = default)
    {
        if (Mode != TeamBridgeMode.Offline || _disposed) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "wss" && !(u.Scheme == "ws" && u.IsLoopback))) { LastError = "wss://(또는 로컬 ws://) 주소만 허용"; Raise(); return false; }
        if (!TryReadTokenClaims(token, out var tenant, out var user, out var role, out var display)) { LastError = "토큰 형식 오류"; Raise(); return false; }
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(u, d.Token).ConfigureAwait(false);
            await ws.SendAsync(Encoding.UTF8.GetBytes(Serialize(new TeamEnvelope("join", tenant, user, display, Text: token))), WebSocketMessageType.Text, true, d.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            LastError = "접속 실패: " + ex.Message;
            Raise();
            return false;
        }
        Tenant = tenant; UserId = user; Role = role; Display = display;
        _client = ws;
        var cts = new CancellationTokenSource();
        _netCts = cts;
        Endpoint = url;
        Mode = TeamBridgeMode.Connected;
        Interlocked.Exchange(ref _clientSeq, 0);
        lock (_gate) _rooms.Remove(tenant);   // 호스트 로그 재생으로 재구성
        LastAction = "🔗 팀 세션 접속";
        _ = Task.Run(() => ClientLoopAsync(ws, cts.Token));
        Raise();
        return true;
    }

    /// <summary>토큰 페이로드 디코드(서명 미검증 · 표시용).</summary>
    public static bool TryReadTokenClaims(string? token, out string tenant, out string user, out TeamRole role, out string display)
    {
        tenant = user = display = ""; role = TeamRole.Reviewer;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;
        var p = FromB64Url(token[..dot]);
        if (p is null) return false;
        var parts = Encoding.UTF8.GetString(p).Split('|');
        if (parts.Length != 5 || !IsSafeId(parts[0]) || !IsSafeId(parts[1]) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r is < 0 or > 2) return false;
        tenant = parts[0]; user = parts[1]; role = (TeamRole)r; display = parts[4].Length == 0 ? parts[1] : parts[4];
        return true;
    }

    private async Task ClientLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[MaxMessageBytes];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var len = await ReceiveAsync(ws, buf, ct).ConfigureAwait(false);
                if (len < 0) break;
                if (len == 0) continue;
                Interlocked.Increment(ref _received);
                var e = Parse(buf.AsSpan(0, len));
                if (e is null || e.Tenant != Tenant) continue;
                if (e.Seq > 0 && e.Seq <= Interlocked.Read(ref _clientSeq)) continue;   // 중복/역순 무시
                if (e.Seq > 0) Interlocked.Exchange(ref _clientSeq, e.Seq);
                if (e.Type == "role" && e.Ref == UserId) Role = (TeamRole)Math.Clamp(e.N, 0, 2);
                Apply(e, trusted: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { LastError = "연결 끊김: " + ex.Message; }
        catch (Exception ex) { Program.Log("Team.Client", ex); LastError = ex.Message; }
        finally
        {
            if (ReferenceEquals(_client, ws))
            {
                Mode = TeamBridgeMode.Offline;
                _client = null;
                LastAction = "🔌 팀 세션 종료";
                Raise();
            }
        }
    }

    /// <summary>호스트 중지/접속 해제(멱등).</summary>
    public void Disconnect()
    {
        var cts = Interlocked.Exchange(ref _netCts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        var l = Interlocked.Exchange(ref _listener, null);
        try { l?.Stop(); l?.Close(); } catch (Exception ex) { Program.Log("Team.Stop", ex); }
        foreach (var kv in _conns) { try { kv.Value.Ws.Abort(); } catch { } }
        var c = Interlocked.Exchange(ref _client, null);
        try { c?.Abort(); c?.Dispose(); } catch { }
        Mode = TeamBridgeMode.Offline;
        Endpoint = null;
        Raise();
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Team.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        OnChange = null;
    }
}
