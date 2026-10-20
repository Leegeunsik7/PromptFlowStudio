namespace PromptFlowStudio.Services.ProductionCrew;

public enum DebateReason : byte { Agreement, Compromise, EmotionDriven, BeatAlignment, ContinuityHold, BudgetCap, ManualOverride }

/// <summary>토론 로그 항목(값 형식): 틱 · 샷 · 주제 · 제안 분산 · 승자/양보 역할 · 결정값(정규) · 감정 손실 전/후 · 사유.</summary>
public readonly record struct DebateEntry(long Tick, int Shot, CrewTopic Topic, float Spread, CrewRole Winner, CrewRole Conceder, float Value, float LossBefore, float LossAfter, DebateReason Reason);

/// <summary>
/// [PromptFlow Studio v16.91.0] ⚖ 자율 연출 충돌 해결 &amp; 컨센서스 평가기(무할당 · 결정적 · 규칙/최적화 기반).
///   ▸ 목적 함수(정규 파라미터 x ∈ [0,1]¹⁰): Σ_역할 Σ_주제 w_r · a_rk · c_rk · (x_k − p_rk)² + λ · 감정 손실(x) + 수동 잠금(하드 제약) → 후처리 박자 정렬(컷 길이 × 템포 = 최근접 짝수 박).
///   ▸ 감정 예측(선형): 각성 = 0.35 속도 + 0.3(1 − 컷) + 0.25 템포 + 0.1 VFX · 긴장 = 0.3(1 − 키) + 0.25 더치 + 0.25 음악 강도 + 0.2 헤이즈 · 정서가 = 2(0.5(1 − 색온도) + 0.3 키 + 0.2(1 − 더치)) − 1.
///   ▸ 풀이: 투영 경사 하강 60회(해석적 경사) · 주제별 충돌 = 가중 표준편차 &gt; 허용 → 토론 로그(승자 = 최근접 · 양보 = 최원거리 · 사유 분류).
///   ▸ 신뢰 가중 학습(Hedge): 역할 손실 = 결정과의 제곱 거리 평균 − 감정 기여 → w ← w^0.98 · exp(−η(ℓ − ℓ̄)) · [0.25, 3](망각 회귀) · 도메인 권한(주제 담당 역할 a_rk = 1.5 · 연출 감정 주제 1.3).
/// </summary>
public sealed class AutonomousDirectorialConsensusEvaluator
{
    public const int Agents = MultiAgentVirtualProductionCrewOrchestratorEngine.Agents, Topics = MultiAgentVirtualProductionCrewOrchestratorEngine.Topics, LogSize = 128, Iterations = 60;
    private static readonly float[,] Authority = BuildAuthority();

    private readonly object _gate = new();
    private readonly float[] _w = new float[Agents], _x = new float[Topics], _g = new float[Topics], _mean = new float[Topics], _loss = new float[Agents];
    private readonly bool[] _locked = new bool[Topics];
    private readonly float[] _lockValue = new float[Topics];
    private readonly DebateEntry[] _log = new DebateEntry[LogSize];
    private int _logHead, _logCount;
    private readonly float[] _lastLogged = new float[Topics];

    public AutonomousDirectorialConsensusEvaluator() { Array.Fill(_w, 1f); Array.Fill(_lastLogged, -1f); }

    public float Lambda { get; set; } = 2.5f;
    public float Mu { get; set; } = 0.6f;
    public float Eta { get; set; } = 0.15f;
    public float ConflictTolerance { get; set; } = 0.12f;
    public bool AdaptWeights { get; set; } = true;

    public float Weight(CrewRole r) => _w[(int)r];
    public bool IsLocked(CrewTopic t) => _locked[(int)t];
    public float LockValue(CrewTopic t) => _lockValue[(int)t];
    public int Conflicts { get; private set; }
    public long Resolutions { get; private set; }
    public long TotalConflicts { get; private set; }
    public float LastLossBefore { get; private set; }
    public float LastLossAfter { get; private set; }
    public EmotionTarget Predicted { get; private set; }
    public float BeatsPerCut { get; private set; }
    public double LastResolveMs { get; private set; }
    public int LogCount => _logCount;
    public DebateEntry Log(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];

