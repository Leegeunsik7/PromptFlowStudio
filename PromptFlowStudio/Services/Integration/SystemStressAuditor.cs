using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Agents;
using PromptFlowStudio.Services.Timeline;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Integration;

/// <summary>스트레스 오디트 결과(불변) — 3영역 Pass + GC/지연 지표.</summary>
public sealed record StressAuditResult(
    bool Pass, bool GatewayOk, bool C2paOk, bool SwarmOk,
    int Requests, int Denied429, long Gc0, long Gc1, long Gc2, double LatencyMs, string Message);

/// <summary>
/// [PromptFlow Studio v8.1.0] 경량 인메모리 스트레스 오디터(관찰 가능 싱글턴 · 순수 BCL · 외부 테스트 프레임워크 0).
///   ▸ 격리(로컬 new) 인스턴스로 실행해 전역 텔레메트리 카운터를 오염시키지 않는다.
///   ▸ ①게이트웨이 Token Bucket 429 방어 ②C2PA 서명/변조 무결성 ③스웜 6라운드 수렴을 1초 내 동시 검증.
///   ⚠ <see cref="OnChange"/> 는 실행 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class SystemStressAuditor
{
    private readonly DistributedRenderGrid _grid;
    public SystemStressAuditor(DistributedRenderGrid grid) => _grid = grid;

    public event Action? OnChange;
    public bool IsRunning { get; private set; }
    public StressAuditResult? Last { get; private set; }

    /// <summary>가상 요청 <paramref name="requests"/>건 동시 주입 스트레스 오디트(격리 인스턴스). 기본 300건.</summary>
    public async Task<StressAuditResult> RunAsync(int requests = 300, CancellationToken ct = default)
    {
        if (IsRunning) return Last ?? Empty();
        IsRunning = true; OnChange?.Invoke();
        var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var sw = Stopwatch.StartNew();
        var denied = 0; bool gwOk = false, c2Ok = false, swOk = false;
        try
        {
            // ① 게이트웨이 429 방어(격리 게이트웨이 · rps/burst 5 → 대량 동시 요청은 대부분 429).
            var gw = new ApiGatewayEngine(_grid);
            gw.RegisterTenant("__stress__", "__stresskey__", 5, 5);
            var allowed = 0;
            Parallel.For(0, Math.Clamp(requests, 10, 5000), _ =>
            {
                var r = gw.Authorize("__stresskey__");
                if (r.Code == 429) Interlocked.Increment(ref denied);
                else if (r.Ok) Interlocked.Increment(ref allowed);
            });
            gw.RemoveTenant("__stress__");
            gwOk = denied > 0 && allowed <= 16;   // 버스트 상한 근방만 통과

            // ② C2PA 서명/변조(격리 엔진).
            var c2 = new C2PaWatermarkEngine();
            var m = c2.SignManifest(new C2paClaim("__stress__", "seed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var okSign = c2.VerifyManifest(m);
            var tampered = m with { ClaimJson = m.ClaimJson + " " };   // 클레임 변조 → 서명 불일치
            var tamperCaught = !c2.VerifyManifest(tampered);
            c2Ok = okSign && tamperCaught;

            // ③ 스웜 6라운드 수렴(격리 오케스트레이터 + 결함 주입 타임라인).
            using var swarm = new AgentSwarmOrchestrator();
            var st = new MultiTrackTimelineState();
            st.AddClip(TimelineTrackKind.Vocal, 26, 3, "orphan", "#a855f7");    // 립싱크 미스매치
            st.AddClip(TimelineTrackKind.Gyro, 12, 1, "jitter", "#34d399");     // 자이로 급변
            await swarm.RunAutoHealAsync(st, 6, ct).ConfigureAwait(false);
            swOk = swarm.DefectsFound > 0 && swarm.DefectsResolved > 0 && !swarm.IsRunning;
        }
        catch (Exception ex) { Program.Log("StressAudit", ex); }
        finally { IsRunning = false; }

        sw.Stop();
        var pass = gwOk && c2Ok && swOk;
        Last = new StressAuditResult(pass, gwOk, c2Ok, swOk, requests, denied,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
            sw.Elapsed.TotalMilliseconds,
            $"{(pass ? "✅ PASS" : "⚠ FAIL")} · GW{(gwOk ? "○" : "×")} C2PA{(c2Ok ? "○" : "×")} Swarm{(swOk ? "○" : "×")} · {sw.Elapsed.TotalMilliseconds:0}ms");
        OnChange?.Invoke();
        return Last;
    }

    private static StressAuditResult Empty() => new(false, false, false, false, 0, 0, 0, 0, 0, 0, "미실행");
}
