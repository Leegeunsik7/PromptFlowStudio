using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>모바일 브리지 능력 비트(헤드가 결선한 네이티브 훅만 켜진다).</summary>
[Flags]
public enum MobileBridgeCapability
{
    None = 0,
    Haptics = 1 << 0,
    Share = 1 << 1,
    Battery = 1 << 2,
    KeepAwake = 1 << 3,
    SafeArea = 1 << 4,
}

/// <summary>모바일 디바이스 프로파일(불변 · 헤드가 보고 · 데스크톱은 스텁 값).</summary>
public sealed record MobileDeviceProfile(
    string Platform, string Model, string Idiom,
    int ScreenWidth, int ScreenHeight, double Density,
    double SafeTop, double SafeBottom);

/// <summary>브리지 호출 결과(불변) — 네이티브 성공/스텁 폴백을 구분한다.</summary>
public sealed record MobileBridgeResult(bool Ok, bool Native, string Message);

/// <summary>
/// [PromptFlow Studio v8.8.3] MAUI 모바일 브리지 어댑터(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · 플랫폼 타입 직접 참조 0).
///   ▸ 플랫폼 식별만 <c>#if ANDROID || IOS</c> 컴파일 상수로 격리하고, 네이티브 동작(햅틱/공유/배터리/화면유지/세이프에어리어)은
///     MAUI 헤드가 <see cref="BindHaptic"/> 등으로 결선하는 <see cref="Func{T,TResult}"/> 훅에 위임한다(제2헌장 — 서브컴포넌트 직접 주입 금지·상위 전달).
///   ▸ 훅 미결선 구간의 호출은 예외 없이 <b>BCL 스텁</b>으로 폴백하고 보류 큐에 적재 → 결선 시점에 <see cref="FlushPending"/> 로 1회 재생(멱등 · 중복 재생 없음).
///   ▸ 데스크톱(WinForms) 에서는 항상 스텁 경로로 동작하므로 동일 코드가 양 프로파일에서 컴파일·실행된다.
///   ⚠ <see cref="OnChange"/> 는 호출 스레드(플랫폼 콜백 포함)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장).
/// </summary>
public sealed class MauiMobileBridgeAdapter
{
    private const int PendingCap = 32;

    private readonly ConcurrentQueue<PendingCall> _pending = new();
    private readonly object _gate = new();

    private Func<int, Task<bool>>? _haptic;
    private Func<string, string, Task<bool>>? _share;
    private Func<(double Level, bool Charging)>? _battery;
    private Func<bool, bool>? _keepAwake;

