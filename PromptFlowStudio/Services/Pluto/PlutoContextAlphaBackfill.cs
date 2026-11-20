using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧬 컨텍스트 알파 표본 1건 — (Domain, RenderMode) 안에서 BGM · 태그 토큰이 짝 실험 상대 대비 낸 잔차 알파(합성 점수 0~1 척도).</summary>
public sealed record PlutoContextAlphaSample(string ExperimentId, string Domain, string RenderMode, string Token, double Alpha, DateTimeOffset ObservedUtc);

/// <summary>🧬 과거 이력 소급 결과.</summary>
public sealed record PlutoContextBackfillReport(bool Completed, bool AlreadyDone, int Experiments, int Samples, string MessageKorean)
{
    public static readonly PlutoContextBackfillReport Unavailable = new(false, false, 0, 0, "학습 메모리 미연결");
}

/// <summary>
/// 🧬 BGM 무드 · 스타일 태그 컨텍스트 알파 — 과거 A/B 실험 장부 소급(1회) + 이후 판정의 같은 공식 실시간 기록(실험 ID 단위 정확히 1회).
///   ▸ 장부 실측 사실: 실험마다 <c>ThemeHint</c> · 챔피언/챌린저 훅 · 룩 · BGM · 지표는 있지만 <c>Domain</c> · <c>StyleTags</c> · 렌더 포맷은 저장되지 않는다 →
///     Domain = <see cref="PromptDomainClassifier"/>(테마)(A/B 생성기와 같은 분류) · 포맷 = <see cref="DefaultRenderMode"/>(A/B 는 몽타주에서만 생성) ·
///     태그 = 렌더 카탈로그에서 변형 영상(파일명 = 영상 ID)으로 조인되는 경우만.
///   ▸ 기준선 = 짝 평균 — A/B 두 변형은 같은 컨텍스트를 공유하는 통제 실험이므로 짝 평균이 그 실험의 편향 없는 지역 기준선이다.
///     변형 A 알파 = 점수A − (점수A + 점수B)/2 · 두 변형이 같은 토큰(차이가 아닌 특성)은 실험 변수가 아니므로 제외.
///   ▸ 훅 · 룩 · 컨텍스트 기준선(ctx:) · 다목적 EWMA 는 건드리지 않는다(이미 기록됨 → 이중 계상 방지).
/// </summary>
public static class PlutoContextAlphaBackfill
{
    public const string MarkerKey = "migration:ctx-alpha-backfill-v1";
    public const string ExperimentKeyPrefix = "abctx:";
    public const string DefaultRenderMode = "몽타주";

    /// <summary>BGM(<c>custom</c> 제외 — 업로드 곡마다 다른 소리) · 태그 토큰만 통과.</summary>
    public static bool IsContextAlphaToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && (token.StartsWith(PlutoAdaptivePatternRanker.StylePrefix, StringComparison.Ordinal)
               || (token.StartsWith(PlutoAdaptivePatternRanker.BgmPrefix, StringComparison.Ordinal) && !token.Equals("bgm:custom", StringComparison.OrdinalIgnoreCase)))
           && token.Length > token.IndexOf(':') + 1;

    /// <summary>🧬 짝 실험 1건 → 표본(순수) — 비유한 점수 · 토큰 없음 → 빈 목록.</summary>
    public static IReadOnlyList<PlutoContextAlphaSample> PairSamples(string experimentId, string domain, string renderMode, double scoreA, double scoreB,
        IReadOnlyList<string> tokensA, IReadOnlyList<string> tokensB, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(tokensA);
        ArgumentNullException.ThrowIfNull(tokensB);
        var list = new List<PlutoContextAlphaSample>(tokensA.Count + tokensB.Count);
        if (string.IsNullOrWhiteSpace(experimentId) || !double.IsFinite(scoreA) || !double.IsFinite(scoreB)) return list;
        var mean = (scoreA + scoreB) / 2.0;
        Add(list, experimentId.Trim(), domain, renderMode, tokensA, tokensB, scoreA - mean, observedUtc);
        Add(list, experimentId.Trim(), domain, renderMode, tokensB, tokensA, scoreB - mean, observedUtc);
        return list;
    }

