using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>⚔️ 보관된 분기 충돌 사본 1건 — 허브 파일 · 보관 경로 · 로컬 수정 시각 · 크기 · 의미 병합 가능 여부 · JSON 유효성.</summary>
public sealed record PlutoConflictCopy(string HubFileName, string FullPath, DateTime CapturedUtc, long SizeBytes, bool IsMergeable, bool IsValidJson)
{
    public string FileName => Path.GetFileName(FullPath);
}

/// <summary>⚔️ 충돌 해결 결과.</summary>
public enum PlutoConflictAction { None = 0, MergeStaged = 1, RestoreStaged = 2, Dismissed = 3 }

public sealed record PlutoConflictResolution(PlutoConflictAction Action, string HubFileName, string MessageKorean)
{
    public bool Ok => Action != PlutoConflictAction.None;
}

/// <summary>
/// ⚔️ 분기 충돌 사본 관리 + 의미 기반 합집합 병합(순수 · 결정론) — 두 PC 의 오프라인 작업을 어느 쪽도 잃지 않고 합친다.
///   ▸ 병합 가능 허브: 실험 장부(실험 ID) · 오답 노트(라벨) · 렌더 카탈로그(영상 ID) · 시리즈 아크(시리즈 키 → 편 번호) · 학습 메모리(키).
///   ▸ 키가 겹치면: 진행이 더 된 쪽(판정 완료 &gt; 지표 &gt; 대기 · 측정 &gt; 미측정) → 더 최근 시각 → 현재 사본. 누적 카운터는 합이 아니라 최댓값(공통 이력 이중 계상 방지).
///   ▸ JsonNode 로 다루므로 모르는 필드(이후 스키마 확장)도 보존 · 둘 중 하나라도 손상 → null(아무것도 쓰지 않음).
///   ▸ 적용은 '대기열'에 올린 뒤 그 허브가 이번 세션에 처음 적재되기 직전에 일어난다(<see cref="PlutoDriveStorageManager.ApplyPendingResolution(string)"/>) —
///     이미 메모리에 상태를 든 서비스가 다음 기록으로 병합 결과를 되덮지 않게 하기 위함.
/// </summary>
public sealed class PlutoConflictMergeService
{
    public static readonly IReadOnlyList<string> MergeableHubs = new[]
    {
        PlutoExperimentLedgerService.FileName, PlutoMistakeNotebookService.FileName, PlutoRenderCatalogService.FileName,
        PlutoSeriesArcMemoryService.FileName, PlutoLearningService.FileName,
    };

    private const string ConflictMarker = ".conflict-";
    private readonly PlutoDriveStorageManager _storage;

    public PlutoConflictMergeService(PlutoDriveStorageManager storage) => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    public string ConflictRoot => Path.Combine(_storage.FallbackRoot, PlutoDriveStorageManager.ConflictFolderName);

