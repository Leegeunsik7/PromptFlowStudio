namespace PromptFlowStudio.Services.InfluencerLive;

/// <summary>v17.61.0 채팅 세이프티 판정.</summary>
public enum ChatSafetyVerdict : byte { Clean, Toxic, BotFlood, Spam, Raid, Shadowbanned }

/// <summary>v17.61.0 차단 로그 항목(본문은 고정 슬롯 복사 — 문자열 할당 0).</summary>
public struct ChatBlockEntry
{
    public long AtMs;
    public int UserHash, UserLen, TextLen;
    public ChatSafetyVerdict Verdict;
}

/// <summary>
/// [PromptFlow Studio v17.61.0] 🛡 채팅 도배 공격 &amp; 비속어 섀도우밴 방어 가드(<see cref="Inspect"/> 무할당).
///   ▸ 봇 트래픽: ID 해시별 누출 버킷(용량 10 · 1 건/s 누출) — 초당 10 건 이상 버스트 시 60 s 격리 + 스트라이크.
///   ▸ 도배: 같은 ID 정규화 본문 3 회 연속 → 스팸 · 스트라이크. 레이드: 정규화 본문 해시가 1 s 창에서 타 ID 합산 10 건 이상 → 해당 본문 8 s 봉쇄 · 레이드 상태.
///   ▸ 비속어: 정규화(전각 → 반각 · 소문자 · 리트 0/1/3/4/5/7/@/$/! · 제로폭/공백/구두점 제거 · 반복 문자 축약 · '*' 와일드카드) 후 다국어 블랙리스트 · 플랫폼 금칙어 부분 일치,
///     한글 숫자 끼움(시1발)은 문자만 남긴 제 2 정규형으로 재검사. 허용 예외(다시발견 · 시발점)가 덮는 일치는 무시.
///   ▸ 반복 문자 축약은 금칙어에도 동일 적용(www → w 처럼 축약 후 1 글자가 되는 금칙어는 두지 않는다).
///   ▸ 스트라이크 2 회 → 섀도우밴(본인 화면에만 표시 · 아바타 반응 · 하이라이트 클립 영구 제외).
/// </summary>
public sealed class ChatSpamToxicitySafetyGuard
{
    public const int Slots = 4096, Probe = 16, TextSlots = 512, NormCap = 512, LogCap = 64, LogUser = 24, LogText = 80;
    public const float BucketCapacity = 10f, LeakPerSec = 1f;
    public const int QuarantineMs = 60_000, DupLimit = 3, RaidThreshold = 10, RaidWindowMs = 1000, RaidHoldMs = 8000, StrikesToShadowban = 2;
    private const char Wild = '\u0001';

    /// <summary>다국어 블랙리스트(정규형 비교 — 생성 시 같은 규칙으로 정규화).</summary>
    public static readonly string[] Blacklist =
    {
        // ko
        "씨발", "시발", "씨빨", "씨바", "ㅅㅂ", "ㅆㅂ", "ㅅ발", "병신", "ㅂㅅ", "븅신", "개새끼", "개색기", "개새기", "ㅅㄲ", "좆", "존나", "ㅈㄴ", "닥쳐", "미친놈", "미친년", "지랄", "ㅈㄹ", "썅", "엿먹어", "sibal", "ssibal", "shibal",
        // en
        "fuck", "fck", "shit", "bitch", "asshole", "bastard", "motherfucker", "whore", "slut", "retard", "killyourself",
        // ja · zh · es · de · fr
        "死ね", "しね", "くたばれ", "クソ野郎", "傻逼", "操你", "他妈的", "草泥马", "hijodeputa", "mierda", "pendejo", "cabron", "scheisse", "scheiße", "arschloch", "putain", "connard", "salope",
    };

    /// <summary>플랫폼 금칙어(불법 도박 · 사기 · 성인 · 링크 도배).</summary>
    public static readonly string[] PlatformBanned = { "카지노", "바카라", "토토사이트", "불법도박", "성인방송", "freerobux", "freevbucks", "cryptogiveaway", "doubleyourbitcoin", "http" };

