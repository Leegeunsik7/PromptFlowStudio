using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>📅 KRX 일자별 세션 시각 — 장전 동시호가 시작 · 정규장 개장 · 정규장 마감 · 시간외 종료(KST).</summary>
public readonly record struct KrxSessionHours(TimeSpan PreOpen, TimeSpan Open, TimeSpan Close, TimeSpan PostClose)
{
    /// <summary>표준 세션 — 08:30 장전 · 09:00 개장 · 15:30 마감 · 18:00 시간외 종료.</summary>
    public static readonly KrxSessionHours Standard = new(new(8, 30, 0), new(9, 0, 0), new(15, 30, 0), new(18, 0, 0));

    /// <summary>시각 순서 정합(PreOpen ≤ Open &lt; Close ≤ PostClose · 하루 이내).</summary>
    public bool IsValid => PreOpen >= TimeSpan.Zero && PreOpen <= Open && Open < Close && Close <= PostClose && PostClose <= TimeSpan.FromHours(24);
}

/// <summary>
/// 📅 비정기 일정 확장 — 앱 데이터 디렉터리 <c>krx_closures.json</c>(수능일 개장 지연 · 임시공휴일 · 조기 폐장) 동적 수용.
/// <code>
/// { "closures": [ "2026-06-03", { "date": "2026-10-02", "note": "임시공휴일" } ],
///   "sessions": [ { "date": "2026-11-19", "open": "10:00", "close": "16:30", "note": "수능" } ] }
/// </code>
///   ▸ sessions 의 preOpen 생략 = open-30분 · postClose 생략 = close+2시간30분(표준 15:30→18:00 간격 유지).
///   ▸ 파싱 실패 항목은 건너뛰고 나머지 반영([부분 구제]) · 파일 부재 = 0건(예외 없음).
/// </summary>
public static partial class KrxHolidayCalendar
{
    public const string ClosuresFileName = "krx_closures.json";
    private static readonly ConcurrentDictionary<DateOnly, KrxSessionHours> SessionOverrides = new();
    private static long _closureVersion;

    /// <summary>비정기 일정 변경 카운터(진단 · UI 재평가 트리거).</summary>
    public static long ClosureVersion => Interlocked.Read(ref _closureVersion);

    /// <summary>기본 경로 — <c>%LOCALAPPDATA%/PromptFlowStudio/Config/krx_closures.json</c>.</summary>
    public static string DefaultClosuresPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Config", ClosuresFileName);

    /// <summary>일자별 세션 시각 오버라이드 등록(유효하지 않으면 false).</summary>
    public static bool SetSessionHours(DateOnly date, KrxSessionHours hours)
    {
        if (!hours.IsValid) return false;
        SessionOverrides[date] = hours;
        Interlocked.Increment(ref _closureVersion);
        return true;
    }

    /// <summary>일자별 세션 오버라이드 제거.</summary>
    public static bool ClearSessionHours(DateOnly date)
    {
        var removed = SessionOverrides.TryRemove(date, out _);
        if (removed) Interlocked.Increment(ref _closureVersion);
        return removed;
    }

    /// <summary>해당 일자 세션 시각(오버라이드 우선 · 기본 <see cref="KrxSessionHours.Standard"/>).</summary>
    public static KrxSessionHours HoursOf(DateOnly date)
        => SessionOverrides.TryGetValue(date, out var h) ? h : KrxSessionHours.Standard;

    /// <summary><c>krx_closures.json</c> 로드 — 반영 건수(휴장+세션) 반환. 파일 부재 0 · 읽기 실패 -1.</summary>
    public static int LoadClosures(string? path = null)
    {
        var p = string.IsNullOrWhiteSpace(path) ? DefaultClosuresPath : path!;
        try
        {
            if (!File.Exists(p)) return 0;
            return LoadClosuresFromJson(File.ReadAllText(p));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Program.Log("KrxCalendar.LoadClosures", ex);
            return -1;
        }
    }

    /// <summary>JSON 본문 반영(스키마 즉시 검증 · 불량 항목 건너뜀). 루트 손상 시 -1.</summary>
    public static int LoadClosuresFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return -1;
            var count = 0;

            if (root.TryGetProperty("closures", out var closures) && closures.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in closures.EnumerateArray())
                {
                    var raw = e.ValueKind switch
                    {
                        JsonValueKind.String => e.GetString(),
                        JsonValueKind.Object when e.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String => d.GetString(),
                        _ => null,
                    };
                    if (TryParseDate(raw, out var date)) { AddClosure(date); count++; }
                }
            }

            if (root.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in sessions.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!TryParseDate(Str(e, "date"), out var date)) continue;
                    var std = KrxSessionHours.Standard;
                    var open = TryParseTime(Str(e, "open"), out var o) ? o : std.Open;
                    var close = TryParseTime(Str(e, "close"), out var c) ? c : std.Close;
                    var pre = TryParseTime(Str(e, "preOpen"), out var po) ? po : open - TimeSpan.FromMinutes(30);
                    var post = TryParseTime(Str(e, "postClose"), out var pc) ? pc : close + (std.PostClose - std.Close);
                    if (SetSessionHours(date, new KrxSessionHours(pre, open, close, post))) count++;
                }
            }
            return count;
        }
        catch (JsonException ex) { Program.Log("KrxCalendar.ParseClosures", ex); return -1; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryParseDate(string? s, out DateOnly date)
        => DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryParseTime(string? s, out TimeSpan t)
        => TimeSpan.TryParseExact(s?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out t)
        || TimeSpan.TryParseExact(s?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out t);
}
