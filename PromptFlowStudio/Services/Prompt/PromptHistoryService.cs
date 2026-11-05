using System;
using System.Collections.Generic;
using System.Globalization;

namespace PromptFlowStudio.Services.Prompt;

/// <summary>🕒 프롬프트 타임머신 스냅샷 — 프롬프트(카메라 모션 토큰 포함) · 카메라 라벨 · 시드 · 시드 고정.</summary>
public sealed record PromptSnapshot(string Label, string Prompt, string? Camera, long Seed, bool SeedPinned, DateTimeOffset At)
{
    /// <summary>드롭다운 표기 — <c>[09:15 원본 한글]</c>.</summary>
    public string Display => "[" + At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " " + Label + "]";
}

/// <summary>
/// 🕒 프롬프트 타임머신 — AI 변형(강화·번역·3-Way·시황 무드) 직전/입력 정지 시점의 상태를 최대 <see cref="Capacity"/>개 링 버퍼로 보관.
/// 직전 스냅샷과 내용(프롬프트·시드·고정)이 같으면 중복 적재하지 않음. 스레드 안전.
/// </summary>
public sealed class PromptHistoryService
{
    public const int Capacity = 10;
    private readonly object _lock = new();
    private readonly PromptSnapshot?[] _ring = new PromptSnapshot?[Capacity];
    private int _head;   // 다음 기록 위치
    private int _count;

    /// <summary>스냅샷 변경 통지(UI 갱신) — 구독자 예외는 격리.</summary>
    public event Action? Changed;

    public Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.Now;

    public int Count { get { lock (_lock) return _count; } }

    /// <summary>스냅샷 적재 — 빈 프롬프트·직전과 동일 상태는 무시(false). 용량 초과 시 가장 오래된 항목 제거.</summary>
    public bool Push(string label, string? prompt, string? camera, long seed, bool seedPinned)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var snap = new PromptSnapshot(string.IsNullOrWhiteSpace(label) ? "수정" : label.Trim(), prompt, camera, seed, seedPinned, Clock());
        lock (_lock)
        {
            if (_count > 0 && _ring[(_head - 1 + Capacity) % Capacity] is { } last
                && string.Equals(last.Prompt, snap.Prompt, StringComparison.Ordinal) && last.Seed == snap.Seed && last.SeedPinned == snap.SeedPinned)
                return false;
            _ring[_head] = snap;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
        Raise();
        return true;
    }

    /// <summary>최신순 목록(0 = 가장 최근).</summary>
    public IReadOnlyList<PromptSnapshot> Snapshots
    {
        get
        {
            lock (_lock)
            {
                var arr = new PromptSnapshot[_count];
                for (var i = 0; i < _count; i++) arr[i] = _ring[(_head - 1 - i + Capacity * 2) % Capacity]!;
                return arr;
            }
        }
    }

    /// <summary>최신순 인덱스의 스냅샷 반환(복원용) — 범위 밖이면 null. 히스토리는 보존(되돌린 뒤 다시 앞으로 이동 가능).</summary>
    public PromptSnapshot? Restore(int newestIndex)
    {
        lock (_lock)
        {
            if ((uint)newestIndex >= (uint)_count) return null;
            return _ring[(_head - 1 - newestIndex + Capacity * 2) % Capacity];
        }
    }

    public void Clear()
    {
        lock (_lock) { Array.Clear(_ring); _head = 0; _count = 0; }
        Raise();
    }

    private void Raise()
    {
        var h = Changed;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { Program.Log("PromptHistory.Changed", ex); }
        }
    }
}