    /// <summary>허용 예외 — 이 정규형 구간이 덮는 블랙리스트 일치는 무시.</summary>
    public static readonly string[] Allow = { "다시발", "시발점", "시발역", "시발택시", "pushit" };

    private struct UserSlot
    {
        public int Hash, LastText, Dups;
        public bool Used, Shadow;
        public byte Strikes;
        public float Level;
        public long LastMs, QuarantineUntil;
    }

    private struct TextSlot { public int Hash, Count; public long WindowMs, BlockUntil; }

    private readonly UserSlot[] _users = new UserSlot[Slots];
    private readonly TextSlot[] _texts = new TextSlot[TextSlots];
    private readonly string[] _terms, _allow;
    private readonly char[] _n1 = new char[NormCap], _n2 = new char[NormCap];
    private readonly ChatBlockEntry[] _log = new ChatBlockEntry[LogCap];
    private readonly char[] _logUser = new char[LogCap * LogUser], _logText = new char[LogCap * LogText];
    private readonly long[] _counts = new long[6];
    private long _logN, _raidUntil = long.MinValue;
    private int _n1Len, _n2Len;

    public ChatSpamToxicitySafetyGuard()
    {
        _terms = new string[Blacklist.Length + PlatformBanned.Length];
        var k = 0;
        foreach (var t in Blacklist) _terms[k++] = NormalizeTerm(t);
        foreach (var t in PlatformBanned) _terms[k++] = NormalizeTerm(t);
        _allow = new string[Allow.Length];
        for (var i = 0; i < Allow.Length; i++) _allow[i] = NormalizeTerm(Allow[i]);
    }

    public bool Enabled { get; set; } = true;
    public long Inspected { get; private set; }
    public long Blocked { get; private set; }
    public long Shadowbanned { get; private set; }
    public long RaidsDetected { get; private set; }
    public double LastInspectUs { get; private set; }
    public double MaxInspectUs { get; private set; }
    public long Count(ChatSafetyVerdict v) => _counts[(int)v];
    public bool RaidActive(long nowMs) => nowMs < _raidUntil;
    public long RaidUntilMs => _raidUntil;
    public int LogCount => (int)Math.Min(_logN, LogCap);
    public long LogTotal => _logN;
    /// <summary>i = 0 이 최신.</summary>
    public ref readonly ChatBlockEntry Log(int i) => ref _log[Slot(i)];
    public ReadOnlySpan<char> LogUserText(int i) { var s = Slot(i); return _logUser.AsSpan(s * LogUser, _log[s].UserLen); }
    public ReadOnlySpan<char> LogBody(int i) { var s = Slot(i); return _logText.AsSpan(s * LogText, _log[s].TextLen); }
    private int Slot(int i) => (int)(((_logN - 1 - i) % LogCap + LogCap) % LogCap);

    public static string VerdictName(ChatSafetyVerdict v) => v switch
    {
        ChatSafetyVerdict.Toxic => "🤬 비속어",
        ChatSafetyVerdict.BotFlood => "🤖 봇 격리",
        ChatSafetyVerdict.Spam => "📋 도배",
        ChatSafetyVerdict.Raid => "🌊 레이드",
        ChatSafetyVerdict.Shadowbanned => "👻 섀도우밴",
        _ => "✅ 통과",
    };

    public void Reset()
    {
        Array.Clear(_users); Array.Clear(_texts); Array.Clear(_counts);
        _logN = 0; _raidUntil = long.MinValue;
        Inspected = Blocked = Shadowbanned = RaidsDetected = 0; MaxInspectUs = 0;
    }

    /// <summary>ID 해시(FNV-1a · 대소문자 무시).</summary>
    public static int HashUser(ReadOnlySpan<char> user)
    {
        var h = 2166136261u;
        foreach (var c in user) { h ^= char.ToLowerInvariant(c); h *= 16777619u; }
        return (int)(h | 1u);
    }

    /// <summary>ID 가 섀도우밴 상태인가(클립 · 반응 영구 제외 판정).</summary>
    public bool IsShadowbanned(int userHash) { var s = Find(userHash, false); return s >= 0 && _users[s].Shadow; }

