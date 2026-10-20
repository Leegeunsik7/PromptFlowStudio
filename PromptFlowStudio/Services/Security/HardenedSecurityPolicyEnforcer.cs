using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PromptFlowStudio.Services.Security;

public enum PolicyEffect { Allow, Deny }

public enum PrincipalState { Trusted, Watched, Quarantined }

public sealed record PolicyRuleView(int Revision, string Principal, string Resource, string Action, PolicyEffect Effect);

public sealed record PrincipalView(string Id, PrincipalState State, int Violations, DateTimeOffset? QuarantinedUntil);

/// <summary>⚠ 명명: <see cref="Integration.TenantPolicyEngine"/> 의 동명 record(PolicyDecision)와 CS0104 회피 위해 접두.</summary>
public sealed record SecurityPolicyDecision(bool Allowed, PolicyEffect Effect, string Reason, string DecisionHash8, double ElapsedMs);

/// <summary>
/// v10.0.2 강화 보안 정책 집행기 — 선언적 규칙(Principal/Resource/Action 글롭 패턴 → Allow/Deny)을 최신 리비전
/// 우선 평가하는 정책 결정 지점(PEP)이다. Deny 판정 누적이 임계를 넘으면 Zero-Trust 격리(Quarantined)로 전환해
/// 정책 결과와 무관하게 즉시 차단하고, 모든 판정은 해시체인 결정 로그로 봉인한다. <see cref="Integration.ZeroTrustGovernanceAuditor"/>
/// (테넌트 거버넌스 감사)·<see cref="Integration.EnterpriseSsoEngine"/>(세션 인증)와 달리 런타임 액션별 세밀 허용/거부를
/// 담당하는 최종 집행 게이트다. 순수 BCL(Regex/SHA-256).
/// </summary>
public sealed class HardenedSecurityPolicyEnforcer : IDisposable
{
    private const int ViolationQuarantineThreshold = 3;
    private static readonly TimeSpan QuarantineWindow = TimeSpan.FromSeconds(30);

    private sealed class Rule { public int Revision; public string Principal = "*"; public string Resource = "*"; public string Action = "*"; public PolicyEffect Effect; }
    private sealed class Principal { public PrincipalState State = PrincipalState.Trusted; public int Violations; public DateTimeOffset? Until; }

    private readonly object _gate = new();
    private readonly List<Rule> _rules = new();
    private readonly Dictionary<string, Principal> _principals = new(StringComparer.Ordinal);
    private byte[] _lastDecisionHash = Array.Empty<byte>();
    private System.Threading.Timer? _timer;
    private readonly Random _rng = new(10002);
    private int _revision;
    private long _evaluations, _denies, _quarantines, _releases;
    private bool _disposed;

    public event Action? OnChange;

    public HardenedSecurityPolicyEnforcer() => SeedDefaultsLocked();

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Evaluations => Interlocked.Read(ref _evaluations);
    public long Denies => Interlocked.Read(ref _denies);
    public long Quarantines => Interlocked.Read(ref _quarantines);
    public long Releases => Interlocked.Read(ref _releases);
    public int RuleRevision { get { lock (_gate) return _revision; } }

    public IReadOnlyList<PolicyRuleView> Rules
    {
        get { lock (_gate) { var l = new List<PolicyRuleView>(_rules.Count); foreach (var r in _rules) l.Add(new PolicyRuleView(r.Revision, r.Principal, r.Resource, r.Action, r.Effect)); return l; } }
    }

    public IReadOnlyList<PrincipalView> Principals
    {
        get { lock (_gate) { var l = new List<PrincipalView>(_principals.Count); foreach (var (id, p) in _principals) l.Add(new PrincipalView(id, p.State, p.Violations, p.Until)); return l; } }
    }

    private void SeedDefaultsLocked()
    {
        _rules.Clear();
        AddRuleLocked("svc-*", "gallery://*", "write", PolicyEffect.Allow);
        AddRuleLocked("svc-*", "config://*", "write", PolicyEffect.Deny);
        AddRuleLocked("mobile-*", "render://*", "submit", PolicyEffect.Allow);
        AddRuleLocked("*", "*", "*", PolicyEffect.Deny);   // 최하위 우선순위 기본 거부(deny-by-default)
        LastAction = "기본 정책 4건 시드";
    }

    private void AddRuleLocked(string principal, string resource, string action, PolicyEffect effect)
        => _rules.Add(new Rule { Revision = ++_revision, Principal = principal, Resource = resource, Action = action, Effect = effect });

