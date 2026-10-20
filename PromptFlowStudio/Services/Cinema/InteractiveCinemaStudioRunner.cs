using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Metaverse;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.SpatialAudio;

namespace PromptFlowStudio.Services.Cinema;

public enum AudiencePersona : byte { ThrillSeeker = 0, Empath, Explorer }

/// <summary>
/// [PromptFlow Studio v16.78.0] 🎬 AI 인터랙티브 시네마 &amp; 서사 분기 스튜디오 러너(30 Hz · 오디오 블록 1600 · <see cref="TickOnce"/> 무할당).
/// 2 슬롯(나가는/들어오는) × [4D 스플랫 장면(노드 무드 형상 · 시간 회전/맥동) · 절차적 RGB 프레임 · 무드 화성 스코어 → v16.76 HRTF 바이노럴] →
/// 서사 엔진 커밋 시 들어오는 슬롯 장전(프리페치 적중 시 재사용) → 블렌딩 가드 크로스페이드 → 완료 시 슬롯 교체. 가상 관객(BCI · 시선 · 음성 · 투표) 시뮬레이터.
/// </summary>
public sealed class InteractiveCinemaStudioRunner : IDisposable
{
    public const int Hz = 30, SampleRate = 48000, AudioBlock = SampleRate / Hz, FrameW = 128, FrameH = 72, Splats = 240;

    private static readonly Vector3[] EmotionColor =
    {
        new(1f, 0.8f, 0.3f), new(1f, 0.4f, 0.2f), new(0.45f, 0.1f, 0.6f), new(0.2f, 0.4f, 0.9f), new(0.2f, 0.9f, 0.8f), new(0.85f, 0.75f, 1f),
    };

    private static readonly string[][] PersonaLines =
    {
        new[] { "빨리 도망쳐!", "긴장된다 두근두근", "추격 최고", "비상구로 나가!" },
        new[] { "동료를 구해줘", "눈물 나 ㅠㅠ", "함께 탈출하자", "슬퍼..." },
        new[] { "저 신호 뭐지? 궁금해", "지하로 내려가서 조사하자", "진실을 열어봐", "와 대박 우주" },
    };

    private readonly object _gate = new();
    private readonly RealtimeInteractiveNarrativeBranchingEngine _engine;
    private readonly NeuralChoiceTreeEmotionEvaluator _eval;
    private readonly ZeroSeamlessTransitionBlendingGuard _guard;
    private readonly NeuralBinauralHrtfSpatializer _hrtf = new(SampleRate);
    private readonly NeuroBioSignalEmotionParserEngine? _neuro;
    private readonly GaussianScene[] _nodeScene = new GaussianScene[RealtimeInteractiveNarrativeBranchingEngine.MaxNodes];
    private readonly GaussianScene[] _slotScene = { new(Splats), new(Splats) };
    private readonly GaussianScene _blended = new(Splats);
    private readonly byte[][] _slotFrame = { new byte[FrameW * FrameH * 3], new byte[FrameW * FrameH * 3] };
    private readonly byte[] _frame = new byte[FrameW * FrameH * 3];
    private readonly float[] _mono = new float[AudioBlock];
    private readonly float[][] _slotL = { new float[AudioBlock], new float[AudioBlock] }, _slotR = { new float[AudioBlock], new float[AudioBlock] };
    private readonly float[] _outL = new float[AudioBlock], _outR = new float[AudioBlock];
    private readonly int[] _slotNode = { -1, -1 };
    private readonly float[] _slotTime = new float[2];
    private readonly double[] _phase = new double[2 * 3];
    private readonly float[] _tmp = new float[NeuralChoiceTreeEmotionEvaluator.Dim];
    private CancellationTokenSource? _cts;
    private long _frameNo, _seenTransition, _seenPrefetch;
    private int _active;
    private ulong _rng = 0xC1_1678UL;
    private float _peak, _rms, _nextLine = 2f;
    private Vector3 _gazeDir = -Vector3.UnitZ;

    public InteractiveCinemaStudioRunner(IServiceProvider sp)
        : this((RealtimeInteractiveNarrativeBranchingEngine?)sp.GetService(typeof(RealtimeInteractiveNarrativeBranchingEngine)) ?? new RealtimeInteractiveNarrativeBranchingEngine(),
               (ZeroSeamlessTransitionBlendingGuard?)sp.GetService(typeof(ZeroSeamlessTransitionBlendingGuard)) ?? new ZeroSeamlessTransitionBlendingGuard(SampleRate),
               (NeuroBioSignalEmotionParserEngine?)sp.GetService(typeof(NeuroBioSignalEmotionParserEngine))) { }

