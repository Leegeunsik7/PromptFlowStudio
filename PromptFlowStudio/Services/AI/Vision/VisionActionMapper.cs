using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Services.AI.Vision;

/// <summary>VLM 요약문에서 추출하는 방송 액션(모션 계열 + 스타일 계열).</summary>
public enum VisionAction : byte
{
    None = 0,
    // 모션 계열(WanDynamicMotionController)
    MotionSpike = 1, SpeedRamp = 2, Celebration = 3, GameOver = 4,
    // 스타일 계열(LoRAHotSwapManager 무중단 스왑)
    StyleNoir = 5, StyleCyberpunk = 6, StyleFantasy = 7,
}

/// <summary>요약문 1건의 파싱 결과(값 형식 · 할당 0). 모션 1개 + 스타일 1개까지 동시 검출.</summary>
public readonly record struct VisionActionMatch(VisionAction Motion, VisionAction Style, int MotionKeywordIndex, int StyleKeywordIndex)
{
    public bool Any => Motion != VisionAction.None || Style != VisionAction.None;
}

/// <summary>디스패치 1회 결과(값 형식).</summary>
public readonly record struct VisionActionDispatch(VisionActionMatch Match, bool MotionInjected, bool StyleSwapped, bool MotionSuppressed, bool StyleSuppressed, bool MotionDeferredToSceneBinder, long AtMs);

/// <summary>
/// [PromptFlow Studio v13.3.0] 비전 액션 매퍼 — VLM 요약문(자연어)을 <see cref="ReadOnlySpan{T}"/> 키워드 매칭으로 해석해
///   모션 스파이크(<see cref="WanDynamicMotionController"/>)와 스타일 LoRA 무중단 스왑(<see cref="LoRAHotSwapManager"/>)으로 변환한다.
///   ▸ Zero-Alloc 파싱: 키워드 표는 정적 배열, 매칭은 <see cref="MemoryExtensions.IndexOf(ReadOnlySpan{char}, ReadOnlySpan{char}, StringComparison)"/>
///     (OrdinalIgnoreCase) 만 사용 - Split/Regex/ToLower 없음. 결과는 값 형식(<see cref="VisionActionMatch"/>).
///   ▸ 원자적 쿨다운: 액션별 마지막 발동 시각(ms)을 long 슬롯에 두고 CAS 로 선점 - 동시 호출에서도 창(기본 3초)당 정확히 1회.
///   ▸ 역할 분리: 장면 분류값(VisionScene) 기반 파티클/줌/롤은 기존 <see cref="VisionReactionBinder"/> 가 담당한다. 같은 VLM 응답의
///     장면이 이미 모션을 받은 ExplosionFlash/HighAction 이면 이 매퍼는 모션을 생략(이중 주입 방지)하고 스타일만 처리한다.
///   ▸ UI 비차단: 파싱·모션은 동기 경량, LoRA 스왑만 비동기이며 전 구간 ConfigureAwait(false) - 호출 스레드 컨텍스트로 복귀하지 않는다.
/// </summary>
public sealed class VisionActionMapper
{
    public const int DefaultCooldownMs = 3000;
    public const double StyleLoraStrength = 0.7;

    // 우선순위 = 배열 순서(앞일수록 우선). 영문(VLM 지시문 언어) + 한글 동의어.
    private static readonly (VisionAction Action, string[] Keywords)[] MotionTable =
    {
        (VisionAction.MotionSpike, new[] { "explosion", "explode", "combat", "battle", "fight", "crash", "blast", "폭발", "전투", "격투", "충돌" }),
        (VisionAction.GameOver,    new[] { "game over", "defeat", "you died", "wasted", "게임 오버", "패배" }),
        (VisionAction.Celebration, new[] { "celebration", "celebrat", "victory", "confetti", "fireworks", "winner", "축하", "승리", "불꽃놀이" }),
        (VisionAction.SpeedRamp,   new[] { "fast motion", "high speed", "racing", "chase", "speeding", "빠른", "질주", "추격" }),
    };
    private static readonly (VisionAction Action, string[] Keywords)[] StyleTable =
    {
        // 구체적 스타일을 일반적 분위기보다 먼저 - "dark cyberpunk alley" 는 cyberpunk
        (VisionAction.StyleCyberpunk, new[] { "cyberpunk", "neon", "사이버펑크", "네온" }),
        (VisionAction.StyleFantasy,   new[] { "fantasy", "magic", "dragon", "castle", "판타지", "마법" }),
        (VisionAction.StyleNoir,      new[] { "dark", "night", "noir", "shadow", "어두운", "어둠" }),
    };

