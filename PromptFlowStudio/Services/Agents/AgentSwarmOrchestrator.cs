using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Timeline;   // MultiTrackTimelineState · TimelineClip · TimelineTrackKind

namespace PromptFlowStudio.Services.Agents;

/// <summary>
/// [PromptFlow Studio v8.0.0] 자율형 AI 에이전트 스웜 오케스트레이터(관찰 가능 싱글턴 · 순수 BCL · <see cref="Channel{T}"/> · IDisposable).
///   ▸ Director/Audio/Editor/QC 4역할이 <see cref="AgentFeedback"/> 를 비동기 채널로 교환한다.
///   ▸ <b>QcAgent</b> 가 립싱크 미스매치·자이로 급변·프롬프트 공백을 감지 → <b>DirectorAgent</b> 가 포착해 타임라인 스냅을 자율 정제(멱등).
///   ▸ 결함이 없을 때까지(또는 최대 라운드) 순환 교정하고, 같은 타임라인은 같은 결과로 수렴한다(멱등 · 무한루프 없음).
///   ⚠ <see cref="OnChange"/> 는 채널 소비/실행 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class AgentSwarmOrchestrator : IDisposable
{
    private const int LogCap = 60;
    private const double GyroJitterSec = 1.5;   // 이보다 짧은 자이로 = 급변(카메라 튐)

    private readonly Channel<AgentFeedback> _bus = Channel.CreateUnbounded<AgentFeedback>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly object _gate = new();
    private readonly List<AgentFeedback> _log = new();

    public AgentSwarmOrchestrator() => _pump = Task.Run(() => PumpAsync(_cts.Token));

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public bool IsRunning { get; private set; }
    public long TotalRounds { get; private set; }
    public long DefectsFound { get; private set; }
    public long DefectsResolved { get; private set; }
    public string? LastSummary { get; private set; }
    public IReadOnlyList<AgentFeedback> RecentFeedback { get { lock (_gate) return _log.Take(24).ToList(); } }

    /// <summary>메시지 발행(채널 → 소비 루프가 로그 적재·방송).</summary>
    private void Publish(AgentRole role, FeedbackSeverity sev, string category, string message, string? clipId = null)
        => _bus.Writer.TryWrite(new AgentFeedback(role, sev, category, message, clipId).Stamped());

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var fb in _bus.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                lock (_gate) { _log.Insert(0, fb); if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap); }
                Notify();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("AgentSwarm.Pump", ex); }
    }

    /// <summary>
    /// 자율 교정 루프 — QC 감지 → Director/Audio/Editor 교정 → 결함 0 또는 maxRounds 까지 순환. 타임라인을 직접 정제한다.
    /// </summary>
    public async Task<string> RunAutoHealAsync(MultiTrackTimelineState state, int maxRounds = 3, CancellationToken ct = default)
    {
        if (IsRunning || state is null) return "이미 실행 중이거나 타임라인이 없습니다.";
        IsRunning = true; Notify();
        int found = 0, resolved = 0, rounds = 0;
        try
        {
            Publish(AgentRole.Director, FeedbackSeverity.Info, "swarm", "🤖 에이전트 스웜 자율 교정 시작");
            for (var r = 1; r <= Math.Clamp(maxRounds, 1, 6); r++)
            {
                ct.ThrowIfCancellationRequested();
                rounds = r;
                var defects = QcInspect(state);
                if (defects.Count == 0)
                {
                    Publish(AgentRole.Qc, FeedbackSeverity.Info, "qc-pass", $"라운드 {r}: 품질 결함 없음 — 타임라인 정합 ✓");
                    break;
                }
                found += defects.Count;
                foreach (var d in defects) Publish(d.Role, d.Severity, d.Category, d.Message, d.TargetClipId);
                foreach (var d in defects)
                    if (ApplyFix(state, d))
                    {
                        resolved++;
                        Publish(FixerFor(d.Category), FeedbackSeverity.Info, d.Category, "자율 교정 적용 · " + d.Message, d.TargetClipId);
                    }
                await Task.Delay(120, ct).ConfigureAwait(false);   // 라운드 간 UI 마샬링 여유
            }
            TotalRounds += rounds; DefectsFound += found; DefectsResolved += resolved;
            LastSummary = $"🤖 자율 교정 완료 — {rounds}라운드 · 감지 {found} · 교정 {resolved}";
        }
        catch (OperationCanceledException) { LastSummary = "🤖 자율 교정 취소됨"; }
        catch (Exception ex) { Program.Log("AgentSwarm.Run", ex); LastSummary = "🤖 자율 교정 오류: " + ex.Message; }
        finally { IsRunning = false; Notify(); }
        return LastSummary ?? "";
    }

    // ── QcAgent: 결함 감지 ──
    private static List<AgentFeedback> QcInspect(MultiTrackTimelineState state)
    {
        var defects = new List<AgentFeedback>();
        var videos = state.ClipsFor(TimelineTrackKind.Video);
        var vocals = state.ClipsFor(TimelineTrackKind.Vocal);
        var gyros = state.ClipsFor(TimelineTrackKind.Gyro);
        var prompts = state.ClipsFor(TimelineTrackKind.Prompt);

        // 립싱크 미스매치 — 보컬이 어떤 비디오 컷과도 겹치지 않음.
        foreach (var a in vocals.Where(a => !videos.Any(v => Overlaps(a, v))))
            defects.Add(new AgentFeedback(AgentRole.Qc, FeedbackSeverity.Defect, "lipsync-mismatch",
                $"보컬 '{a.Label}' 이 비디오 컷과 겹치지 않음(립싱크 미스매치)", a.Id));

        // 자이로 급변 — 자이로 클립이 너무 짧아 카메라가 튐.
        foreach (var g in gyros.Where(g => g.DurationSec < GyroJitterSec))
            defects.Add(new AgentFeedback(AgentRole.Qc, FeedbackSeverity.Defect, "gyro-jitter",
                $"자이로 '{g.Label}' 길이 {g.DurationSec:0.#}s 로 급변(카메라 튐)", g.Id));

        // 프롬프트 공백 — 비디오 컷을 덮는 프롬프트 오버라이드가 없음.
        foreach (var v in videos.Where(v => !prompts.Any(p => Overlaps(p, v))))
            defects.Add(new AgentFeedback(AgentRole.Qc, FeedbackSeverity.Defect, "prompt-gap",
                $"컷 '{v.Label}' 에 프롬프트 커버리지 없음", v.Id));

        return defects;
    }

    // ── Director/Audio/Editor: 멱등 교정 ──
    private static bool ApplyFix(MultiTrackTimelineState state, AgentFeedback d)
    {
        var clips = state.Clips;
        var target = clips.FirstOrDefault(c => c.Id == d.TargetClipId);
        switch (d.Category)
        {
            case "lipsync-mismatch":
                if (target is null) return false;
                var videos = state.ClipsFor(TimelineTrackKind.Video);
                if (videos.Count == 0) return false;
                var nearest = videos.OrderBy(v => Math.Abs(v.StartSec - target.StartSec)).First();
                state.MoveClip(target.Id, nearest.StartSec);   // 보컬을 가장 가까운 컷 시작으로 정렬(스냅)
                return true;

            case "gyro-jitter":
                if (target is null) return false;
                state.ResizeClip(target.Id, 2.0);              // 급변 자이로 최소 2초로 완화
                return true;

            case "prompt-gap":
                if (target is null) return false;
                state.AddClip(TimelineTrackKind.Prompt, target.StartSec, target.DurationSec, "cinematic, natural light", "#f59e0b");
                return true;

            default: return false;
        }
    }

    private static AgentRole FixerFor(string category) => category switch
    {
        "lipsync-mismatch" => AgentRole.Audio,
        "gyro-jitter" => AgentRole.Editor,
        "prompt-gap" => AgentRole.Director,
        _ => AgentRole.Director,
    };

    private static bool Overlaps(TimelineClip a, TimelineClip b) => a.StartSec < b.EndSec && a.EndSec > b.StartSec;

    public void Dispose()
    {
        try { _cts.Cancel(); _bus.Writer.TryComplete(); _cts.Dispose(); } catch { /* 무시 */ }
    }
}