    public InteractiveCinemaStudioRunner(RealtimeInteractiveNarrativeBranchingEngine engine, ZeroSeamlessTransitionBlendingGuard guard, NeuroBioSignalEmotionParserEngine? neuro = null)
    {
        _engine = engine; _guard = guard; _neuro = neuro; _eval = engine.Evaluator;
        _engine.Guard = guard;
        LoadDefaultStory();
    }

    public event Action? OnChange;

    public RealtimeInteractiveNarrativeBranchingEngine Engine => _engine;
    public NeuralChoiceTreeEmotionEvaluator Evaluator => _eval;
    public ZeroSeamlessTransitionBlendingGuard Guard => _guard;
    public NeuralBinauralHrtfSpatializer Hrtf => _hrtf;
    public GaussianScene Blended => _blended;
    public ReadOnlySpan<byte> Frame => _frame;
    public bool IsRunning => _cts is not null;
    public long FrameNo { get { lock (_gate) return _frameNo; } }
    public long FramesProduced { get; private set; }
    public int ActiveNode => _slotNode[_active];
    public int IncomingNode => _guard.Active ? _slotNode[1 - _active] : -1;
    public float Peak => _peak;
    public float Rms => _rms;
    public bool VirtualAudience { get; set; } = true;
    public AudiencePersona Persona { get; set; } = AudiencePersona.Explorer;
    public string? LastAction { get; private set; }

    /// <summary>제어 평면: 기본 스토리 로드 + 노드별 스플랫 장면 구축.</summary>
    public void LoadDefaultStory()
    {
        lock (_gate)
        {
            _engine.BuildDefaultStory();
            BuildScenes();
        }
        LastAction = _engine.LastAction;
        Raise();
    }

    private void BuildScenes()
    {
        for (var n = 0; n < _engine.NodeCount; n++)
        {
            var s = _nodeScene[n] ??= new GaussianScene(Splats);
            s.Clear();
            var node = _engine.Node(n);
            var col = MoodColor(node.Mood);
            for (var i = 0; i < Splats; i++)
            {
                var u = (i + 0.5f) / Splats; var ang = i * 2.39996f;
                Vector3 p = (n % 3) switch
                {
                    0 => new Vector3(MathF.Cos(ang) * 1.2f, 0.3f + u * 2.2f, MathF.Sin(ang) * 1.2f),                                // 나선 기둥
                    1 => new Vector3(MathF.Cos(ang) * (0.6f + u), 1.2f + 0.3f * MathF.Sin(ang * 3f), MathF.Sin(ang) * (0.6f + u)), // 파문 원반
                    _ => Fib(i) * 1.1f + new Vector3(0, 1.3f, 0),                                                                     // 구
                };
                var tint = col * (0.75f + 0.5f * (((uint)i * 2654435761u >> 24) / 255f));
                s.Add(p, Quaternion.Identity, new Vector3(0.05f), 0.8f, Vector3.Clamp(tint, Vector3.Zero, Vector3.One));
            }
        }
        _slotNode[0] = _engine.CurrentNode; _slotNode[1] = -1; _active = 0;
        _seenTransition = _engine.TransitionSerial; _seenPrefetch = _engine.PrefetchSerial;
        _hrtf.ResetVoice(0); _hrtf.ResetVoice(1);
    }

    private static Vector3 Fib(int i)
    {
        var y = 1f - 2f * (i + 0.5f) / Splats; var r = MathF.Sqrt(1f - y * y); var a = i * 2.39996f;
        return new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
    }

    public static Vector3 MoodColor(ReadOnlySpan<float> mood)
    {
        Vector3 c = default; float s = 0;
        for (var d = 0; d < NeuralChoiceTreeEmotionEvaluator.Dim; d++) { c += EmotionColor[d] * mood[d]; s += mood[d]; }
        return s > 1e-4f ? c / s : new Vector3(0.5f);
    }