    /// <summary>스타일 액션 → LoRA 가중치 ID.</summary>
    internal static string? StyleLoraId(VisionAction a) => a switch
    {
        VisionAction.StyleNoir => "style-noir",
        VisionAction.StyleCyberpunk => "style-cyberpunk",
        VisionAction.StyleFantasy => "style-fantasy",
        _ => null,
    };

    private readonly WanDynamicMotionController _motion;
    private readonly LoRAHotSwapManager _lora;
    private readonly Func<long> _nowMs;
    private readonly int _cooldownMs;
    private readonly long[] _lastFiredMs = new long[8];
    private readonly SemaphoreSlim _styleGate = new(1, 1);   // 스타일 스왑 직렬화(Unmerge→Merge 사이 끼어들기 방지)
    private string? _activeStyleLora;
    private long _dispatches, _motionInjected, _styleSwaps, _suppressed, _deferred;
    private readonly object _gate = new();
    private VisionActionDispatch _last;
    private int _hasLast;

    public event Action? OnChange;

    public VisionActionMapper(WanDynamicMotionController motion, LoRAHotSwapManager lora)
        : this(motion, lora, () => Environment.TickCount64, DefaultCooldownMs) { }

    internal VisionActionMapper(WanDynamicMotionController motion, LoRAHotSwapManager lora, Func<long> nowMs, int cooldownMs)
    {
        _motion = motion;
        _lora = lora;
        _nowMs = nowMs;
        _cooldownMs = Math.Max(0, cooldownMs);
        for (var i = 0; i < _lastFiredMs.Length; i++) _lastFiredMs[i] = long.MinValue / 2;
    }

    public int CooldownMs => _cooldownMs;
    public long Dispatches => Interlocked.Read(ref _dispatches);
    public long MotionInjected => Interlocked.Read(ref _motionInjected);
    public long StyleSwaps => Interlocked.Read(ref _styleSwaps);
    public long SuppressedByCooldown => Interlocked.Read(ref _suppressed);
    public long MotionDeferredToSceneBinder => Interlocked.Read(ref _deferred);
    public string? ActiveStyleLora => Volatile.Read(ref _activeStyleLora);
    public VisionActionDispatch? LastDispatch { get { lock (_gate) return _hasLast != 0 ? _last : null; } }

    /// <summary>쿨다운 잔여(ms). 0 이면 즉시 발동 가능.</summary>
    public long CooldownRemainingMs(VisionAction a)
    {
        var left = Interlocked.Read(ref _lastFiredMs[(int)a]) + _cooldownMs - _nowMs();
        return left > 0 ? left : 0;
    }

    public static string Label(VisionAction a) => a switch
    {
        VisionAction.MotionSpike => "💥 모션 스파이크",
        VisionAction.SpeedRamp => "🏎 스피드 램프",
        VisionAction.Celebration => "🎉 축하 펄스",
        VisionAction.GameOver => "☠ 게임 오버 줌아웃",
        VisionAction.StyleNoir => "🌑 누아르 스타일",
        VisionAction.StyleCyberpunk => "🌆 사이버펑크 스타일",
        VisionAction.StyleFantasy => "🏰 판타지 스타일",
        _ => "-",
    };

    /// <summary>요약문 파싱(할당 0). 모션·스타일 각 표에서 우선순위가 가장 높은 1개씩.</summary>
    public static VisionActionMatch Parse(ReadOnlySpan<char> text)
    {
        var (m, mi) = FirstMatch(text, MotionTable);
        var (s, si) = FirstMatch(text, StyleTable);
        return new VisionActionMatch(m, s, mi, si);
    }