    public static bool IsMergeable(string? hubFileName)
    {
        if (string.IsNullOrWhiteSpace(hubFileName)) return false;
        for (var i = 0; i < MergeableHubs.Count; i++) if (string.Equals(MergeableHubs[i], hubFileName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary><c>{stem}.conflict-{yyyyMMddHHmmss}[-n]{ext}</c> → 허브 파일명(형식 불일치 → null).</summary>
    public static string? HubFileOf(string? conflictFileName)
    {
        if (string.IsNullOrWhiteSpace(conflictFileName)) return null;
        var name = Path.GetFileName(conflictFileName);
        var at = name.LastIndexOf(ConflictMarker, StringComparison.Ordinal);
        if (at <= 0) return null;
        var ext = Path.GetExtension(name);
        if (ext.Length == 0) return null;
        return name[..at] + ext;
    }

    // ── 목록 · 해결 ──

    /// <summary>⚔️ 미해결 충돌 사본(보관 시각 최신순 · 같은 시각 → 이름순).</summary>
    public IReadOnlyList<PlutoConflictCopy> ListConflicts()
    {
        var list = new List<PlutoConflictCopy>();
        try
        {
            if (!Directory.Exists(ConflictRoot)) return list;
            foreach (var path in Directory.GetFiles(ConflictRoot))
            {
                if (HubFileOf(path) is not { } hub) continue;
                var fi = new FileInfo(path);
                list.Add(new PlutoConflictCopy(hub, path, fi.LastWriteTimeUtc, fi.Length, IsMergeable(hub), fi.Length > 0 && IsValidJsonText(SafeRead(path))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Conflict] list: " + ex.Message); }
        list.Sort(static (a, b) =>
        {
            var c = b.CapturedUtc.CompareTo(a.CapturedUtc);
            return c != 0 ? c : string.CompareOrdinal(a.FileName, b.FileName);
        });
        return list;
    }

    /// <summary>📥 적용 대기 중인 해결 수(다음 적재 시 반영).</summary>
    public int PendingCount => _storage.PendingResolutionCount;

    /// <summary>
    /// 🔀 병합 대기 — 사본을 대기열로 옮긴다(같은 허브에 대기 병합이 이미 있으면 둘을 먼저 합친다). 병합 불가 허브 · 손상 사본 → 거절(파일 그대로).
    /// </summary>
    public PlutoConflictResolution StageMerge(string conflictPath)
    {
        if (Validate(conflictPath, out var hub, out var text) is { } bad) return bad;
        if (!IsMergeable(hub)) return new(PlutoConflictAction.None, hub, "⚠ '" + hub + "' 는 의미 병합을 지원하지 않아요 — 사본 복원 또는 무시를 선택하세요");
        try
        {
            var target = Path.Combine(EnsurePending(), hub + PlutoDriveStorageManager.PendingMergeExt);
            if (File.Exists(target) && MergeJson(hub, SafeRead(target), text) is { } combined) text = combined;   // 같은 허브 대기 병합 두 건 → 먼저 합침
            File.WriteAllText(target, text);
            File.Delete(conflictPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(PlutoConflictAction.None, hub, "⚠ 병합 예약 실패: " + ex.Message); }
        return new(PlutoConflictAction.MergeStaged, hub, "🔀 '" + hub + "' 병합 예약 — 다음 적재(앱 재시작) 때 현재 기록과 합쳐져요");
    }

    /// <summary>↩️ 사본 복원 대기 — 다음 적재 때 이 사본이 현재 기록을 대체(같은 허브의 대기 병합보다 우선).</summary>
    public PlutoConflictResolution StageRestore(string conflictPath)
    {
        if (Validate(conflictPath, out var hub, out var text) is { } bad) return bad;
        try
        {
            File.WriteAllText(Path.Combine(EnsurePending(), hub + PlutoDriveStorageManager.PendingReplaceExt), text);
            File.Delete(conflictPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(PlutoConflictAction.None, hub, "⚠ 복원 예약 실패: " + ex.Message); }
        return new(PlutoConflictAction.RestoreStaged, hub, "↩️ '" + hub + "' 사본 복원 예약 — 다음 적재(앱 재시작) 때 적용돼요");
    }

    /// <summary>🗑️ 무시 — 사본 삭제(현재 기록 유지).</summary>
    public PlutoConflictResolution Dismiss(string conflictPath)
    {
        if (!IsInsideConflictRoot(conflictPath) || HubFileOf(conflictPath) is not { } hub) return new(PlutoConflictAction.None, "", "⚠ 충돌 사본이 아닙니다");
        try { if (File.Exists(conflictPath)) File.Delete(conflictPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(PlutoConflictAction.None, hub, "⚠ 삭제 실패: " + ex.Message); }
        return new(PlutoConflictAction.Dismissed, hub, "🗑️ '" + hub + "' 충돌 사본을 정리했어요(현재 기록 유지)");
    }

    private PlutoConflictResolution? Validate(string conflictPath, out string hub, out string text)
    {
        hub = HubFileOf(conflictPath) ?? string.Empty;
        text = string.Empty;
        if (hub.Length == 0 || !IsInsideConflictRoot(conflictPath) || !File.Exists(conflictPath)) return new(PlutoConflictAction.None, hub, "⚠ 충돌 사본을 찾을 수 없어요");
        text = SafeRead(conflictPath);
        return IsValidJsonText(text) ? null : new(PlutoConflictAction.None, hub, "⚠ 손상된 사본이라 적용할 수 없어요(무시만 가능)");
    }

    /// <summary>경로 탈출 방지 — 충돌 폴더 바로 아래 파일만.</summary>
    private bool IsInsideConflictRoot(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return dir is not null && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(ConflictRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private string EnsurePending()
    {
        Directory.CreateDirectory(_storage.PendingRoot);
        return _storage.PendingRoot;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    // ── 🔀 의미 병합(순수) ──

    public static bool IsValidJsonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>🔀 허브별 합집합 병합(순수 · 결정론) — <paramref name="current"/> = 지금 쓰는 사본 · <paramref name="other"/> = 상대(충돌 사본). 손상 · 비병합 허브 → null.</summary>
    public static string? MergeJson(string hubFileName, string? current, string? other)
    {
        if (!IsMergeable(hubFileName)) return null;
        JsonObject a, b;
        try
        {
            if (JsonNode.Parse(current ?? "") is not JsonObject ja || JsonNode.Parse(other ?? "") is not JsonObject jb) return null;
            a = ja; b = jb;
        }
        catch (JsonException) { return null; }

        JsonObject merged;
        if (Eq(hubFileName, PlutoExperimentLedgerService.FileName)) merged = MergeLedger(a, b);
        else if (Eq(hubFileName, PlutoMistakeNotebookService.FileName)) merged = MergeKeyedObject(a, b, "Entries", MergeNotebookEntry);
        else if (Eq(hubFileName, PlutoRenderCatalogService.FileName)) merged = MergeCatalog(a, b);
        else if (Eq(hubFileName, PlutoSeriesArcMemoryService.FileName)) merged = MergeArc(a, b);
        else merged = MergeTrendMemory(a, b);
        return merged.ToJsonString(Compact);
    }

    private static bool Eq(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    // ── 실험 장부 — 실험 ID · 판정 완료 > 지표 > 대기 · 변형 영상 경로는 빈 쪽을 채움 ──

    private static JsonObject MergeLedger(JsonObject a, JsonObject b)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var side in new[] { a, b })
            foreach (var e in Items(side, "Experiments"))
            {
                var id = Str(e, "ExperimentId");
                if (id.Length == 0) continue;
                if (!map.TryGetValue(id, out var cur)) { map[id] = (JsonObject)e.DeepClone(); order.Add(id); continue; }
                var pick = LedgerProgress(e) > LedgerProgress(cur) || (LedgerProgress(e) == LedgerProgress(cur) && Time(e, "RegisteredAtUtc") > Time(cur, "RegisteredAtUtc"))
                    ? (JsonObject)e.DeepClone() : cur;
                var rest = ReferenceEquals(pick, cur) ? e : cur;
                foreach (var f in new[] { "ChampionVideoPath", "ChallengerVideoPath" })
                    if (Str(pick, f).Length == 0 && Str(rest, f).Length > 0) pick[f] = Str(rest, f);
                map[id] = pick;
            }
        order.Sort((x, y) =>
        {
            var c = Time(map[x], "RegisteredAtUtc").CompareTo(Time(map[y], "RegisteredAtUtc"));
            return c != 0 ? c : string.CompareOrdinal(x, y);
        });
        var start = Math.Max(0, order.Count - PlutoExperimentLedgerService.Capacity);
        var arr = new JsonArray();
        for (var i = start; i < order.Count; i++) arr.Add(map[order[i]]);
        var root = (JsonObject)a.DeepClone();
        root["Experiments"] = arr;
        return root;
    }

    private static int LedgerProgress(JsonObject e)
        => e["Evaluation"] is JsonObject ? 2 : e["ChampionMetrics"] is JsonObject || e["ChallengerMetrics"] is JsonObject ? 1 : 0;

    // ── 오답 노트 — 라벨 · 더 최근 LastUtc 가 기준 · 적중 수 최댓값 ──

    private static JsonObject MergeNotebookEntry(JsonObject cur, JsonObject other)
    {
        var pick = Time(other, "LastUtc") > Time(cur, "LastUtc") ? (JsonObject)other.DeepClone() : (JsonObject)cur.DeepClone();
        pick["Hits"] = Math.Max(Int(cur, "Hits"), Int(other, "Hits"));
        return pick;
    }

    // ── 렌더 카탈로그 — 영상 ID · 더 최근 렌더 · 학습 완료는 어느 쪽이든 유지 · 최신순 · 용량 ──

    private static JsonObject MergeCatalog(JsonObject a, JsonObject b)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var side in new[] { a, b })
            foreach (var c in Items(side, "Clips"))
            {
                var id = Str(c, "VideoId");
                if (id.Length == 0) continue;
                if (!map.TryGetValue(id, out var cur)) { map[id] = (JsonObject)c.DeepClone(); continue; }
                var pick = Time(c, "RenderedAtUtc") > Time(cur, "RenderedAtUtc") ? (JsonObject)c.DeepClone() : cur;
                if (Bool(c, "TelemetryLogged") || Bool(cur, "TelemetryLogged")) pick["TelemetryLogged"] = true;
                map[id] = pick;
            }
        var list = new List<KeyValuePair<string, JsonObject>>(map);
        list.Sort(static (x, y) =>
        {
            var c = Time(y.Value, "RenderedAtUtc").CompareTo(Time(x.Value, "RenderedAtUtc"));
            return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
        });
        var arr = new JsonArray();
        for (var i = 0; i < list.Count && i < PlutoRenderCatalogService.Capacity; i++) arr.Add(list[i].Value);
        var root = (JsonObject)a.DeepClone();
        root["Clips"] = arr;
        return root;
    }

    // ── 시리즈 아크 — 시리즈 키 → 편 번호 · 측정 > 미측정 · 더 최근 측정/렌더 · 기준선은 표본 많은 쪽 ──

    private static JsonObject MergeArc(JsonObject a, JsonObject b)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var side in new[] { a, b })
            foreach (var s in Items(side, "Series"))
            {
                var key = Str(s, "Key");
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var cur)) { map[key] = (JsonObject)s.DeepClone(); continue; }
                var newer = Time(s, "UpdatedUtc") > Time(cur, "UpdatedUtc");
                var pick = newer ? (JsonObject)s.DeepClone() : cur;
                var eps = new Dictionary<int, JsonObject>();
                foreach (var e in Items(cur, "Episodes")) eps[Int(e, "N")] = (JsonObject)e.DeepClone();
                foreach (var e in Items(s, "Episodes"))
                {
                    var n = Int(e, "N");
                    if (!eps.TryGetValue(n, out var have)) { eps[n] = (JsonObject)e.DeepClone(); continue; }
                    if (EpisodeBeats(e, have)) eps[n] = (JsonObject)e.DeepClone();
                }
                var keys = new List<int>(eps.Keys);
                keys.Sort();
                var arr = new JsonArray();
                for (var i = 0; i < keys.Count; i++) arr.Add(eps[keys[i]]);
                pick["Episodes"] = arr;
                if (Time(cur, "UpdatedUtc") > Time(pick, "UpdatedUtc")) pick["UpdatedUtc"] = cur["UpdatedUtc"]?.DeepClone();
                map[key] = pick;
            }
        var list = new List<KeyValuePair<string, JsonObject>>(map);
        list.Sort(static (x, y) =>
        {
            var c = Time(y.Value, "UpdatedUtc").CompareTo(Time(x.Value, "UpdatedUtc"));
            return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
        });
        if (list.Count > PlutoSeriesArcMemoryService.MaxSeries) list.RemoveRange(PlutoSeriesArcMemoryService.MaxSeries, list.Count - PlutoSeriesArcMemoryService.MaxSeries);
        list.Sort(static (x, y) => string.CompareOrdinal(x.Key, y.Key));   // 서비스 영속 순서와 동일(결정론)
        var series = new JsonArray();
        for (var i = 0; i < list.Count; i++) series.Add(list[i].Value);

        var root = (JsonObject)a.DeepClone();
        root["Series"] = series;
        if (a["Baselines"] is JsonObject || b["Baselines"] is JsonObject)
        {
            var bl = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var side in new[] { a, b })
                if (side["Baselines"] is JsonObject o)
                    foreach (var kv in o)
                        if (kv.Value is JsonObject v && (!bl.TryGetValue(kv.Key, out var have) || Num(v, "N") > Num(have, "N"))) bl[kv.Key] = (JsonObject)v.DeepClone();
            var keys = new List<string>(bl.Keys);
            keys.Sort(StringComparer.Ordinal);
            var obj = new JsonObject();
            for (var i = 0; i < keys.Count; i++) obj[keys[i]] = bl[keys[i]];
            root["Baselines"] = obj;
        }
        return root;
    }

    private static bool EpisodeBeats(JsonObject x, JsonObject have)
    {
        bool mx = Bool(x, "Measured"), mh = Bool(have, "Measured");
        if (mx != mh) return mx;
        return mx ? Time(x, "MeasuredUtc") > Time(have, "MeasuredUtc") : Time(x, "RenderedUtc") > Time(have, "RenderedUtc");
    }

    // ── 학습 메모리 — 키 · 더 최근 LastUtc 기준 · 누적 카운터 최댓값 ──

    private static readonly string[] TrendCounters = { "Interactions", "Adopted", "Exports", "ThumbsUp", "ThumbsDown", "BonusXp" };

    private static JsonObject MergeTrendMemory(JsonObject a, JsonObject b)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kv in a) keys.Add(kv.Key);
        foreach (var kv in b) keys.Add(kv.Key);
        var root = new JsonObject();
        foreach (var k in keys)
        {
            var x = a[k] as JsonObject;
            var y = b[k] as JsonObject;
            if (x is null || y is null) { root[k] = (x ?? y)?.DeepClone() ?? (a[k] ?? b[k])?.DeepClone(); continue; }
            var pick = Time(y, "LastUtc") > Time(x, "LastUtc") ? (JsonObject)y.DeepClone() : (JsonObject)x.DeepClone();
            foreach (var c in TrendCounters)
                if (x.ContainsKey(c) || y.ContainsKey(c)) pick[c] = Math.Max(Int(x, c), Int(y, c));
            root[k] = pick;
        }
        return root;
    }

    // ── 공용 ──

    private static JsonObject MergeKeyedObject(JsonObject a, JsonObject b, string prop, Func<JsonObject, JsonObject, JsonObject> resolve)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (a[prop] is JsonObject oa) foreach (var kv in oa) keys.Add(kv.Key);
        if (b[prop] is JsonObject ob) foreach (var kv in ob) keys.Add(kv.Key);
        var obj = new JsonObject();
        foreach (var k in keys)
        {
            var x = (a[prop] as JsonObject)?[k] as JsonObject;
            var y = (b[prop] as JsonObject)?[k] as JsonObject;
            obj[k] = x is not null && y is not null ? resolve(x, y) : (x ?? y)!.DeepClone();
        }
        var root = (JsonObject)a.DeepClone();
        root[prop] = obj;
        return root;
    }

    private static IEnumerable<JsonObject> Items(JsonObject o, string prop)
    {
        if (o[prop] is not JsonArray arr) yield break;
        foreach (var n in arr) if (n is JsonObject j) yield return j;
    }

    private static string Str(JsonObject o, string prop)
    {
        try { return o[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private static int Int(JsonObject o, string prop)
    {
        try { return o[prop] is JsonValue v && v.TryGetValue<int>(out var n) ? n : (int)Num(o, prop); }
        catch (InvalidOperationException) { return 0; }
    }

    private static double Num(JsonObject o, string prop)
    {
        try { return o[prop] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0.0; }
        catch (InvalidOperationException) { return 0.0; }
    }

    private static bool Bool(JsonObject o, string prop)
    {
        try { return o[prop] is JsonValue v && v.TryGetValue<bool>(out var x) && x; }
        catch (InvalidOperationException) { return false; }
    }

    private static DateTimeOffset Time(JsonObject o, string prop)
    {
        var s = Str(o, prop);
        return s.Length > 0 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
    }
}
