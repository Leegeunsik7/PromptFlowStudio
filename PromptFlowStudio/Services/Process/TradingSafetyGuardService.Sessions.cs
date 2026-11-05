using System;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>📈 KRX 4단계 증시 세션.</summary>
public enum KrxSessionStatus
{
    /// <summary>장마감(시간외 종료 ~ 익일 장전 · 주말/휴장일) — 야간 자유 모드.</summary>
    Closed = 0,
    /// <summary>장전 동시호가(08:30~09:00).</summary>
    PreMarket = 1,
    /// <summary>정규장(09:00~15:30) — 안전 모드 대상.</summary>
    RegularMarket = 2,
    /// <summary>장후 시간외 단일가(15:30~18:00).</summary>
    PostMarket = 3,
}

/// <summary>세션 판정 결과 — 현재 단계 · 다음 전환 시각(KST) · 남은 시간.</summary>
public readonly record struct KrxSessionInfo(KrxSessionStatus Status, DateTimeOffset NextChangeKst, TimeSpan Remaining);

public sealed partial class TradingSafetyGuardService
{
    /// <summary>다음 영업일 탐색 상한(연휴 최장 + 여유).</summary>
    private const int MaxBusinessDayLookahead = 31;

    /// <summary>현재 세션(주입 시계 기준).</summary>
    public KrxSessionInfo CurrentSession => ResolveSession(UtcNow());

    /// <summary>
    /// 순수 판정 — <paramref name="at"/> 의 KST 세션 단계. 주말·KRX 휴장일 = <see cref="KrxSessionStatus.Closed"/>,
    /// 일자별 시각은 <see cref="KrxHolidayCalendar.HoursOf"/>(krx_closures.json 오버라이드 · 수능 10:00 개장 등) 적용.
    /// </summary>
    public static KrxSessionInfo ResolveSession(DateTimeOffset at)
    {
        var kst = at.ToOffset(Kst);
        var date = DateOnly.FromDateTime(kst.DateTime);
        var t = kst.TimeOfDay;

        if (!KrxHolidayCalendar.IsClosed(date))
        {
            var h = KrxHolidayCalendar.HoursOf(date);
            if (t < h.PreOpen) return Make(KrxSessionStatus.Closed, kst, date, h.PreOpen);
            if (t < h.Open) return Make(KrxSessionStatus.PreMarket, kst, date, h.Open);
            if (t < h.Close) return Make(KrxSessionStatus.RegularMarket, kst, date, h.Close);
            if (t < h.PostClose) return Make(KrxSessionStatus.PostMarket, kst, date, h.PostClose);
        }

        var next = date.AddDays(1);
        for (var i = 0; i < MaxBusinessDayLookahead && KrxHolidayCalendar.IsClosed(next); i++) next = next.AddDays(1);
        return Make(KrxSessionStatus.Closed, kst, next, KrxHolidayCalendar.HoursOf(next).PreOpen);
    }

    private static KrxSessionInfo Make(KrxSessionStatus status, DateTimeOffset kst, DateOnly day, TimeSpan at)
    {
        var change = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, Kst) + at;
        var remain = change - kst;
        return new(status, change, remain < TimeSpan.Zero ? TimeSpan.Zero : remain);
    }

    /// <summary>툴바 배지 문구(상수 · 무할당).</summary>
    public static string SessionLabel(KrxSessionStatus s) => s switch
    {
        KrxSessionStatus.PreMarket => "🟡 장전 준비 중",
        KrxSessionStatus.RegularMarket => "🛡️ 정규장 안전 모드 가동 중",
        KrxSessionStatus.PostMarket => "🟠 시간외 거래 세션",
        _ => "🟢 야간 자유 모드 (GPU 100%)",
    };

    /// <summary>배지 CSS 수식자.</summary>
    public static string SessionCss(KrxSessionStatus s) => s switch
    {
        KrxSessionStatus.PreMarket => "pre",
        KrxSessionStatus.RegularMarket => "regular",
        KrxSessionStatus.PostMarket => "post",
        _ => "closed",
    };

    /// <summary>남은 시간 표기 — <c>H시간 M분</c> / <c>M분</c>(InvariantCulture 정수).</summary>
    public static string FormatRemaining(TimeSpan r)
    {
        var totalMin = (long)Math.Ceiling(Math.Max(0, r.TotalMinutes));
        var h = totalMin / 60;
        var m = totalMin % 60;
        var mm = m.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return h > 0 ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) + "시간 " + mm + "분" : mm + "분";
    }
}