    /// <summary>본문 유해성만 판정(ID 상태 무관 · 무할당) — 후원자 닉네임 낭독 전 검사 등.</summary>
    public bool IsToxic(ReadOnlySpan<char> text) => Normalize(text) && MatchAny();

    /// <summary>메시지 1 건 판정(무할당).</summary>
    public ChatSafetyVerdict Inspect(ReadOnlySpan<char> user, ReadOnlySpan<char> text, long nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Inspected++;
        var v = Enabled ? Judge(HashUser(user), text, nowMs) : ChatSafetyVerdict.Clean;
        _counts[(int)v]++;
        if (v != ChatSafetyVerdict.Clean) { Blocked++; Record(user, text, nowMs, v); }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastInspectUs = us; if (us > MaxInspectUs && Inspected > 8) MaxInspectUs = us;
        return v;
    }

    private ChatSafetyVerdict Judge(int uh, ReadOnlySpan<char> text, long now)
    {
        var s = Find(uh, true);
        ref var u = ref _users[s];
        if (u.Shadow) return ChatSafetyVerdict.Shadowbanned;
        var toxic = Normalize(text) && MatchAny();
        var th = HashNorm();
        // 누출 버킷
        var dt = u.LastMs == 0 ? 0f : MathF.Max(0f, (now - u.LastMs) / 1000f);
        u.Level = MathF.Max(0f, u.Level - dt * LeakPerSec) + 1f;
        u.LastMs = now;
        if (toxic) { Strike(ref u); return ChatSafetyVerdict.Toxic; }
        if (now < u.QuarantineUntil) return ChatSafetyVerdict.BotFlood;
        if (u.Level > BucketCapacity) { u.QuarantineUntil = now + QuarantineMs; u.Level = 0f; Strike(ref u); return ChatSafetyVerdict.BotFlood; }
        // 연속 도배
        if (th == u.LastText && th != 0) u.Dups++; else { u.Dups = 1; u.LastText = th; }
        if (u.Dups >= DupLimit) { if (u.Dups == DupLimit) Strike(ref u); return ChatSafetyVerdict.Spam; }
        // 레이드(타 ID 합산 동일 본문)
        if (th != 0)
        {
            ref var ts = ref _texts[(th & 0x7FFFFFFF) % TextSlots];
            if (ts.Hash != th) { ts = default; ts.Hash = th; ts.WindowMs = now; }
            if (now < ts.BlockUntil) { _raidUntil = Math.Max(_raidUntil, now + RaidHoldMs); return ChatSafetyVerdict.Raid; }
            if (now - ts.WindowMs > RaidWindowMs) { ts.WindowMs = now; ts.Count = 0; }
            if (++ts.Count >= RaidThreshold)
            {
                ts.BlockUntil = now + RaidHoldMs;
                if (_raidUntil < now) RaidsDetected++;
                _raidUntil = now + RaidHoldMs;
                return ChatSafetyVerdict.Raid;
            }
        }
        return ChatSafetyVerdict.Clean;
    }

    private void Strike(ref UserSlot u)
    {
        if (++u.Strikes >= StrikesToShadowban && !u.Shadow) { u.Shadow = true; Shadowbanned++; }
    }

    private int Find(int hash, bool create)
    {
        var start = (hash & 0x7FFFFFFF) % Slots;
        var victim = start; var oldest = long.MaxValue;
        for (var p = 0; p < Probe; p++)
        {
            var i = (start + p) % Slots;
            ref var u = ref _users[i];
            if (u.Used && u.Hash == hash) return i;
            if (!u.Used) { if (!create) return -1; u = default; u.Used = true; u.Hash = hash; return i; }
            var age = u.Shadow ? long.MaxValue - 1 : u.LastMs;
            if (age < oldest) { oldest = age; victim = i; }
        }
        if (!create) return -1;
        _users[victim] = default; _users[victim].Used = true; _users[victim].Hash = hash;
        return victim;
    }

