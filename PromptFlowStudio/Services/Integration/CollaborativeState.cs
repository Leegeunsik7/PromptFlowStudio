using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PromptFlowStudio.Services.Timeline;   // TimelineClip · TimelineTrackKind

namespace PromptFlowStudio.Services.Integration;

/// <summary>협업 델타 종류 — 클립 상향(추가/갱신) 또는 제거(툼스톤).</summary>
public enum CollabOpKind { Upsert, Remove }

/// <summary>
/// [PromptFlow Studio v8.2.0] 타임라인 협업 델타 1건(불변 · 직렬화 가능 · WebSocket 페이로드).
///   ▸ LWW(Last-Writer-Wins) 판정 키 = (<see cref="Lamport"/>, <see cref="SiteId"/>) — Lamport 시계 우선, 동률 시 SiteId 사전순.
///   ▸ 같은 델타를 몇 번 적용해도 결과가 같다(멱등) — CRDT 병합의 교환·결합·멱등 3속성을 만족.
/// </summary>
public sealed record TimelineDelta(
    string ClipId, CollabOpKind Kind, int TrackKind,
    double StartSec, double DurationSec, string Label, string Accent,
    long Lamport, string SiteId, long WallClockMs)
{
    /// <summary>이 델타가 상대 델타보다 '나중(승자)'인가 — (Lamport, SiteId) 전순서 비교.</summary>
    public bool Dominates(TimelineDelta other)
        => Lamport != other.Lamport
            ? Lamport > other.Lamport
            : string.CompareOrdinal(SiteId, other.SiteId) > 0;
}

/// <summary>
/// [PromptFlow Studio v8.2.0] 멀티유저 타임라인 CRDT — LWW-Element Map(관찰 불필요 · 순수 BCL · 외부 라이브러리 0).
///   ▸ 클립 Id 별로 마지막 승자 델타를 보관하는 Last-Writer-Wins 맵 + 툼스톤(Remove). 어떤 순서로 병합해도 같은 상태로 수렴한다(Conflict-Free).
///   ▸ <see cref="Apply"/> 는 (Lamport, SiteId) 전순서로 승패를 가려 진짜 변경이 있을 때만 true — 지연/재전송/역순 도착에도 안전(멱등).
///   ▸ 각 사이트는 <see cref="SiteId"/> 와 단조 증가 <see cref="Lamport"/> 를 가진다. 원격 델타 수신 시 시계를 max+1 로 전진(인과성 보존).
///   ⚠ 스레드 안전은 소유자(<see cref="LiveCollaborationHub"/>)의 락으로 보장한다 — 이 타입 자체는 락을 쥐지 않는다.
/// </summary>
public sealed class CollaborativeState
{
    private readonly Dictionary<string, TimelineDelta> _lww = new();   // 클립 Id → 승자 델타(Remove 툼스톤 포함)
    private long _lamport;

    public CollaborativeState(string? siteId = null)
        => SiteId = string.IsNullOrWhiteSpace(siteId) ? Guid.NewGuid().ToString("N")[..6] : siteId!.Trim();

    /// <summary>이 사이트(피어)의 식별자 — LWW 동률 타이브레이크 키.</summary>
    public string SiteId { get; }

    /// <summary>현재 Lamport 시계(관측용).</summary>
    public long Clock => System.Threading.Interlocked.Read(ref _lamport);

    /// <summary>살아있는(툼스톤 제외) 클립 수.</summary>
    public int LiveCount => _lww.Values.Count(d => d.Kind == CollabOpKind.Upsert);

    // ── 로컬 편집 → 델타 생성(Lamport 전진) ──

    /// <summary>로컬 클립 상향 델타를 만든다(Lamport++ · 즉시 자기 맵에 반영).</summary>
    public TimelineDelta LocalUpsert(TimelineClip clip)
    {
        var d = new TimelineDelta(clip.Id, CollabOpKind.Upsert, (int)clip.Kind,
            clip.StartSec, clip.DurationSec, clip.Label ?? "", clip.Accent ?? "#22d3ee",
            NextLamport(), SiteId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _lww[d.ClipId] = d;
        return d;
    }

    /// <summary>로컬 클립 제거 델타(툼스톤)를 만든다(Lamport++).</summary>
    public TimelineDelta LocalRemove(string clipId)
    {
        var d = new TimelineDelta(clipId, CollabOpKind.Remove, 0, 0, 0, "", "",
            NextLamport(), SiteId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _lww[clipId] = d;
        return d;
    }

    // ── 원격 델타 병합(CRDT 핵심) ──

    /// <summary>
    /// 원격 델타를 병합한다. 기존 승자가 없거나 새 델타가 (Lamport, SiteId)로 우세하면 채택하고 시계를 전진시킨다.
    ///   반환=상태가 실제로 바뀌었는가(=재적용/재전송이면 false). 멱등·교환·결합.
    /// </summary>
    public bool Apply(TimelineDelta incoming)
    {
        if (incoming is null || string.IsNullOrEmpty(incoming.ClipId)) return false;
        ObserveClock(incoming.Lamport);   // 인과성: 내 시계를 상대 이상으로 전진

        if (_lww.TryGetValue(incoming.ClipId, out var cur))
        {
            if (!incoming.Dominates(cur)) return false;   // 이미 더 최신 승자 보유 → 무시(LWW)
        }
        _lww[incoming.ClipId] = incoming;
        return true;
    }

    /// <summary>현재 살아있는 클립들을 상태 전송용 델타 목록으로 스냅샷(새 피어 접속 시 초기 동기화).</summary>
    public IReadOnlyList<TimelineDelta> SnapshotDeltas()
        => _lww.Values.Where(d => d.Kind == CollabOpKind.Upsert).ToList();

    /// <summary>현재 살아있는 델타를 타임라인 클립으로 물질화(수렴 상태 조회).</summary>
    public IReadOnlyList<TimelineClip> MaterializeClips()
        => _lww.Values.Where(d => d.Kind == CollabOpKind.Upsert)
            .Select(d => new TimelineClip
            {
                Id = d.ClipId,
                Kind = (TimelineTrackKind)d.TrackKind,
                StartSec = d.StartSec,
                DurationSec = d.DurationSec,
                Label = d.Label,
                Accent = string.IsNullOrWhiteSpace(d.Accent) ? "#22d3ee" : d.Accent,
            })
            .OrderBy(c => c.StartSec).ToList();

    private long NextLamport() => System.Threading.Interlocked.Increment(ref _lamport);

    /// <summary>원격 Lamport 관측 시 내 시계를 max(local, remote)+1 로 전진(단조 · 인과성).</summary>
    private void ObserveClock(long remote)
    {
        long init, next;
        do { init = System.Threading.Interlocked.Read(ref _lamport); next = Math.Max(init, remote) + 1; }
        while (System.Threading.Interlocked.CompareExchange(ref _lamport, next, init) != init);
    }

    // ── 직렬화(WebSocket 프레임) ──

    public static string Serialize(TimelineDelta d) => JsonSerializer.Serialize(d, PromptFlowWireJsonContext.Default.TimelineDelta);

    public static TimelineDelta? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json!, PromptFlowWireJsonContext.Default.TimelineDelta); }
        catch { return null; }
    }
}
