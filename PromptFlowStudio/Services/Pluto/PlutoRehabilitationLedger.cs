using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🌱 Step 29 — 유기적 복권 장부(<c>pluto_rehab_ledger.json</c> · ☁️ Drive 동반 동기화 파일 · 핵심 허브 9종 불변).
/// 복권 1건 = (조합 키, 클립 ID). 오답 노트 엔트리의 <c>RehabilitatedClipIds</c> 는 그 엔트리의 원시 벌점과 한 쌍(같은 PC 계보)이라
/// 노트 병합(최신 LastUtc 승) 시 상대 PC 의 복권 기록이 버려질 수 있다 → 이 장부는 (키, 클립) 합집합으로 수렴하고,
/// 노트는 적재 시 '장부에는 있고 자기 엔트리 목록에는 없는' 복권만 × <see cref="PlutoMistakeNotebookService.RehabilitationFactor"/> 재생한다
/// (같은 클립을 두 PC 가 각각 복권해도 결과 엔트리에는 1회만 반영 · 이중 0.35 감쇠 없음).
/// </summary>
public static class PlutoRehabilitationLedger
{
    public const string FileName = "pluto_rehab_ledger.json";
    /// <summary>장부 상한(최근 적용 순 보존) — 카탈로그 100편 × 편당 조합 키 수 여유.</summary>
    public const int Capacity = 2048;
    /// <summary>키 · 클립 ID 최대 길이(노트 토큰 상한과 동일).</summary>
    public const int MaxFieldLength = PlutoMistakeNotebookService.MaxTokenLength;

    /// <summary>복권 기록 1건.</summary>
    public sealed class Record
    {
        public string Key { get; set; } = string.Empty;
        public string ClipId { get; set; } = string.Empty;
        /// <summary>클립 렌더 시각(UTC · 미상 = default → 재생 불가 · 중복 차단에만 사용).</summary>
        public DateTime ClipRenderedUtc { get; set; }
        /// <summary>최초 적용 시각(UTC).</summary>
        public DateTime AppliedAtUtc { get; set; }
        public double SurpriseDelta { get; set; }
    }

    /// <summary>영속 스냅샷.</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<Record>? Records { get; set; }
    }

    /// <summary>장부 식별자(대소문자 무시 사전 키).</summary>
    public static string IdOf(string key, string clipId) => key + "\u001F" + clipId;

    /// <summary>필드 정리 — 빈 키/클립 → null · UTC 정규화 · 길이 절단.</summary>
    public static Record? Sanitize(Record? r)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.ClipId)) return null;
        var key = r.Key.Trim();
        var clip = r.ClipId.Trim();
        if (key.Length > MaxFieldLength) key = key[..MaxFieldLength];
        if (clip.Length > MaxFieldLength) clip = clip[..MaxFieldLength];
        return new Record
        {
            Key = key, ClipId = clip, ClipRenderedUtc = AsUtc(r.ClipRenderedUtc), AppliedAtUtc = AsUtc(r.AppliedAtUtc),
            SurpriseDelta = double.IsFinite(r.SurpriseDelta) ? r.SurpriseDelta : 0.0,
        };
    }

    private static DateTime AsUtc(DateTime t)
        => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>
    /// 🔀 합집합 병합(순수 · 결정론 · 교환/결합/멱등) — (키, 클립) 동일 → 가장 이른 적용 시각 유지 · 렌더 시각은 아는 쪽으로 채움 ·
    /// 동률은 서수 비교로 고정. 상한 초과 → 가장 오래된 적용부터 제외. 출력 순서 = 적용 시각 → 키 → 클립.
    /// </summary>
    public static Snapshot Merge(IEnumerable<Snapshot?> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        var map = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        foreach (var side in sides)
        {
            if (side?.Records is not { } recs) continue;
            foreach (var raw in recs)
            {
                if (Sanitize(raw) is not { } r) continue;
                var id = IdOf(r.Key, r.ClipId);
                if (!map.TryGetValue(id, out var cur)) { map[id] = r; continue; }
                var pick = Compare(r, cur) < 0 ? r : cur;
                var rest = ReferenceEquals(pick, r) ? cur : r;
                if (pick.ClipRenderedUtc == default && rest.ClipRenderedUtc != default) pick.ClipRenderedUtc = rest.ClipRenderedUtc;
                map[id] = pick;
            }
        }
        var list = new List<Record>(map.Values);
        list.Sort(Compare);
        if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
        return new Snapshot { Records = list };
    }

    /// <summary>결정론 순서 — 적용 시각 → 키(서수 무시 → 서수) → 클립 → 렌더 시각 → Δ.</summary>
    private static int Compare(Record x, Record y)
    {
        var c = x.AppliedAtUtc.CompareTo(y.AppliedAtUtc);
        if (c != 0) return c;
        c = string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.Compare(x.ClipId, y.ClipId, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.Key, y.Key);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.ClipId, y.ClipId);
        if (c != 0) return c;
        c = y.ClipRenderedUtc.CompareTo(x.ClipRenderedUtc);   // 아는 렌더 시각 우선
        return c != 0 ? c : y.SurpriseDelta.CompareTo(x.SurpriseDelta);
    }
}