    public static string ReasonName(DebateReason r) => r switch
    {
        DebateReason.Agreement => "합의", DebateReason.Compromise => "절충", DebateReason.EmotionDriven => "감정 목표 우선", DebateReason.BeatAlignment => "박자 정렬",
        DebateReason.ContinuityHold => "연속성 유지", DebateReason.BudgetCap => "예산 상한", _ => "수동 오버라이드",
    };

    private static float[,] BuildAuthority()
    {
        var a = new float[Agents, Topics];
        for (var r = 0; r < Agents; r++) for (var k = 0; k < Topics; k++) a[r, k] = 1f;
        void D(CrewRole r, params CrewTopic[] ks) { foreach (var k in ks) a[(int)r, (int)k] = 1.5f; }
        D(CrewRole.Cinematographer, CrewTopic.FocalLength, CrewTopic.CameraSpeed);
        D(CrewRole.Gaffer, CrewTopic.KeyIntensity, CrewTopic.ColorTemp, CrewTopic.Haze);
        D(CrewRole.SoundDesigner, CrewTopic.MusicTempo, CrewTopic.MusicIntensity);
        D(CrewRole.VfxSupervisor, CrewTopic.VfxDensity);
        D(CrewRole.Editor, CrewTopic.CutLength);
        D(CrewRole.ScriptSupervisor, CrewTopic.Dutch);
        foreach (var k in new[] { CrewTopic.KeyIntensity, CrewTopic.MusicIntensity, CrewTopic.CutLength }) a[(int)CrewRole.Director, (int)k] = MathF.Max(a[(int)CrewRole.Director, (int)k], 1.3f);
        return a;
    }

    /// <summary>1-Click 오버라이드: 주제 값(실단위) 잠금 · null = 해제.</summary>
    public void Override(CrewTopic t, float? realValue)
    {
        lock (_gate)
        {
            var k = (int)t;
            _locked[k] = realValue.HasValue;
            if (realValue is { } v) _lockValue[k] = MultiAgentVirtualProductionCrewOrchestratorEngine.Normalize(k, v);
        }
    }

    public void ClearOverrides() { lock (_gate) Array.Clear(_locked); }
    public void ResetWeights() { lock (_gate) Array.Fill(_w, 1f); }

    /// <summary>감정 예측(정규 파라미터 · 무할당).</summary>
    public static EmotionTarget Predict(ReadOnlySpan<float> x)
    {
        var a = 0.35f * x[(int)CrewTopic.CameraSpeed] + 0.3f * (1f - x[(int)CrewTopic.CutLength]) + 0.25f * x[(int)CrewTopic.MusicTempo] + 0.1f * x[(int)CrewTopic.VfxDensity];
        var t = 0.3f * (1f - x[(int)CrewTopic.KeyIntensity]) + 0.25f * x[(int)CrewTopic.Dutch] + 0.25f * x[(int)CrewTopic.MusicIntensity] + 0.2f * x[(int)CrewTopic.Haze];
        var v = 2f * (0.5f * (1f - x[(int)CrewTopic.ColorTemp]) + 0.3f * x[(int)CrewTopic.KeyIntensity] + 0.2f * (1f - x[(int)CrewTopic.Dutch])) - 1f;
        return new EmotionTarget(v, a, t);
    }

    public static float EmotionLoss(ReadOnlySpan<float> x, in EmotionTarget e)
    {
        var p = Predict(x);
        var dv = (p.Valence - e.Valence) * 0.5f; var da = p.Arousal - e.Arousal; var dt = p.Tension - e.Tension;
        return dv * dv + da * da + dt * dt;
    }

    private static float Beats(ReadOnlySpan<float> x)
    {
        var cut = MultiAgentVirtualProductionCrewOrchestratorEngine.Denormalize((int)CrewTopic.CutLength, x[(int)CrewTopic.CutLength]);
        var bpm = MultiAgentVirtualProductionCrewOrchestratorEngine.Denormalize((int)CrewTopic.MusicTempo, x[(int)CrewTopic.MusicTempo]);
        return cut * bpm / 60f;
    }