    private void Record(ReadOnlySpan<char> user, ReadOnlySpan<char> text, long now, ChatSafetyVerdict v)
    {
        var s = (int)(_logN % LogCap);
        ref var e = ref _log[s];
        e.AtMs = now; e.UserHash = HashUser(user); e.Verdict = v;
        e.UserLen = Math.Min(user.Length, LogUser); e.TextLen = Math.Min(text.Length, LogText);
        user[..e.UserLen].CopyTo(_logUser.AsSpan(s * LogUser));
        text[..e.TextLen].CopyTo(_logText.AsSpan(s * LogText));
        _logN++;
    }

    // ══════════════════════ 정규화 · 일치(무할당) ══════════════════════
    private static string NormalizeTerm(string t)
    {
        Span<char> a = stackalloc char[64]; Span<char> b = stackalloc char[64];
        Norm(t, a, b, out var na, out _);
        return new string(a[..na]);
    }

    private bool Normalize(ReadOnlySpan<char> text)
    {
        Norm(text.Length > NormCap ? text[..NormCap] : text, _n1, _n2, out _n1Len, out _n2Len);
        return _n1Len > 0;
    }

    /// <summary>제 1 정규형(리트 치환) · 제 2 정규형(문자만 · 숫자/기호 삭제).</summary>
    private static void Norm(ReadOnlySpan<char> s, Span<char> a, Span<char> b, out int na, out int nb)
    {
        na = nb = 0;
        char pa = '\0', pb = '\0';
        foreach (var c0 in s)
        {
            var c = c0;
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­') continue;
            if (c is >= '！' and <= '～') c = (char)(c - 0xFEE0);
            c = char.ToLowerInvariant(c);
            var letter = char.IsLetter(c);
            var m = letter ? c : c switch { '0' => 'o', '1' => 'i', '!' => 'i', '3' => 'e', '4' => 'a', '@' => 'a', '5' => 's', '$' => 's', '7' => 't', '*' => Wild, _ => '\0' };
            if (m != '\0' && (m != pa || m == Wild) && na < a.Length) { a[na++] = m; pa = m; }
            if (letter && c != pb && nb < b.Length) { b[nb++] = c; pb = c; }
        }
    }

    private int HashNorm()
    {
        if (_n2Len == 0 && _n1Len == 0) return 0;
        var h = 2166136261u;
        var src = _n2Len > 0 ? _n2.AsSpan(0, _n2Len) : _n1.AsSpan(0, _n1Len);
        foreach (var c in src) { h ^= c; h *= 16777619u; }
        return (int)(h | 1u);
    }

    private bool MatchAny()
    {
        ReadOnlySpan<char> a = _n1.AsSpan(0, _n1Len), b = _n2.AsSpan(0, _n2Len);
        var wild = a.IndexOf(Wild) >= 0;
        var second = !b.SequenceEqual(a);
        foreach (var t in _terms)
        {
            if (Hit(a, t, wild) || (second && Hit(b, t, false))) return true;
        }
        return false;
    }

    private bool Hit(ReadOnlySpan<char> s, string term, bool wild)
    {
        var tn = term.AsSpan();
        if (tn.Length == 0 || tn.Length > s.Length) return false;
        if (!wild)
        {
            var off = 0;
            while (off <= s.Length - tn.Length)
            {
                var k = s[off..].IndexOf(tn, StringComparison.Ordinal);
                if (k < 0) return false;
                if (!Covered(s, off + k, tn.Length)) return true;
                off += k + 1;
            }
            return false;
        }
        for (var i = 0; i <= s.Length - tn.Length; i++)
        {
            var ok = true; var real = 0;
            for (var j = 0; j < tn.Length && ok; j++) { var c = s[i + j]; if (c == Wild) continue; if (c != tn[j]) ok = false; else real++; }
            if (ok && real * 2 >= tn.Length - 1 && !Covered(s, i, tn.Length)) return true;
        }
        return false;
    }

    private bool Covered(ReadOnlySpan<char> s, int pos, int len)
    {
        foreach (var a in _allow)
        {
            for (var st = Math.Max(0, pos + len - a.Length); st <= pos; st++)
                if (st + a.Length <= s.Length && s.Slice(st, a.Length).SequenceEqual(a.AsSpan())) return true;
        }
        return false;
    }
}