    /// <summary>정책 규칙 추가(최신 리비전 부여) — 매칭 시 리비전 내림차순(최신 우선) 평가.</summary>
    public void AddRule(string principal, string resource, string action, PolicyEffect effect)
    {
        lock (_gate) { AddRuleLocked(principal, resource, action, effect); LastAction = $"규칙 추가 r{_revision} {principal}/{resource}/{action}→{effect}"; }
        OnChange?.Invoke();
    }

    /// <summary>principal 이 resource 에 action 을 수행할 권한을 평가·집행(격리 최우선 → 최신 규칙 매칭 → deny-by-default).</summary>
    public SecurityPolicyDecision Evaluate(string principalId, string resource, string action)
    {
        SecurityPolicyDecision d;
        lock (_gate) d = EvaluateLocked(principalId, resource, action);
        OnChange?.Invoke();
        return d;
    }

    private SecurityPolicyDecision EvaluateLocked(string principalId, string resource, string action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _evaluations++;
        var principal = GetOrCreatePrincipalLocked(principalId);
        ReleaseIfExpiredLocked(principal);

        bool allowed; PolicyEffect effect; string reason;
        if (principal.State == PrincipalState.Quarantined)
        {
            allowed = false; effect = PolicyEffect.Deny; reason = "Zero-Trust 격리 상태 — 정책 무관 즉시 차단";
        }
        else
        {
            Rule? match = null;
            for (var i = _rules.Count - 1; i >= 0; i--)   // 리비전 내림차순 = 최신 우선
            {
                var r = _rules[i];
                if (GlobMatch(r.Principal, principalId) && GlobMatch(r.Resource, resource) && GlobMatch(r.Action, action)) { match = r; break; }
            }
            effect = match?.Effect ?? PolicyEffect.Deny;
            allowed = effect == PolicyEffect.Allow;
            reason = match is null ? "매칭 규칙 없음 — deny-by-default" : $"r{match.Revision} 매칭";
        }

        if (!allowed)
        {
            _denies++;
            principal.Violations++;
            if (principal.State != PrincipalState.Quarantined)
            {
                principal.State = principal.Violations >= ViolationQuarantineThreshold ? PrincipalState.Quarantined : PrincipalState.Watched;
                if (principal.State == PrincipalState.Quarantined)
                {
                    principal.Until = DateTimeOffset.UtcNow + QuarantineWindow;
                    _quarantines++;
                    reason += $" · 위반 {principal.Violations}회 격리 전환";
                }
            }
        }

        var payload = Encoding.UTF8.GetBytes($"{principalId}|{resource}|{action}|{effect}|{Hex8(_lastDecisionHash)}");
        var hash = SHA256.HashData(payload);
        _lastDecisionHash = hash;
        sw.Stop();
        LastAction = $"{principalId}→{resource}#{action} = {(allowed ? "ALLOW" : "DENY")} ({reason})";
        return new SecurityPolicyDecision(allowed, effect, reason, Hex8(hash), sw.Elapsed.TotalMilliseconds);
    }

    private Principal GetOrCreatePrincipalLocked(string id)
    {
        if (!_principals.TryGetValue(id, out var p)) { p = new Principal(); _principals[id] = p; }
        return p;
    }

    private void ReleaseIfExpiredLocked(Principal p)
    {
        if (p.State == PrincipalState.Quarantined && p.Until is { } u && DateTimeOffset.UtcNow >= u)
        {
            p.State = PrincipalState.Watched; p.Violations = 0; p.Until = null; _releases++;
        }
    }

    // 단순 글롭: '*' 만 지원(선행 이스케이프 후 .* 치환) — 순수 BCL Regex, 외부 패턴엔진 불필요.
    private static bool GlobMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, rx, RegexOptions.CultureInvariant);
    }

    public void InjectRepeatedViolations(string principalId)
    {
        lock (_gate) { for (var i = 0; i < ViolationQuarantineThreshold; i++) EvaluateLocked(principalId, "config://secrets", "write"); }
        OnChange?.Invoke();
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 900);
            LastAction = "정책 평가 시뮬레이션 기동";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            var principals = new[] { "svc-render", "mobile-01", "guest-anon" };
            var resources = new[] { "gallery://asset", "render://job", "config://secrets" };
            var actions = new[] { "write", "submit", "read" };
            EvaluateLocked(principals[_rng.Next(principals.Length)], resources[_rng.Next(resources.Length)], actions[_rng.Next(actions.Length)]);
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "정책 시뮬레이션 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { _principals.Clear(); _revision = 0; _lastDecisionHash = Array.Empty<byte>(); SeedDefaultsLocked(); LastAction = "정책 집행기 초기화"; }
        OnChange?.Invoke();
    }

    private static string Hex8(byte[] h)
    {
        if (h.Length == 0) return "00000000";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