    private void AlignBeats()
    {
        const int C = (int)CrewTopic.CutLength, T = (int)CrewTopic.MusicTempo;
        var cut = MultiAgentVirtualProductionCrewOrchestratorEngine.Denormalize(C, _x[C]);
        var bpm = MultiAgentVirtualProductionCrewOrchestratorEngine.Denormalize(T, _x[T]);
        var target = MathF.Max(2f, MathF.Round(cut * bpm / 60f / 2f) * 2f);
        if (!_locked[C])
        {
            var want = MultiAgentVirtualProductionCrewOrchestratorEngine.Normalize(C, target * 60f / bpm);
            _x[C] = Math.Clamp(want, _x[C] - 0.12f, _x[C] + 0.12f);
            _x[C] = Math.Clamp(_x[C], 0f, 1f);
            cut = MultiAgentVirtualProductionCrewOrchestratorEngine.Denormalize(C, _x[C]);
        }
        if (!_locked[T]) _x[T] = Math.Clamp(MultiAgentVirtualProductionCrewOrchestratorEngine.Normalize(T, target * 60f / cut), 0f, 1f);
    }

    /// <summary>
    /// 합의(무할당): proposals/confidence/has = [역할 × 주제] · decided(in/out 정규) = 웜 스타트 · 반환 = 충돌 주제 수.
    /// </summary>
    public int Resolve(in EmotionTarget e, ReadOnlySpan<float> proposals, ReadOnlySpan<float> confidence, ReadOnlySpan<bool> has, Span<float> decided, long tick, int shot)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 가중 평균(제안 단독 해 · 손실 비교 기준)
            for (var k = 0; k < Topics; k++)
            {
                float s = 0f, sw = 0f;
                for (var r = 0; r < Agents; r++) { var i = r * Topics + k; if (!has[i]) continue; var w = _w[r] * Authority[r, k] * confidence[i]; s += w * proposals[i]; sw += w; }
                _mean[k] = sw > 0f ? s / sw : decided[k];
                _x[k] = _locked[k] ? _lockValue[k] : decided[k];
            }
            LastLossBefore = EmotionLoss(_mean, e);
            // 투영 경사 하강
            const float step = 0.08f;
            for (var it = 0; it < Iterations; it++)
            {
                for (var k = 0; k < Topics; k++)
                {
                    float g = 0f;
                    for (var r = 0; r < Agents; r++) { var i = r * Topics + k; if (!has[i]) continue; g += 2f * _w[r] * Authority[r, k] * confidence[i] * (_x[k] - proposals[i]); }
                    _g[k] = g;
                }
                // 감정 손실 경사(선형 모델 해석 미분)
                var p = Predict(_x);
                float dv = (p.Valence - e.Valence) * 0.5f, da = p.Arousal - e.Arousal, dt = p.Tension - e.Tension;
                var L = 2f * Lambda;
                _g[(int)CrewTopic.CameraSpeed] += L * da * 0.35f;
                _g[(int)CrewTopic.CutLength] += L * da * -0.3f;
                _g[(int)CrewTopic.MusicTempo] += L * da * 0.25f;
                _g[(int)CrewTopic.VfxDensity] += L * da * 0.1f;
                _g[(int)CrewTopic.KeyIntensity] += L * (dt * -0.3f + dv * 0.5f * 2f * 0.3f);
                _g[(int)CrewTopic.Dutch] += L * (dt * 0.25f + dv * 0.5f * 2f * -0.2f);
                _g[(int)CrewTopic.MusicIntensity] += L * dt * 0.25f;
                _g[(int)CrewTopic.Haze] += L * dt * 0.2f;
                _g[(int)CrewTopic.ColorTemp] += L * dv * 0.5f * 2f * -0.5f;
                for (var k = 0; k < Topics; k++)
                {
                    if (_locked[k]) { _x[k] = _lockValue[k]; continue; }
                    _x[k] = Math.Clamp(_x[k] - step * _g[k] / (1f + MathF.Abs(_g[k]) * 0.5f), 0f, 1f);
                }
            }
            // 박자 정렬(후처리): 컷 길이 × 템포 = 최근접 짝수 박 — 컷 길이 우선 보정(±0.12 정규 이내) · 초과분은 템포 보정 · 잠금 주제 불변
            if (Mu > 0f) AlignBeats();
            LastLossAfter = EmotionLoss(_x, e);
            Predicted = Predict(_x);
            BeatsPerCut = Beats(_x);
            // 충돌 진단 · 로그
            var conflicts = 0;
            for (var k = 0; k < Topics; k++)
            {
                float s = 0f, s2 = 0f, sw = 0f, near = float.MaxValue, far = -1f; int win = 0, lose = 0, n = 0;
                for (var r = 0; r < Agents; r++)
                {
                    var i = r * Topics + k; if (!has[i]) continue;
                    var w = confidence[i]; s += w * proposals[i]; s2 += w * proposals[i] * proposals[i]; sw += w; n++;
                    var d = MathF.Abs(proposals[i] - _x[k]);
                    if (d < near) { near = d; win = r; }
                    if (d > far) { far = d; lose = r; }
                }
                if (n < 2 && !_locked[k]) continue;
                var mean = sw > 0f ? s / sw : 0f;
                var spread = sw > 0f ? MathF.Sqrt(MathF.Max(0f, s2 / sw - mean * mean)) : 0f;
                var isConflict = spread > ConflictTolerance;
                if (isConflict) conflicts++;
                if ((isConflict || _locked[k]) && MathF.Abs(_lastLogged[k] - _x[k]) > 0.02f)
                {
                    var reason = _locked[k] ? DebateReason.ManualOverride
                        : k is (int)CrewTopic.CutLength or (int)CrewTopic.MusicTempo && MathF.Abs(BeatsPerCut - MathF.Round(BeatsPerCut / 2f) * 2f) < 0.25f ? DebateReason.BeatAlignment
                        : win == (int)CrewRole.ScriptSupervisor ? DebateReason.ContinuityHold
                        : win == (int)CrewRole.VfxSupervisor && k == (int)CrewTopic.VfxDensity ? DebateReason.BudgetCap
                        : MathF.Abs(_x[k] - _mean[k]) > 0.05f ? DebateReason.EmotionDriven
                        : near < 0.05f ? DebateReason.Agreement : DebateReason.Compromise;
                    _log[_logHead] = new DebateEntry(tick, shot, (CrewTopic)k, spread, (CrewRole)win, (CrewRole)lose, _x[k], LastLossBefore, LastLossAfter, reason);
                    _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
                    _lastLogged[k] = _x[k];
                }
            }
            Conflicts = conflicts; TotalConflicts += conflicts;
            // 신뢰 가중 학습(Hedge)
            if (AdaptWeights)
            {
                float mean = 0f; var na = 0;
                for (var r = 0; r < Agents; r++)
                {
                    float l = 0f; var n = 0;
                    for (var k = 0; k < Topics; k++) { var i = r * Topics + k; if (!has[i]) continue; var d = proposals[i] - _x[k]; l += d * d; n++; }
                    _loss[r] = n > 0 ? l / n : 0f; if (n > 0) { mean += _loss[r]; na++; }
                }
                mean /= Math.Max(1, na);
                // 망각(1 방향 회귀 · 2%) + Hedge 갱신 → 장기 포화 방지 · 정상 상태 w ≈ exp(−η·Δℓ/0.02)
                for (var r = 0; r < Agents; r++) _w[r] = Math.Clamp(MathF.Pow(_w[r], 0.98f) * MathF.Exp(-Eta * (_loss[r] - mean)), 0.25f, 3f);
            }
            _x.AsSpan().CopyTo(decided);
            Resolutions++;
            LastResolveMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return conflicts;
        }
    }
}
