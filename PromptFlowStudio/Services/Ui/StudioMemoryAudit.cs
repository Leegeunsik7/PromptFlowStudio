using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Ui;

/// <summary>메모리 감사 스냅샷 — 총 활성 수·컴포넌트별 활성 수·누수 의심 목록(활성&gt;0).</summary>
public sealed record MemoryAuditSnapshot(int LiveTotal, IReadOnlyDictionary<string, int> ByComponent, IReadOnlyList<string> Suspects);

/// <summary>
/// [PromptFlowStudio] 스튜디오 제어반 메모리 누수 감사 레지스트리(v3.1.0 · 관찰 가능 싱글턴 · 순수 BCL).
///   ▸ 각 덱 컴포넌트가 OnInitialized 에서 <see cref="Track"/>, Dispose 에서 <see cref="Release"/> 를 호출 → <b>활성 인스턴스 수를 실시간 집계</b>.
///   ▸ 모달을 닫았는데 활성 수가 0으로 돌아오지 않으면 <see cref="MemoryAuditSnapshot.Suspects"/> 로 <b>구독/JSInterop 핸들 누수를 조기 탐지</b>.
///   ▸ Track/Release 는 스레드 안전(백그라운드 OnChange 마샬링 주의). 순수 카운팅이라 부작용 없음.
/// </summary>
public sealed class StudioMemoryAudit
{
    private readonly object _lock = new();
    private readonly Dictionary<long, string> _tokens = new();     // 토큰 → 컴포넌트명
    private readonly Dictionary<string, int> _live = new(StringComparer.Ordinal);
    private long _nextToken;
    private long _totalTracked;   // 세션 누적 추적 수(통계)

    /// <summary>활성 수 변경 방송.</summary>
    public event Action? OnChange;

    /// <summary>컴포넌트 생성 시 호출 — 활성 토큰 발급.</summary>
    public long Track(string component)
    {
        long token;
        lock (_lock)
        {
            token = ++_nextToken;
            _tokens[token] = component ?? "?";
            _live[component ?? "?"] = _live.GetValueOrDefault(component ?? "?") + 1;
            _totalTracked++;
        }
        OnChange?.Invoke();
        return token;
    }

    /// <summary>컴포넌트 Dispose 시 호출 — 토큰 해제(중복/미상 토큰은 무시).</summary>
    public void Release(long token)
    {
        var changed = false;
        lock (_lock)
        {
            if (_tokens.Remove(token, out var comp))
            {
                var n = _live.GetValueOrDefault(comp) - 1;
                if (n <= 0) _live.Remove(comp); else _live[comp] = n;
                changed = true;
            }
        }
        if (changed) OnChange?.Invoke();
    }

    /// <summary>현재 총 활성 수.</summary>
    public int LiveTotal { get { lock (_lock) return _tokens.Count; } }
    /// <summary>세션 누적 추적 수(통계).</summary>
    public long TotalTracked { get { lock (_lock) return _totalTracked; } }

    /// <summary>감사 스냅샷(활성&gt;0 컴포넌트를 누수 의심으로 표기).</summary>
    public MemoryAuditSnapshot Snapshot()
    {
        lock (_lock)
        {
            var by = new Dictionary<string, int>(_live);
            var suspects = by.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}×{kv.Value}").ToList();
            return new MemoryAuditSnapshot(_tokens.Count, by, suspects);
        }
    }

    /// <summary>모든 활성 토큰이 해제되었는지(모달 닫힘 후 0 이어야 정상).</summary>
    public bool IsClean { get { lock (_lock) return _tokens.Count == 0; } }
}
