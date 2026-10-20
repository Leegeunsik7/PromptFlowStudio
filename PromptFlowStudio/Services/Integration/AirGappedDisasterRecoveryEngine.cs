using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Agents;     // AgentSwarmOrchestrator
using PromptFlowStudio.Services.Timeline;   // MultiTrackTimelineState · TimelineClip · TimelineTrackKind

namespace PromptFlowStudio.Services.Integration;

/// <summary>복구 스냅샷 클립(불변 · 직렬화 DTO).</summary>
public sealed record RecoveryClip(string Id, int Kind, double StartSec, double DurationSec, string Label, string Accent, string? ThumbUrl);

/// <summary>스웜 상태 스냅샷(불변 · 감사 메타 · 카운터 복원 불가라 기록만).</summary>
public sealed record RecoverySwarmMeta(long TotalRounds, long DefectsFound, long DefectsResolved, string? LastSummary);

/// <summary>
/// 폐쇄망 복구 스냅샷(불변) — 스키마 버전·생성 시각·타임라인 전량·스웜 메타 + 무결성 해시(SHA-256).
/// </summary>
public sealed record DisasterSnapshot(
    int Schema, long TimestampMs, double TotalDurationSec, double PlayheadSec,
    IReadOnlyList<RecoveryClip> Clips, RecoverySwarmMeta Swarm, string IntegrityHash);

/// <summary>
/// [PromptFlow Studio v8.4.2] 폐쇄망(Air-Gapped) 재해 복구 &amp; 스냅샷 엔진(관찰 가능 싱글턴 · 순수 BCL <see cref="System.Text.Json"/> · 외부 NuGet 0).
///   ▸ 전체 세션(타임라인 4트랙 클립 · 총길이 · 플레이헤드)과 스웜 메타를 단일 JSON 스냅샷으로 직렬화 — 인터넷/클라우드 의존 0.
///   ▸ 스냅샷에 SHA-256 무결성 해시를 바인딩 → 복구 전 <see cref="VerifyIntegrity"/> 로 변조/손상 감지(우아한 거부).
///   ▸ <see cref="Restore"/> 는 <see cref="MultiTrackTimelineState.UpsertClip"/> 로 클립 정체성을 보존하며 원클릭 복구(대량 동기화는 마지막 1회만 방송).
///   ⚠ 스웜 카운터는 읽기 전용(복원 불가)이라 감사 메타로만 기록. 어떤 예외도 삼켜 복구가 앱을 멈추지 않는다(Graceful).
/// </summary>
public sealed class AirGappedDisasterRecoveryEngine
{
    private const int SchemaVersion = 1;
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public long SnapshotCount { get; private set; }
    public long RestoreCount { get; private set; }
    public string? LastError { get; private set; }
    public string? LastSnapshotHash { get; private set; }

    /// <summary>현재 타임라인/스웜 상태를 스냅샷 객체로 캡처(무결성 해시 바인딩).</summary>
    public DisasterSnapshot Capture(MultiTrackTimelineState timeline, AgentSwarmOrchestrator? swarm = null)
    {
        var clips = timeline.Clips
            .Select(c => new RecoveryClip(c.Id, (int)c.Kind, c.StartSec, c.DurationSec, c.Label, c.Accent, c.ThumbUrl))
            .ToList();
        var meta = new RecoverySwarmMeta(
            swarm?.TotalRounds ?? 0, swarm?.DefectsFound ?? 0, swarm?.DefectsResolved ?? 0, swarm?.LastSummary);

        // 해시는 콘텐츠 필드만으로 산출(해시 필드 제외) → 결정적.
        var body = new DisasterSnapshot(SchemaVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            timeline.TotalDurationSec, timeline.PlayheadSec, clips, meta, "");
        var hash = ComputeHash(body);
        var snap = body with { IntegrityHash = hash };
        SnapshotCount++; LastSnapshotHash = hash; Notify();
        return snap;
    }

    /// <summary>스냅샷을 JSON 문자열로 직렬화(폐쇄망 파일 저장용).</summary>
    public string Serialize(DisasterSnapshot snap) => JsonSerializer.Serialize(snap, PromptFlowJsonContext.Default.DisasterSnapshot);

    /// <summary>현재 상태를 캡처 후 즉시 JSON 직렬화(원스텝).</summary>
    public string CaptureJson(MultiTrackTimelineState timeline, AgentSwarmOrchestrator? swarm = null)
        => Serialize(Capture(timeline, swarm));

    /// <summary>JSON → 스냅샷 역직렬화(손상 시 null · Graceful).</summary>
    public DisasterSnapshot? Deserialize(string json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.DisasterSnapshot); }
        catch (Exception ex) { Program.Log("DisasterRecovery.Deserialize", ex); LastError = "스냅샷 파싱 실패"; return null; }
    }

    /// <summary>무결성 검증 — 해시 재계산(상수시간 비교). 변조/손상 시 false.</summary>
    public bool VerifyIntegrity(DisasterSnapshot? snap)
    {
        if (snap is null) { LastError = "스냅샷 없음"; return false; }
        var recomputed = ComputeHash(snap with { IntegrityHash = "" });
        var ok = FixedEquals(recomputed, snap.IntegrityHash);
        if (!ok) LastError = "무결성 해시 불일치 — 스냅샷 변조/손상";
        return ok;
    }

    /// <summary>
    /// 원클릭 복구 — 무결성 검증 통과 시 타임라인을 스냅샷 상태로 재구성한다. 반환=복구 성공 여부.
    ///   전 클립 제거 후 <see cref="MultiTrackTimelineState.UpsertClip"/> 로 정체성 보존 재적용(마지막에 1회 방송).
    /// </summary>
    public bool Restore(DisasterSnapshot? snap, MultiTrackTimelineState timeline)
    {
        if (timeline is null) return false;
        if (!VerifyIntegrity(snap)) return false;
        try
        {
            timeline.SetTotalDuration(snap!.TotalDurationSec);
            timeline.ClearAll(notify: false);
            var last = snap.Clips.Count - 1;
            for (var i = 0; i < snap.Clips.Count; i++)
            {
                var c = snap.Clips[i];
                timeline.UpsertClip(c.Id, (TimelineTrackKind)c.Kind, c.StartSec, c.DurationSec, c.Label, c.Accent, notify: i == last);
            }
            if (snap.Clips.Count == 0) timeline.ClearAll(notify: true);   // 빈 스냅샷도 방송 보장
            timeline.SetPlayhead(snap.PlayheadSec);
            RestoreCount++; LastError = null; Notify();
            return true;
        }
        catch (Exception ex) { Program.Log("DisasterRecovery.Restore", ex); LastError = "복구 중 오류"; return false; }
    }

    /// <summary>JSON 문자열에서 직접 복구(역직렬화 + 검증 + 재구성).</summary>
    public bool RestoreFromJson(string json, MultiTrackTimelineState timeline) => Restore(Deserialize(json), timeline);

    // ── 무결성 해시(BCL SHA-256) ──
    private static string ComputeHash(DisasterSnapshot body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, PromptFlowJsonContext.Default.DisasterSnapshot)))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b ?? ""));
}
