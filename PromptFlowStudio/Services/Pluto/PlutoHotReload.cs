using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// ♻️ 실행 중 재적재 계약 — 상태를 가진 싱글톤 Pluto 허브가 디스크의 영속 JSON 을 '재시작 없이' 다시 읽는다.
///   ▸ 내부 잠금 안에서 ① 진행 중 백그라운드 기록 배수(drain) ② 대기 중인 충돌 해결(<c>sync_pending/</c>) 승격 ③ 파싱 ④ 원자 교체.
///   ▸ 파싱 실패 · 파일 없음 · 읽기 불가(잠금) · 읽기 전용 세션 → false 이고 메모리 상태는 그대로(라이브 상태를 절대 비우지 않음).
/// </summary>
public interface IPlutoHotReloadable
{
    /// <summary>이 허브가 소유한 영속 파일명(예: <c>pluto_experiment_ledger.json</c>).</summary>
    string HubFileName { get; }

    /// <summary>♻️ 디스크 → 메모리 원자 재적재(성공 → true · 실패 → 라이브 상태 유지 + false). 취소만 전파.</summary>
    Task<bool> ReloadFromDiskAsync(CancellationToken ct = default);
}

/// <summary>♻️ 재적재 공용 헬퍼(순수 · 결정론) — 대기 해결 승격 · 허브 해석 · 복원 사본 형태 검증.</summary>
public static class PlutoHotReload
{
    /// <summary>
    /// 📥 대기 해결 승격 — 호출자(허브)가 자기 잠금을 쥔 상태에서 부른다. 그래야 승격과 재적재 사이에 그 허브의 기록이 끼어들어
    /// 병합 결과를 메모리 상태로 되덮는 경로가 없다. 저장소가 Drive 관리자가 아니면 무동작.
    /// </summary>
    public static bool PromotePending(IPlutoStorageManager? storage, string hubFileName)
    {
        if (storage is not PlutoDriveStorageManager dm) return false;
        try { return dm.ApplyPendingResolution(hubFileName); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.HotReload] promote: " + ex.Message);   // [부분 구제] 다음 적재에 재시도
            return false;
        }
    }

    /// <summary>♻️ DI 에서 재적재 가능한 6대 허브(등록된 것만 · 중복 제거).</summary>
    public static IReadOnlyList<IPlutoHotReloadable> ResolveHubs(IServiceProvider? sp)
    {
        var list = new List<IPlutoHotReloadable>(6);
        if (sp is null) return list;
        Add(list, sp, typeof(IPlutoExperimentLedgerService));
        Add(list, sp, typeof(IPlutoMistakeNotebookService));
        Add(list, sp, typeof(IPlutoRenderCatalogService));
        Add(list, sp, typeof(IPlutoSeriesArcMemoryService));
        Add(list, sp, typeof(PlutoLearningService));
        Add(list, sp, typeof(IPlutoMetaCalibrationService));
        return list;
    }

    private static void Add(List<IPlutoHotReloadable> list, IServiceProvider sp, Type t)
    {
        try
        {
            if (sp.GetService(t) is IPlutoHotReloadable h && !list.Contains(h)) list.Add(h);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { System.Diagnostics.Debug.WriteLine("[Pluto.HotReload] resolve: " + ex.Message); }
    }

    /// <summary>♻️ 파일명으로 허브 찾기(대소문자 무시 · 없음 → null).</summary>
    public static IPlutoHotReloadable? Find(IReadOnlyList<IPlutoHotReloadable>? hubs, string? hubFileName)
    {
        if (hubs is null || string.IsNullOrWhiteSpace(hubFileName)) return null;
        for (var i = 0; i < hubs.Count; i++)
            if (hubs[i] is { } h && string.Equals(h.HubFileName, hubFileName, StringComparison.OrdinalIgnoreCase)) return h;
        return null;
    }

    /// <summary>
    /// 🧩 복원 사본 형태 검증(순수) — 6대 허브는 자기 루트 컬렉션을 가져야 한다(<c>{}</c> · 다른 허브 파일 · 배열 루트로 학습 상태 전체를 지우는 복원 차단).
    /// 그 외 허브(형태 미정의) → 객체 루트면 통과.
    /// </summary>
    public static bool HasExpectedShape(string hubFileName, string? json)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject o) return false;
            root = o;
        }
        catch (JsonException) { return false; }
        if (Eq(hubFileName, PlutoExperimentLedgerService.FileName)) return root["Experiments"] is JsonArray;
        if (Eq(hubFileName, PlutoMistakeNotebookService.FileName)) return root["Entries"] is JsonObject;
        if (Eq(hubFileName, PlutoRenderCatalogService.FileName)) return root["Clips"] is JsonArray;
        if (Eq(hubFileName, PlutoSeriesArcMemoryService.FileName)) return root["Series"] is JsonArray;
        if (Eq(hubFileName, PlutoMetaCalibrationService.FileName)) return root["Window"] is JsonArray || root["Attributions"] is JsonObject;
        if (Eq(hubFileName, PlutoLearningService.FileName))
        {
            if (root.Count == 0) return false;                                          // 빈 사전 = 누적 XP 전체 소거
            foreach (var kv in root) if (kv.Value is not JsonObject) return false;   // 키 → 엔트리 객체 사전
            return true;
        }
        return true;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