    public MauiMobileBridgeAdapter()
    {
#if ANDROID
        PlatformLabel = "Android";
        IsMobileHead = true;
#elif IOS
        PlatformLabel = "iOS";
        IsMobileHead = true;
#else
        PlatformLabel = "Desktop(Windows)";
        IsMobileHead = false;
#endif
        Profile = new MobileDeviceProfile(PlatformLabel, "unknown", IsMobileHead ? "Phone" : "Desktop", 0, 0, 1d, 0d, 0d);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public string PlatformLabel { get; }
    public bool IsMobileHead { get; }
    public MobileDeviceProfile Profile { get; private set; }
    public MobileBridgeCapability Capabilities { get; private set; } = MobileBridgeCapability.None;
    public bool IsNativeBound => Capabilities != MobileBridgeCapability.None;
    public long BridgeCalls { get; private set; }
    public long NativeCalls { get; private set; }
    public long StubFallbacks { get; private set; }
    public long ReplayedCalls { get; private set; }
    public int PendingCount => _pending.Count;
    public double BatteryLevel { get; private set; } = -1d;
    public bool BatteryCharging { get; private set; }
    public bool KeepAwakeRequested { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>결선된 능력 라벨(HUD 칩).</summary>
    public IReadOnlyList<string> CapabilityLabels
        => Enum.GetValues<MobileBridgeCapability>()
               .Where(c => c != MobileBridgeCapability.None && Capabilities.HasFlag(c))
               .Select(c => c.ToString())
               .ToList();

    // ── 헤드 결선(MAUI 측 MauiPlatformServices 에서 호출) ──

    /// <summary>햅틱 훅 결선(ms 진동). 결선 즉시 보류 큐 재생.</summary>
    public void BindHaptic(Func<int, Task<bool>>? handler)
        => Bind(MobileBridgeCapability.Haptics, () => _haptic = handler, handler is not null, "햅틱");

    /// <summary>공유 시트 훅 결선(제목·본문/URL).</summary>
    public void BindShare(Func<string, string, Task<bool>>? handler)
        => Bind(MobileBridgeCapability.Share, () => _share = handler, handler is not null, "공유 시트");

    /// <summary>배터리 프로브 훅 결선(0~1 잔량 · 충전 여부).</summary>
    public void BindBattery(Func<(double Level, bool Charging)>? handler)
        => Bind(MobileBridgeCapability.Battery, () => _battery = handler, handler is not null, "배터리");

    /// <summary>화면 유지(Keep awake) 훅 결선.</summary>
    public void BindKeepAwake(Func<bool, bool>? handler)
        => Bind(MobileBridgeCapability.KeepAwake, () => _keepAwake = handler, handler is not null, "화면 유지");

    /// <summary>헤드가 측정한 디바이스 프로파일 보고(세이프에어리어 포함 · 멱등 갱신).</summary>
    public void ReportDeviceProfile(MobileDeviceProfile? profile)
    {
        if (profile is null) return;
        Profile = profile;
        if (profile.SafeTop > 0 || profile.SafeBottom > 0) Capabilities |= MobileBridgeCapability.SafeArea;
        LastAction = $"디바이스 프로파일 수신 — {profile.Platform}/{profile.Model} {profile.ScreenWidth}×{profile.ScreenHeight}";
        Notify();
    }

    // ── 브리지 호출(스텁 폴백 보장) ──

    /// <summary>햅틱 진동 요청 — 미결선 시 보류 큐 적재 후 스텁 성공 반환(UI 흐름 차단 없음).</summary>
    public async Task<MobileBridgeResult> HapticAsync(int milliseconds = 25)
    {
        BridgeCalls++;
        var ms = Math.Clamp(milliseconds, 5, 2000);
        var hook = _haptic;
        if (hook is null) return Fallback(MobileBridgeCapability.Haptics, $"{ms}", "햅틱 훅 미결선 — 스텁 폴백");

        try
        {
            var ok = await hook(ms).ConfigureAwait(false);
            return Native(ok, ok ? $"햅틱 {ms}ms" : "햅틱 거부(플랫폼 정책)");
        }
        catch (Exception ex)
        {
            Program.Log("MauiBridge.Haptic", ex);
            return Fallback(MobileBridgeCapability.Haptics, $"{ms}", "햅틱 실패 — 스텁 폴백");
        }
    }

    /// <summary>공유 시트 호출 — 미결선/실패 시 스텁 폴백.</summary>
    public async Task<MobileBridgeResult> ShareAsync(string title, string payload)
    {
        BridgeCalls++;
        var t = (title ?? "").Trim();
        var p = (payload ?? "").Trim();
        if (p.Length == 0) return Native(false, "공유할 내용이 비어 있습니다.");

        var hook = _share;
        if (hook is null) return Fallback(MobileBridgeCapability.Share, $"{t}\u001f{p}", "공유 훅 미결선 — 스텁 폴백");

        try
        {
            var ok = await hook(t, p).ConfigureAwait(false);
            return Native(ok, ok ? $"공유 시트 오픈 — {t}" : "공유 취소");
        }
        catch (Exception ex)
        {
            Program.Log("MauiBridge.Share", ex);
            return Fallback(MobileBridgeCapability.Share, $"{t}\u001f{p}", "공유 실패 — 스텁 폴백");
        }
    }

    /// <summary>배터리 상태 갱신 — 미결선 시 -1(미지원) 유지.</summary>
    public MobileBridgeResult RefreshBattery()
    {
        BridgeCalls++;
        var hook = _battery;
        if (hook is null) return Fallback(MobileBridgeCapability.Battery, "", "배터리 훅 미결선 — 스텁 폴백");

        try
        {
            var (level, charging) = hook();
            BatteryLevel = Math.Clamp(level, 0d, 1d);
            BatteryCharging = charging;
            return Native(true, $"배터리 {(BatteryLevel * 100):0}%{(charging ? " · 충전 중" : "")}");
        }
        catch (Exception ex)
        {
            Program.Log("MauiBridge.Battery", ex);
            return Fallback(MobileBridgeCapability.Battery, "", "배터리 프로브 실패 — 스텁 폴백");
        }
    }

    /// <summary>렌더 중 화면 꺼짐 방지 토글 — 미결선 시 요청만 기록.</summary>
    public MobileBridgeResult SetKeepAwake(bool enabled)
    {
        BridgeCalls++;
        KeepAwakeRequested = enabled;
        var hook = _keepAwake;
        if (hook is null) return Fallback(MobileBridgeCapability.KeepAwake, enabled ? "1" : "0", "화면 유지 훅 미결선 — 요청만 보존");

        try
        {
            var ok = hook(enabled);
            return Native(ok, ok ? (enabled ? "화면 유지 ON" : "화면 유지 OFF") : "화면 유지 전환 거부");
        }
        catch (Exception ex)
        {
            Program.Log("MauiBridge.KeepAwake", ex);
            return Fallback(MobileBridgeCapability.KeepAwake, enabled ? "1" : "0", "화면 유지 실패 — 스텁 폴백");
        }
    }

    /// <summary>
    /// 보류 큐 재생 — 결선된 능력의 호출만 1회 재실행하고 큐에서 제거(멱등 · 미결선 항목은 큐에 남김).
    /// </summary>
    public async Task<int> FlushPending()
    {
        if (_pending.IsEmpty) return 0;

        var carry = new List<PendingCall>();
        var replayed = 0;
        while (_pending.TryDequeue(out var call))
        {
            var bound = Capabilities.HasFlag(call.Capability);
            if (!bound) { carry.Add(call); continue; }

            switch (call.Capability)
            {
                case MobileBridgeCapability.Haptics when int.TryParse(call.Argument, out var ms):
                    await HapticAsync(ms).ConfigureAwait(false);
                    replayed++;
                    break;
                case MobileBridgeCapability.Share:
                    var parts = call.Argument.Split('\u001f');
                    await ShareAsync(parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "").ConfigureAwait(false);
                    replayed++;
                    break;
                case MobileBridgeCapability.Battery:
                    RefreshBattery();
                    replayed++;
                    break;
                case MobileBridgeCapability.KeepAwake:
                    SetKeepAwake(call.Argument == "1");
                    replayed++;
                    break;
                default:
                    carry.Add(call);
                    break;
            }
        }
        foreach (var c in carry) _pending.Enqueue(c);

        if (replayed > 0)
        {
            ReplayedCalls += replayed;
            LastAction = $"보류 브리지 호출 {replayed}건 재생";
            Notify();
        }
        return replayed;
    }

    /// <summary>보류 큐 비움(세션 종료/헤드 전환 시).</summary>
    public void ClearPending()
    {
        while (_pending.TryDequeue(out _)) { }
        LastAction = "보류 큐 비움";
        Notify();
    }

    // ── 내부 ──

    private void Bind(MobileBridgeCapability cap, Action assign, bool bound, string label)
    {
        lock (_gate)
        {
            assign();
            if (bound) Capabilities |= cap; else Capabilities &= ~cap;
        }
        LastAction = bound ? $"{label} 네이티브 훅 결선" : $"{label} 훅 해제 — 스텁 경로 복귀";
        Notify();
        if (bound) _ = FlushPending();
    }

    private MobileBridgeResult Native(bool ok, string message)
    {
        NativeCalls++;
        LastAction = message;
        Notify();
        return new MobileBridgeResult(ok, true, message);
    }

    private MobileBridgeResult Fallback(MobileBridgeCapability cap, string argument, string message)
    {
        StubFallbacks++;
        if (_pending.Count < PendingCap) _pending.Enqueue(new PendingCall(cap, argument));
        LastAction = message;
        Notify();
        return new MobileBridgeResult(true, false, message);
    }

    /// <summary>미결선 구간의 보류 호출(불변).</summary>
    private readonly record struct PendingCall(MobileBridgeCapability Capability, string Argument);
}