    /// <summary>제어 평면: 관객 텍스트/음성 반응 큐잉.</summary>
    public void SubmitText(string text, bool voice)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _engine.PushText(text, voice);
        LastAction = (voice ? "🎙 " : "💬 ") + text;
        Raise();
    }

    public void Override(int choice) { _engine.Override(choice); Raise(); }

    public void SetHold(bool hold)
    {
        _engine.DirectorHold = hold;
        LastAction = hold ? "✋ 감독 홀드 — 수동 개입 대기" : "▶ 자동 분기 재개";
        Raise();
    }

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Cinema.Tick");
            const float dt = 1f / Hz;
            if (VirtualAudience) SimulateAudience(dt);
            else if (_neuro is not null && _neuro.SubjectCount > 0) _engine.PushBci(_neuro.StateAt(0));
            _engine.Tick(dt);

            // 프리페치(결정 창 예측 노드를 대기 슬롯에 장전) · 커밋 시 들어오는 슬롯 확정
            var other = 1 - _active;
            if (_engine.PrefetchSerial != _seenPrefetch)
            {
                _seenPrefetch = _engine.PrefetchSerial;
                if (!_guard.Active) LoadSlot(other, _engine.PredictedNode);
            }
            if (_engine.TransitionSerial != _seenTransition)
            {
                _seenTransition = _engine.TransitionSerial;
                if (_slotNode[other] != _engine.CurrentNode) LoadSlot(other, _engine.CurrentNode);
                _slotTime[other] = 0;
            }

            // 슬롯 렌더: 활성 + (트랜지션 중) 대기
            RenderSlot(_active, dt);
            var blending = _guard.Active;
            if (blending) RenderSlot(other, dt);
            else { Array.Clear(_slotL[other]); Array.Clear(_slotR[other]); }

            _guard.AdvanceFrame(1000f * dt);
            _guard.BlendVideo(_slotFrame[_active], _slotFrame[other], _frame);
            _guard.BlendSplats(_slotScene[_active], blending ? _slotScene[other] : _slotScene[_active], _blended);
            _guard.BlendAudio(_slotL[_active], _slotR[_active], _slotL[other], _slotR[other], _outL, _outR);
            if (_guard.ConsumeCompleted())
            {
                _active = other;
                _guard.ResetAudioRouting();
                _hrtf.ResetVoice(1 - _active);
            }
            float pk = 0; double ss = 0;
            for (var k = 0; k < AudioBlock; k++) { pk = MathF.Max(pk, MathF.Max(MathF.Abs(_outL[k]), MathF.Abs(_outR[k]))); ss += _outL[k] * _outL[k]; }
            _peak = pk; _rms = (float)Math.Sqrt(ss / AudioBlock);
            FramesProduced++;
            _frameNo++;
        }
    }

    private void LoadSlot(int slot, int node)
    {
        if (node < 0) return;
        if (_slotNode[slot] != node) { _hrtf.ResetVoice(slot); for (var k = 0; k < 3; k++) _phase[slot * 3 + k] = 0; }
        _slotNode[slot] = node;
    }

    private void RenderSlot(int slot, float dt)
    {
        var node = _slotNode[slot];
        if (node < 0) return;
        var t = _slotTime[slot] += dt;
        var mood = _engine.Node(node).Mood;
        var tension = mood[(int)CinemaEmotion.Tension];

        // 4D 스플랫: Y 회전(긴장 ↑ → 빠름) + 반경 맥동
        var src = _nodeScene[node]; var dst = _slotScene[slot];
        src.CopyTo(dst);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, t * (0.3f + 1.2f * tension));
        var pulse = 1f + 0.06f * MathF.Sin(t * (2f + 6f * tension));
        for (var i = 0; i < dst.Count; i++)
        {
            var p = src.Position(i); var c = new Vector3(0, 1.3f, 0);
            var q = Vector3.Transform(p - c, rot) * pulse + c;
            dst.Pos[i * 3] = q.X; dst.Pos[i * 3 + 1] = q.Y; dst.Pos[i * 3 + 2] = q.Z;
        }
        dst.Version++;

        // 절차적 RGB 프레임(무드 색 대각 물결)
        var col = MoodColor(mood) * 255f;
        var f = _slotFrame[slot];
        for (var y = 0; y < FrameH; y++)
            for (var x = 0; x < FrameW; x++)
            {
                var k = 0.55f + 0.45f * MathF.Sin((x + y) * 0.09f - t * (1f + 3f * tension));
                var o = (y * FrameW + x) * 3;
                f[o] = (byte)(col.X * k); f[o + 1] = (byte)(col.Y * k); f[o + 2] = (byte)(col.Z * k);
            }

        // 무드 화성(장/단 3화음) · 긴장 = 트레몰로 → HRTF(노드별 방위)
        var minor = mood[(int)CinemaEmotion.Fear] + mood[(int)CinemaEmotion.Sadness] > mood[(int)CinemaEmotion.Joy] + mood[(int)CinemaEmotion.Awe];
        var root = 110f * MathF.Pow(2f, (node % 5) / 12f);
        float f1 = root, f2 = root * (minor ? 1.1892f : 1.2599f), f3 = root * 1.4983f;
        for (var i = 0; i < AudioBlock; i++)
        {
            var b = slot * 3;
            _phase[b] += f1 / SampleRate; _phase[b + 1] += f2 / SampleRate; _phase[b + 2] += f3 / SampleRate;
            if (_phase[b] >= 1) _phase[b] -= 1; if (_phase[b + 1] >= 1) _phase[b + 1] -= 1; if (_phase[b + 2] >= 1) _phase[b + 2] -= 1;
            var trem = 1f - 0.4f * tension * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * (t + i / (float)SampleRate) * 6f));
            _mono[i] = 0.06f * trem * (MathF.Sin(2f * MathF.PI * (float)_phase[b]) + 0.7f * MathF.Sin(2f * MathF.PI * (float)_phase[b + 1]) + 0.6f * MathF.Sin(2f * MathF.PI * (float)_phase[b + 2]));
        }
        Array.Clear(_slotL[slot]); Array.Clear(_slotR[slot]);
        var az = ((node % 4) - 1.5f) * 0.6f;
        _hrtf.ProcessVoice(slot, _mono, new Vector3(MathF.Sin(az), 0, -MathF.Cos(az)), 1f, _slotL[slot], _slotR[slot]);
    }

    /// <summary>가상 관객: 페르소나 선호 무드 → BCI · 선호 선택지 응시(사카드 잡음) · 주기적 음성/텍스트 · 결정 창 투표.</summary>
    private void SimulateAudience(float dt)
    {
        var cur = _engine.CurrentNode;
        if (cur < 0) return;
        var node = _engine.Node(cur);
        // 페르소나 선호 벡터
        var pref = _tmp.AsSpan();
        pref.Clear();
        switch (Persona)
        {
            case AudiencePersona.ThrillSeeker: pref[(int)CinemaEmotion.Tension] = 1f; pref[(int)CinemaEmotion.Fear] = 0.6f; break;
            case AudiencePersona.Empath: pref[(int)CinemaEmotion.Joy] = 0.8f; pref[(int)CinemaEmotion.Sadness] = 0.7f; break;
            default: pref[(int)CinemaEmotion.Curiosity] = 1f; pref[(int)CinemaEmotion.Awe] = 0.8f; break;
        }
        // BCI: 노드 무드 반응
        float joy = node.Mood[0] + node.Mood[5], neg = node.Mood[2] + node.Mood[3];
        var st = EmotionState.Neutral with
        {
            Valence = Math.Clamp(0.5f + 0.35f * (joy - neg) + 0.05f * Noise(), 0f, 1f),
            Arousal = Math.Clamp(0.3f + 0.6f * MathF.Max(node.Mood[1], node.Mood[2]) + 0.05f * Noise(), 0f, 1f),
            Fear = Math.Clamp(node.Mood[2] * 0.8f, 0f, 1f),
            Engagement = Math.Clamp(0.5f + 0.4f * NeuralChoiceTreeEmotionEvaluator.Cosine(pref, node.Mood), 0f, 1f),
            Confidence = 0.8f,
        };
        _engine.PushBci(st);

        // 선호 선택지 = 대상 무드와 선호 벡터 코사인 최대
        var best = -1; var bs = -1f;
        for (var k = 0; k < node.OutCount; k++)
        {
            if (!_eval.IsEligible(k)) continue;
            var s = NeuralChoiceTreeEmotionEvaluator.Cosine(pref, _engine.Node(_engine.Edge(node.Out[k]).To).Mood);
            if (s > bs) { bs = s; best = k; }
        }
        var eye = new Vector3(0, 1.5f, 0);
        var target = best >= 0 ? Vector3.Normalize(_eval.Hotspot(best) - eye) : -Vector3.UnitZ;
        if (NextFloat() < 0.08f) target = Vector3.Normalize(target + new Vector3(Noise(), Noise() * 0.5f, 0) * 0.6f); // 사카드
        _gazeDir = Vector3.Normalize(Vector3.Lerp(_gazeDir, target, 0.5f));
        _engine.PushGaze(new XrGazeSample(eye, _gazeDir, 0.9f, (long)(_engine.TimeSec * 1e6f)));

        _nextLine -= dt;
        if (_nextLine <= 0)
        {
            var lines = PersonaLines[(int)Persona];
            _engine.PushText(lines[(int)(NextFloat() * lines.Length) % lines.Length], NextFloat() < 0.5f);
            _nextLine = 2.5f + 3f * NextFloat();
        }
        if (_engine.InDecisionWindow && best >= 0 && NextFloat() < 0.04f) _engine.PushVote(best);
    }

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    private float Noise() => NextFloat() * 2f - 1f;

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 인터랙티브 시네마 상영";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 상영 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Cinema.Tick", ex); }
                if ((FrameNo & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Cinema.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