    private static (VisionAction, int) FirstMatch(ReadOnlySpan<char> text, (VisionAction Action, string[] Keywords)[] table)
    {
        if (text.IsEmpty) return (VisionAction.None, -1);
        for (var t = 0; t < table.Length; t++)
        {
            var kws = table[t].Keywords;
            for (var k = 0; k < kws.Length; k++)
                if (text.IndexOf(kws[k].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return (table[t].Action, k);
        }
        return (VisionAction.None, -1);
    }

    /// <summary>원자적 쿨다운 선점 - 창 안이면 false, 아니면 CAS 로 발동 시각을 기록하고 true(동시 호출 중 1개만 성공).</summary>
    internal bool TryEnter(VisionAction a)
    {
        ref var slot = ref _lastFiredMs[(int)a];
        while (true)
        {
            var now = _nowMs();
            var last = Interlocked.Read(ref slot);
            if (now - last < _cooldownMs) return false;
            if (Interlocked.CompareExchange(ref slot, now, last) == last) return true;
        }
    }

    /// <summary>VLM 응답 1건을 액션으로 변환·실행. 예외는 밖으로 내보내지 않는다(분석기에서 fire-and-forget 호출).</summary>
    public async Task<VisionActionDispatch> DispatchActionAsync(string? summary, VisionScene scene, CancellationToken ct = default)
    {
        var match = Parse(summary.AsSpan());
        bool motionDone = false, styleDone = false, motionSup = false, styleSup = false, deferred = false;
        if (match.Any) Interlocked.Increment(ref _dispatches);

        if (match.Motion != VisionAction.None)
        {
            if (scene is VisionScene.ExplosionFlash or VisionScene.HighAction) { deferred = true; Interlocked.Increment(ref _deferred); }
            else if (!TryEnter(match.Motion)) { motionSup = true; Interlocked.Increment(ref _suppressed); }
            else
            {
                try { ApplyMotion(match.Motion); motionDone = true; Interlocked.Increment(ref _motionInjected); }
                catch (Exception ex) { Program.Log("VisionActionMapper.Motion", ex); }
            }
        }

        if (match.Style != VisionAction.None)
        {
            if (!TryEnter(match.Style)) { styleSup = true; Interlocked.Increment(ref _suppressed); }
            else
            {
                try { styleDone = await SwapStyleAsync(StyleLoraId(match.Style)!, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 종료 */ }
                catch (Exception ex) { Program.Log("VisionActionMapper.Style", ex); }
            }
        }

        var result = new VisionActionDispatch(match, motionDone, styleDone, motionSup, styleSup, deferred, _nowMs());
        if (match.Any)
        {
            lock (_gate) { _last = result; _hasLast = 1; }
            OnChange?.Invoke();
        }
        return result;
    }

    private void ApplyMotion(VisionAction a)
    {
        switch (a)
        {
            case VisionAction.MotionSpike: _motion.SetZoom(ZoomDirection.In, 0.9); _motion.SetRoll(8); break;          // 줌인 + 셰이크
            case VisionAction.SpeedRamp: _motion.SetPan(PanDirection.Right, 0.8); break;
            case VisionAction.Celebration: _motion.SetZoom(ZoomDirection.In, 0.4); _motion.SetPan(PanDirection.Up, 0.3); break;
            case VisionAction.GameOver: _motion.SetZoom(ZoomDirection.Out, 0.6); break;                                    // 느린 줌아웃
        }
        _motion.InjectNext();
    }

    // 이전 스타일 LoRA 해제 → 새 스타일 병합(각각 LoRAHotSwapManager 의 짧은 Pause 창 안에서 수행 - 샘플링 루프 무중단).
    private async Task<bool> SwapStyleAsync(string loraId, CancellationToken ct)
    {
        await _styleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prev = Volatile.Read(ref _activeStyleLora);
            if (string.Equals(prev, loraId, StringComparison.Ordinal)) return false;   // 이미 적용 중
            if (prev is not null) await _lora.UnmergeAsync(prev, ct).ConfigureAwait(false);
            var r = await _lora.MergeAsync(loraId, StyleLoraStrength, ct).ConfigureAwait(false);
            if (!r.Ok) return false;
            Volatile.Write(ref _activeStyleLora, loraId);
            Interlocked.Increment(ref _styleSwaps);
            return true;
        }
        finally { _styleGate.Release(); }
    }
}