    private static void Add(List<PlutoContextAlphaSample> list, string id, string domain, string mode, IReadOnlyList<string> own, IReadOnlyList<string> other, double alpha, DateTimeOffset at)
    {
        for (var i = 0; i < own.Count; i++)
        {
            var t = own[i]?.Trim();
            if (!IsContextAlphaToken(t)) continue;
            var shared = false;
            for (var k = 0; k < other.Count && !shared; k++) shared = string.Equals(other[k]?.Trim(), t, StringComparison.OrdinalIgnoreCase);
            var dup = false;
            for (var k = 0; k < list.Count && !dup; k++) dup = list[k].ExperimentId == id && string.Equals(list[k].Token, t, StringComparison.OrdinalIgnoreCase);
            if (!shared && !dup) list.Add(new PlutoContextAlphaSample(id, domain, mode, t!, alpha, at));
        }
    }

    /// <summary>
    /// 🧬 장부 JSON → 판정 완료 실험의 표본(판정 시각 → 실험 ID 순 · 결정론). 손상 JSON → null(소급 완료 표시를 하지 않기 위해 '빈 목록'과 구분).
    /// </summary>
    public static IReadOnlyList<PlutoContextAlphaSample>? FromLedgerJson(string? ledgerJson, IReadOnlyList<PlutoRenderedClipRecord>? catalog, out int experiments)
    {
        experiments = 0;
        JsonObject root;
        try
        {
            if (JsonNode.Parse(ledgerJson ?? "") is not JsonObject o) return null;
            root = o;
        }
        catch (JsonException) { return null; }
        var all = new List<PlutoContextAlphaSample>();
        if (root["Experiments"] is not JsonArray arr) return all;
        foreach (var node in arr)
        {
            if (node is not JsonObject e || e["Evaluation"] is not JsonObject ev) continue;
            if (!TryMetrics(e["ChampionMetrics"], out var sa) || !TryMetrics(e["ChallengerMetrics"], out var sb)) continue;
            var id = Str(e, "ExperimentId");
            if (id.Length == 0) continue;
            var domain = PromptDomainClassifier.Classify(Str(e, "ThemeHint")).ToString();
            var at = DateTimeOffset.TryParse(Str(ev, "EvaluatedAtUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
            var samples = PairSamples(id, domain, DefaultRenderMode, sa, sb, Tokens(Str(e, "ChampionBgm"), Str(e, "ChampionVideoPath"), catalog), Tokens(Str(e, "ChallengerBgm"), Str(e, "ChallengerVideoPath"), catalog), at);
            if (samples.Count == 0) continue;
            experiments++;
            all.AddRange(samples);
        }
        all.Sort(static (x, y) =>
        {
            var c = x.ObservedUtc.CompareTo(y.ObservedUtc);
            if (c != 0) return c;
            c = string.CompareOrdinal(x.ExperimentId, y.ExperimentId);
            return c != 0 ? c : string.CompareOrdinal(x.Token, y.Token);
        });
        return all;
    }

    private static List<string> Tokens(string bgm, string videoPath, IReadOnlyList<PlutoRenderedClipRecord>? catalog)
    {
        var list = new List<string>(4);
        if (bgm.Length > 0) list.Add(PlutoAdaptivePatternRanker.BgmPrefix + bgm);
        if (catalog is null || videoPath.Length == 0) return list;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        for (var i = 0; i < catalog.Count; i++)
        {
            var c = catalog[i];
            if (c is null) continue;
            if (!string.Equals(c.VideoId, stem, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.OutputPath, videoPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Tags is not null)
                for (var k = 0; k < c.Tags.Count; k++)
                    if (!string.IsNullOrWhiteSpace(c.Tags[k])) list.Add(PlutoAdaptivePatternRanker.StylePrefix + c.Tags[k].Trim());
            break;
        }
        return list;
    }

    private static bool TryMetrics(JsonNode? n, out double score)
    {
        score = double.NaN;
        if (n is not JsonObject m) return false;
        double h = Num(m, "ThreeSecondHookRetentionPct"), c = Num(m, "AverageCompletionPct"), r = Num(m, "ClickThroughRatePct");
        if (!double.IsFinite(h) || !double.IsFinite(c) || !double.IsFinite(r)) return false;
        score = PlutoContextualBaseline.CompositeScore(h, c, r);
        return true;
    }

    private static double Num(JsonObject o, string p)
    {
        try { return o[p] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN; }
        catch (InvalidOperationException) { return double.NaN; }
    }

    private static string Str(JsonObject o, string p)
    {
        try { return o[p] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }
}

/// <summary>🧬 BGM · 태그 컨텍스트 알파 과거 이력 소급(1회 · 멱등).</summary>
public interface IPlutoContextAlphaBackfillService
{
    /// <summary>미완료면 장부를 읽어 소급(완료 표식은 학습 메모리와 같은 원자 기록) · 완료 → 즉시 반환 · 장부 부재/손상 → 표식 없이 다음 기회로.</summary>
    Task<PlutoContextBackfillReport> EnsureBackfilledAsync(CancellationToken ct = default);
}

/// <summary>🧬 <see cref="IPlutoContextAlphaBackfillService"/> 구현 — 동시 호출(기동 · 두뇌 리포트) 직렬화.</summary>
public sealed class PlutoContextAlphaBackfillService : IPlutoContextAlphaBackfillService, IDisposable
{
    private readonly PlutoLearningService? _learning;
    private readonly IPlutoStorageManager? _storage;
    private readonly IPlutoRenderCatalogService? _catalog;
    private readonly string? _ledgerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlutoContextBackfillReport? _done;

    public PlutoContextAlphaBackfillService(PlutoLearningService? learning, IPlutoStorageManager? storage, IPlutoRenderCatalogService? catalog = null, string? ledgerPath = null)
    {
        _learning = learning;
        _storage = storage;
        _catalog = catalog;
        _ledgerPath = ledgerPath;
    }

    public async Task<PlutoContextBackfillReport> EnsureBackfilledAsync(CancellationToken ct = default)
    {
        if (_done is { } d) return d;
        if (_learning is null) return PlutoContextBackfillReport.Unavailable;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_done is { } d2) return d2;
            await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_learning.HasContextAlphaBackfill) return _done = new PlutoContextBackfillReport(true, true, 0, 0, "소급 완료됨(재실행 없음)");
            var path = _ledgerPath ?? _storage?.ResolveDataPath(PlutoExperimentLedgerService.FileName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new PlutoContextBackfillReport(false, false, 0, 0, "실험 장부 없음 — 다음 기동에 재시도");
            string json;
            try { json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new PlutoContextBackfillReport(false, false, 0, 0, "장부 읽기 실패 — 다음 기동에 재시도: " + ex.Message); }
            IReadOnlyList<PlutoRenderedClipRecord>? clips = null;
            if (_catalog is not null)
            {
                try { clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Backfill] catalog: " + ex.Message); }   // [부분 구제] 태그 없이 BGM 만
            }
            if (PlutoContextAlphaBackfill.FromLedgerJson(json, clips, out _) is not { } samples)
                return new PlutoContextBackfillReport(false, false, 0, 0, "장부 손상 — 소급 보류(다음 기동에 재시도)");
            var applied = await _learning.RecordContextAlphaSamplesAsync(samples, historical: true, PlutoContextAlphaBackfill.MarkerKey, ct).ConfigureAwait(false);
            return _done = new PlutoContextBackfillReport(true, applied < 0, Math.Max(0, applied), samples.Count,
                applied < 0 ? "소급 완료됨(재실행 없음)" : "과거 A/B " + applied.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건 → BGM · 태그 컨텍스트 알파 소급");
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
