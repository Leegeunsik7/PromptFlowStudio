using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>
/// 📅 KRX 한국거래소 휴장일 캘린더 — 순수 BCL(<see cref="KoreanLunisolarCalendar"/>로 음력 환산 · 외부 데이터 0).
///   ▸ 주말 · 신정 · 삼일절 · 근로자의 날(5/1 · 증시 휴장) · 어린이날 · 부처님오신날 · 현충일 · 광복절 · 개천절 · 한글날 · 성탄절.
///   ▸ 설날/추석 3일 연휴 + 대체공휴일(설·추석·어린이날 2014~ · 국경일 2021~ · 부처님오신날/성탄절 2023~).
///   ▸ 연말 폐장일 — 12/31(주말·공휴일이면 직전 영업일).
///   ▸ 선거일·임시공휴일 등 비정기 휴장은 <see cref="AddClosure"/> 로 주입. 연도별 집합 캐시(최초 1회 계산 후 무할당 조회).
/// </summary>
public static partial class KrxHolidayCalendar
{
    private static readonly KoreanLunisolarCalendar Lunar = new();
    private static readonly ConcurrentDictionary<int, HashSet<DateOnly>> Cache = new();
    private static readonly ConcurrentDictionary<DateOnly, byte> Extra = new();

    /// <summary>비정기 휴장일(선거일·임시공휴일) 추가 — 해당 연도 캐시 무효화.</summary>
    public static void AddClosure(DateOnly date)
    {
        Extra[date] = 0;
        Cache.TryRemove(date.Year, out _);
        Interlocked.Increment(ref _closureVersion);
    }

    /// <summary>비정기 휴장일 제거.</summary>
    public static bool RemoveClosure(DateOnly date)
    {
        var removed = Extra.TryRemove(date, out _);
        if (removed) { Cache.TryRemove(date.Year, out _); Interlocked.Increment(ref _closureVersion); }
        return removed;
    }

    /// <summary>주말 또는 평일 휴장일이면 true(= 장 미개장).</summary>
    public static bool IsClosed(DateOnly date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsHoliday(date);

    /// <summary>평일 휴장일(공휴일·대체공휴일·근로자의 날·연말 폐장일·비정기) 여부 — 주말 자체는 false.</summary>
    public static bool IsHoliday(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return Cache.GetOrAdd(date.Year, static y => Build(y)).Contains(date);
    }

    /// <summary>KST 시각 기준 휴장 여부.</summary>
    public static bool IsClosed(DateTimeOffset at) => IsClosed(DateOnly.FromDateTime(at.ToOffset(TradingSafetyGuardService.Kst).DateTime));

    /// <summary>해당 연도의 평일 휴장일 목록(오름차순 · 진단/UI).</summary>
    public static IReadOnlyList<DateOnly> HolidaysOf(int year)
    {
        var list = new List<DateOnly>(Cache.GetOrAdd(year, static y => Build(y)));
        list.Sort();
        return list;
    }

    /// <summary>음력 → 양력(윤달 보정). 범위 밖이면 null.</summary>
    public static DateOnly? FromLunar(int year, int month, int day)
    {
        try
        {
            if (year < Lunar.MinSupportedDateTime.Year + 1 || year > Lunar.MaxSupportedDateTime.Year - 1) return null;
            var leap = Lunar.GetLeapMonth(year);   // 0 = 윤달 없음 · n = n번째 달이 윤달(직전 달의 윤달)
            var idx = leap != 0 && month >= leap ? month + 1 : month;
            return DateOnly.FromDateTime(Lunar.ToDateTime(year, idx, day, 0, 0, 0, 0));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static HashSet<DateOnly> Build(int year)
    {
        // 출처별 공휴일 카운트(주말 포함) — 2건 이상 = 공휴일 겹침(대체공휴일 사유).
        var src = new Dictionary<DateOnly, int>();
        void Add(DateOnly d) { if (d.Year == year) src[d] = src.TryGetValue(d, out var n) ? n + 1 : 1; }
        DateOnly F(int m, int d) => new(year, m, d);

        foreach (var (m, d) in FixedDays) Add(F(m, d));
        var seollal = Block(FromLunar(year, 1, 1));
        var chuseok = Block(FromLunar(year, 8, 15));
        var buddha = FromLunar(year, 4, 8);
        foreach (var d in seollal) Add(d);
        foreach (var d in chuseok) Add(d);
        if (buddha is { } b) Add(b);

        var subs = new List<DateOnly>();
        if (year >= 2014)
        {
            BlockSubstitute(seollal, src, subs);
            BlockSubstitute(chuseok, src, subs);
            SingleSubstitute(F(5, 5), src, subs);
        }
        if (year >= 2021)
        {
            SingleSubstitute(F(3, 1), src, subs);
            SingleSubstitute(F(8, 15), src, subs);
            SingleSubstitute(F(10, 3), src, subs);
            SingleSubstitute(F(10, 9), src, subs);
        }
        if (year >= 2023)
        {
            if (buddha is { } bd) SingleSubstitute(bd, src, subs);
            SingleSubstitute(F(12, 25), src, subs);
        }

        var all = new HashSet<DateOnly>(src.Keys);
        foreach (var s in subs) all.Add(s);
        foreach (var kv in Extra) if (kv.Key.Year == year) all.Add(kv.Key);

        // 연말 폐장일 — 12/31부터 역방향 첫 영업일.
        var eoy = F(12, 31);
        while (IsWeekend(eoy) || all.Contains(eoy)) eoy = eoy.AddDays(-1);
        all.Add(eoy);

        all.RemoveWhere(IsWeekend);
        return all;
    }

    private static readonly (int M, int D)[] FixedDays =
        { (1, 1), (3, 1), (5, 1), (5, 5), (6, 6), (8, 15), (10, 3), (10, 9), (12, 25) };

    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>설·추석 연휴 3일(전날·당일·다음날).</summary>
    private static DateOnly[] Block(DateOnly? center)
        => center is { } c ? new[] { c.AddDays(-1), c, c.AddDays(1) } : Array.Empty<DateOnly>();

    private static bool Overlaps(DateOnly d, Dictionary<DateOnly, int> src) => src.TryGetValue(d, out var n) && n >= 2;

    private static DateOnly NextFree(DateOnly from, Dictionary<DateOnly, int> src, List<DateOnly> subs)
    {
        var cur = from;
        do cur = cur.AddDays(1);
        while (IsWeekend(cur) || src.ContainsKey(cur) || subs.Contains(cur));
        return cur;
    }

    /// <summary>설·추석 — 연휴 중 일요일 또는 타 공휴일과 겹친 날 수만큼 연휴 직후 비휴일 평일(토요일 미적용).</summary>
    private static void BlockSubstitute(DateOnly[] block, Dictionary<DateOnly, int> src, List<DateOnly> subs)
    {
        if (block.Length == 0) return;
        var cur = block[^1];
        foreach (var d in block)
        {
            if (d.DayOfWeek != DayOfWeek.Sunday && !Overlaps(d, src)) continue;
            cur = NextFree(cur, src, subs);
            subs.Add(cur);
        }
    }

    /// <summary>단일 공휴일 — 토·일 또는 타 공휴일과 겹치면 다음 비휴일 평일.</summary>
    private static void SingleSubstitute(DateOnly day, Dictionary<DateOnly, int> src, List<DateOnly> subs)
    {
        if (!IsWeekend(day) && !Overlaps(day, src)) return;
        subs.Add(NextFree(day, src, subs));
    }
}
