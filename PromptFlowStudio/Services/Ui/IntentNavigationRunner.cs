using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v17.89.0] 🧭 인텐트 내비게이션 상태 — 허브 모달 · 모달 헤더 <c>?</c> 도움말 서랍 · 글로벌 HUD(<c>NavRun</c>)가 공유한다.
/// 검색은 <see cref="CatalogIntentSearchEngine.Search"/> 를 고정 hits 버퍼에 채우고(무할당) 지연(µs) · 최대 지연 · 횟수를 집계한다.
/// 도움말 커버리지(<see cref="StudioCatalogHelpRegistry.CountDefects"/>)는 생성 시 1회 계산한다.
/// </summary>
public sealed class IntentNavigationRunner
{
    private readonly IntentHit[] _hits = new IntentHit[CatalogIntentSearchEngine.MaxHits];

    public IntentNavigationRunner()
    {
        HelpDefects = StudioCatalogHelpRegistry.CountDefects();
        HelpCovered = StudioCatalog.Entries.Length - HelpDefects;
    }

    public event Action? OnChange;
    public string Query { get; private set; } = "";
    public int HitCount { get; private set; }
    public IntentHit Hit(int i) => _hits[i];
    public long Searches { get; private set; }
    public long Opens { get; private set; }
    public long HelpViews { get; private set; }
    public double LastSearchUs { get; private set; }
    public double PeakSearchUs { get; private set; }
    public int HelpCovered { get; }
    public int HelpDefects { get; }
    public string? LastOpenedKey { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>질의 갱신 → 상위 hits 재계산. 빈 질의는 hits 0.</summary>
    public void Search(string? query)
    {
        Query = query ?? "";
        var t0 = Stopwatch.GetTimestamp();
        HitCount = CatalogIntentSearchEngine.Search(Query.AsSpan(), _hits);
        var us = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
        if (Query.Length > 0)
        {
            Searches++;
            LastSearchUs = us;
            if (us > PeakSearchUs) PeakSearchUs = us;
        }
        Raise();
    }

    public void NoteOpen(string key)
    {
        Opens++;
        LastOpenedKey = key;
        LastAction = "열기 · " + (StudioCatalogHelpRegistry.Entry(key)?.Title ?? key);
        Raise();
    }

    public void NoteHelp(string key)
    {
        HelpViews++;
        LastAction = "도움말 · " + (StudioCatalogHelpRegistry.Entry(key)?.Title ?? key);
        Raise();
    }

    public string Summary() =>
        "검색 " + Searches.ToString(CultureInfo.InvariantCulture) + " · 열기 " + Opens.ToString(CultureInfo.InvariantCulture) +
        " · 도움말 " + HelpViews.ToString(CultureInfo.InvariantCulture) + " · 커버리지 " + HelpCovered.ToString(CultureInfo.InvariantCulture) +
        "/" + StudioCatalog.Entries.Length.ToString(CultureInfo.InvariantCulture);

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("IntentNavigation.OnChange", ex); }
    }
}
