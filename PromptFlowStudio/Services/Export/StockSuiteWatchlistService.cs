using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PromptFlowStudio.Services.Export;

/// <summary>📊 관심종목 1건 — 티커(국내 6자리 코드/미국 심볼) · 종목명 · 섹터.</summary>
public sealed record WatchlistStock(string Ticker, string Name, string Sector)
{
    /// <summary>칩 표기 — <c>005930 삼성전자</c>.</summary>
    public string ChipLabel => Ticker + " " + Name;
}

/// <summary>관심종목 로드 결과 — FromFile=false 면 기본 핀테크 종목군(SourcePath null).</summary>
public sealed record WatchlistLoadResult(IReadOnlyList<WatchlistStock> Stocks, bool FromFile, string? SourcePath);

/// <summary>
/// [PromptFlowStudio] 📊 StockSuite 관심종목(Watchlist) 연동 — StockSuite 설정 경로에서 <c>watchlist.json</c> / <c>config/favorites.json</c> 탐색·파싱.
///   ▸ 허용 스키마: 배열 루트 또는 <c>{"watchlist"|"favorites"|"items"|"stocks": [...]}</c> · 원소는 문자열 티커 또는
///     <c>{ticker|code|symbol, name|stockName|title, sector|industry}</c>. 티커 중복은 1회만(alreadyRegistered 차단).
///   ▸ 파일 부재/손상/빈 목록 → 코스피 시총 TOP 5 + 나스닥 매그니피센트 7 기본 제공(무중단 폴백).
/// </summary>
public sealed class StockSuiteWatchlistService
{
    public const int MaxStocks = 50;
    private const long MaxFileBytes = 1 << 20;
    public static readonly string[] CandidateRelativePaths = { "watchlist.json", Path.Combine("config", "favorites.json"), Path.Combine("config", "watchlist.json"), "favorites.json" };

    private static readonly WatchlistStock[] DefaultStocks =
    {
        new("005930", "삼성전자", "반도체"),
        new("000660", "SK하이닉스", "반도체"),
        new("373220", "LG에너지솔루션", "2차전지"),
        new("207940", "삼성바이오로직스", "바이오"),
        new("005380", "현대차", "자동차"),
        new("AAPL", "애플", "IT 하드웨어"),
        new("MSFT", "마이크로소프트", "소프트웨어"),
        new("NVDA", "엔비디아", "반도체"),
        new("GOOGL", "알파벳", "인터넷"),
        new("AMZN", "아마존", "이커머스"),
        new("META", "메타", "인터넷"),
        new("TSLA", "테슬라", "전기차"),
    };

    private readonly Func<IEnumerable<string?>> _searchRoots;
    private readonly object _lock = new();
    private WatchlistLoadResult? _cache;

    public StockSuiteWatchlistService() : this(null) { }

    /// <param name="searchRoots">탐색 루트 공급자(StockSuite 에셋 폴더 · 실행 파일 폴더 등 · null/빈 값 무시).</param>
    public StockSuiteWatchlistService(Func<IEnumerable<string?>>? searchRoots)
    {
        _searchRoots = searchRoots ?? (static () => Array.Empty<string?>());
    }

    /// <summary>탐색 루트 구성 — 에셋 폴더 · 그 상위 · StockSuite.exe 폴더(중복 제거 · 잘못된 경로 무시).</summary>
    public static IEnumerable<string?> RootsFrom(string? assetDir, string? executablePath)
    {
        var roots = new List<string?>(3);
        try
        {
            if (!string.IsNullOrWhiteSpace(assetDir))
            {
                roots.Add(assetDir);
                roots.Add(Path.GetDirectoryName(Path.GetFullPath(assetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }
            if (!string.IsNullOrWhiteSpace(executablePath)) roots.Add(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { Program.Log("Watchlist.Roots", ex); }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string?>(roots.Count);
        foreach (var r in roots) if (!string.IsNullOrWhiteSpace(r) && seen.Add(r)) unique.Add(r);
        return unique;
    }

    /// <summary>기본 핀테크 종목군(코스피 TOP 5 + 매그니피센트 7).</summary>
    public static IReadOnlyList<WatchlistStock> Defaults => DefaultStocks;

    /// <summary>마지막 로드 결과(미로드 시 즉시 로드).</summary>
    public WatchlistLoadResult Current
    {
        get
        {
            lock (_lock) if (_cache is not null) return _cache;
            return Load();
        }
    }

    /// <summary>탐색 루트 순회 → 첫 유효 파일 파싱. 없으면 기본 종목군.</summary>
    public WatchlistLoadResult Load()
    {
        WatchlistLoadResult result = new(DefaultStocks, false, null);
        IEnumerable<string?> roots;
        try { roots = _searchRoots() ?? Array.Empty<string?>(); }
        catch (Exception ex) { Program.Log("Watchlist.Roots", ex); roots = Array.Empty<string?>(); }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var hit = TryLoadFrom(root);
            if (hit is not null) { result = hit; break; }
        }
        lock (_lock) _cache = result;
        return result;
    }

    /// <summary>단일 루트에서 후보 파일 탐색 — 유효 종목이 1개 이상이면 결과, 아니면 null.</summary>
    public static WatchlistLoadResult? TryLoadFrom(string root)
    {
        foreach (var rel in CandidateRelativePaths)
        {
            string path;
            try
            {
                path = Path.Combine(root, rel);
                if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { continue; }
            try
            {
                var list = Parse(File.ReadAllText(path));
                if (list.Count > 0) return new(list, true, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("Watchlist.Read", ex); }
        }
        return null;
    }

    /// <summary>JSON → 종목 리스트(손상/스키마 불일치 시 빈 목록 · throw 없음).</summary>
    public static IReadOnlyList<WatchlistStock> Parse(string? json)
    {
        var list = new List<WatchlistStock>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object)
            {
                arr = default;
                foreach (var key in new[] { "watchlist", "favorites", "items", "stocks" })
                    if (doc.RootElement.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) { arr = a; break; }
            }
            if (arr.ValueKind != JsonValueKind.Array) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in arr.EnumerateArray())
            {
                WatchlistStock? s = e.ValueKind switch
                {
                    JsonValueKind.String => FromTicker(e.GetString()),
                    JsonValueKind.Object => FromObject(e),
                    _ => null,
                };
                if (s is null || !seen.Add(s.Ticker)) continue;
                list.Add(s);
                if (list.Count >= MaxStocks) break;
            }
        }
        catch (JsonException ex) { Program.Log("Watchlist.Parse", ex); list.Clear(); }
        return list;
    }

    private static WatchlistStock? FromTicker(string? ticker)
    {
        var t = (ticker ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length < 6 && IsAllDigits(t)) t = t.PadLeft(6, '0');   // 숫자형 국내 코드(5930) → 005930
        foreach (var d in DefaultStocks)
            if (d.Ticker.Equals(t, StringComparison.OrdinalIgnoreCase)) return d;
        return new(t.ToUpperInvariant(), t.ToUpperInvariant(), "");
    }

    private static WatchlistStock? FromObject(JsonElement e)
    {
        var ticker = First(e, "ticker", "code", "symbol");
        if (ticker.Length == 0) return null;
        var name = First(e, "name", "stockName", "title");
        var sector = First(e, "sector", "industry");
        var known = FromTicker(ticker)!;
        return new(known.Ticker, name.Length > 0 ? name : known.Name, sector.Length > 0 ? sector : known.Sector);
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return true;
    }

    private static string First(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0) return s.Trim();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
        return "";
    }
}
