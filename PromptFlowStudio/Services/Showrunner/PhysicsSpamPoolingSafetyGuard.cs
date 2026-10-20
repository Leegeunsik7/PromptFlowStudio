namespace PromptFlowStudio.Services.Showrunner;

public enum SpawnVerdict : byte { Admitted, RateLimited, UserCooldown, QueueFull, Unpaid, Unknown }

/// <summary>
/// [PromptFlow Studio v17.67.0] 물리 객체 스팸 폭주 방어 &amp; 프레임 드롭 가드(무할당).
///   ▸ 수신 링(1024): 게이트웨이 스레드 → 락 1 회 복사 · 포화 시 신규 드롭 · 틱당 소비 상한 <see cref="DrainPerFrame"/>(커맨드 1만 개 폭주도 프레임 비용 상한 고정).
///   ▸ 글로벌 쿨다운: 객체 토큰 버킷(<see cref="RatePerSec"/> /s · 버스트 <see cref="Burst"/>) — 컨페티는 비용 4.
///   ▸ 사용자 쿨다운: 512 슬롯 개방 주소 해시(8 탐침 · 만석 시 가장 오래된 슬롯 교체) · 1.5 s.
///   ▸ 프레임 가드: 물리 레이어 비용이 <see cref="LayerBudgetMs"/> 를 넘으면 버킷 유입률을 절반으로(회복 2 s) — 렌더 예산 보호.
///   ▸ 풀 상한 500: 초과 스폰은 스포너가 카메라 밖 최고령 → 최고령 순으로 재활용(<see cref="Recycled"/>).
/// </summary>
public sealed class PhysicsSpamPoolingSafetyGuard
{
    public const int QueueCap = 1024, DrainPerFrame = 48, UserSlots = 512, Probe = 8, TextCap = 24, UserCap = 24;
    public const float RatePerSec = 40f, Burst = 40f, UserCooldownMs = 1500f, LayerBudgetMs = 3f;

    public struct Command { public int UserHash, Amount, UserLen, TextLen; public long AtMs; }

    private readonly object _q = new();
    private readonly Command[] _cmd = new Command[QueueCap];
    private readonly char[] _chars = new char[QueueCap * (TextCap + UserCap)];
    private readonly int[] _uKey = new int[UserSlots];
    private readonly long[] _uAt = new long[UserSlots];
    private int _head, _count;
    private float _tokens = Burst, _rateScale = 1f;
    private double _lastRefill = double.NaN, _calmUntil;

    public bool Enabled { get; set; } = true;
    public long Offered { get; private set; }
    public long QueueDropped { get; private set; }
    public long Admitted { get; private set; }
    public long RateLimited { get; private set; }
    public long UserLimited { get; private set; }
    public long Unpaid { get; private set; }
    public long UnknownCommands { get; private set; }
    public long Recycled { get; set; }
    public long Throttles { get; private set; }
    public float Tokens => _tokens;
    public float RateScale => _rateScale;
    public int Pending { get { lock (_q) return _count; } }

    public void ResetMetrics() { Offered = QueueDropped = Admitted = RateLimited = UserLimited = Unpaid = UnknownCommands = Recycled = Throttles = 0; }
    public void Reset() { lock (_q) { _head = _count = 0; } Array.Clear(_uKey); Array.Clear(_uAt); _tokens = Burst; _rateScale = 1f; _lastRefill = double.NaN; ResetMetrics(); }

    public static int Hash(ReadOnlySpan<char> s) { var h = unchecked((int)2166136261); foreach (var c in s) h = unchecked((h ^ char.ToLowerInvariant(c)) * 16777619); return h == 0 ? 1 : h; }

    // ══════════════════════ 수신(임의 스레드 · 무할당) ══════════════════════
    public bool Offer(ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, long nowMs)
    {
        lock (_q)
        {
            Offered++;
            if (_count >= QueueCap) { QueueDropped++; return false; }
            var k = (_head + _count) % QueueCap;
            ref var c = ref _cmd[k];
            c.UserHash = Hash(user); c.Amount = amount; c.AtMs = nowMs;
            c.UserLen = Math.Min(user.Length, UserCap); c.TextLen = Math.Min(text.Length, TextCap);
            var o = k * (TextCap + UserCap);
            user[..c.UserLen].CopyTo(_chars.AsSpan(o)); text[..c.TextLen].CopyTo(_chars.AsSpan(o + UserCap));
            _count++;
            return true;
        }
    }

    /// <summary>다음 커맨드 꺼내기(틱 스레드) — 사용자 · 본문 스팬은 <paramref name="user"/>/<paramref name="text"/> 버퍼에 복사.</summary>
    public bool TryDequeue(Span<char> user, Span<char> text, out Command cmd)
    {
        lock (_q)
        {
            if (_count == 0) { cmd = default; return false; }
            cmd = _cmd[_head];
            var o = _head * (TextCap + UserCap);
            _chars.AsSpan(o, cmd.UserLen).CopyTo(user); _chars.AsSpan(o + UserCap, cmd.TextLen).CopyTo(text);
            _head = (_head + 1) % QueueCap; _count--;
            return true;
        }
    }

    // ══════════════════════ 판정(틱 스레드 · 무할당) ══════════════════════
    public void Refill(double nowMs)
    {
        if (double.IsNaN(_lastRefill)) _lastRefill = nowMs;
        var dt = (float)Math.Max(0, nowMs - _lastRefill) / 1000f;
        _lastRefill = nowMs;
        if (nowMs >= _calmUntil) _rateScale = MathF.Min(1f, _rateScale + dt * 0.5f);
        _tokens = MathF.Min(Burst, _tokens + RatePerSec * _rateScale * dt);
    }

    /// <summary>비용 <paramref name="cost"/> 객체 스폰 허가 — 가격 · 사용자 쿨다운 · 글로벌 버킷 순.</summary>
    public SpawnVerdict Admit(int userHash, int amount, int price, float cost, long nowMs)
    {
        if (!Enabled) { Admitted++; return SpawnVerdict.Admitted; }
        if (amount < price) { Unpaid++; return SpawnVerdict.Unpaid; }
        var slot = Slot(userHash, out var found);
        if (found && nowMs - _uAt[slot] < UserCooldownMs) { UserLimited++; return SpawnVerdict.UserCooldown; }
        if (_tokens < cost) { RateLimited++; return SpawnVerdict.RateLimited; }
        _tokens -= cost;
        _uKey[slot] = userHash; _uAt[slot] = nowMs;
        Admitted++;
        return SpawnVerdict.Admitted;
    }

    public void Unknown() => UnknownCommands++;

    private int Slot(int h, out bool found)
    {
        var start = (int)((uint)h % UserSlots); var oldest = start; var oldestAt = long.MaxValue;
        for (var i = 0; i < Probe; i++)
        {
            var s = (start + i) % UserSlots;
            if (_uKey[s] == h) { found = true; return s; }
            if (_uKey[s] == 0) { found = false; return s; }
            if (_uAt[s] < oldestAt) { oldestAt = _uAt[s]; oldest = s; }
        }
        found = false;
        return oldest;
    }

    /// <summary>물리 레이어 프레임 비용 관측 — 예산 초과 시 유입 반감(2 s 유지).</summary>
    public void ObserveLayer(double layerMs, double nowMs)
    {
        if (layerMs <= LayerBudgetMs) return;
        _rateScale = MathF.Max(0.125f, _rateScale * 0.5f);
        _calmUntil = nowMs + 2000;
        Throttles++;
    }
}
