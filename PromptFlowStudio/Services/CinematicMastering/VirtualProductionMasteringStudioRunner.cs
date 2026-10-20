using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Icvfx;

namespace PromptFlowStudio.Services.CinematicMastering;

/// <summary>
/// [PromptFlow Studio v16.80.0] 🎬 AI 가상 프로덕션 &amp; 마스터링 스튜디오 러너(Genlock 레이트 루프 · <see cref="TickOnce"/> 무할당).
/// 합성 촬영(48 s 루프): 크레인/달리 카메라 → 트래커 4샘플/프레임(±0.4 mm 노이즈) → 트래킹 엔진 예측 포즈 ·
/// LED 월(12×5 m · 96×54) 씬 = 하늘(∞) · 원경 산(900 m) · 언덕(120 m) · 근경(18 m) 레이어 시차 + 3DGS 스플랫 192개(인너 프러스텀 = 추적 시점 · 바깥 = 공칭 시점) → AP0 →
/// 감정선(새벽 → 따뜻함 → 긴장 → 클라이맥스 → 해소) 신경망 룩 그레이딩 · SDR/HDR 마스터링 → 렌더 비용(실측 + 합성 부하 · 6% 스파이크) → Genlock 프레임 가드.
/// </summary>
public sealed class VirtualProductionMasteringStudioRunner : IDisposable
{
    public const int W = 96, H = 54, Splats = 192, TrackerPerFrame = 4, SceneSeconds = 48;
    public static readonly float[] LayerDepths = { float.PositiveInfinity, 900f, 120f, 18f };
    public static readonly string[] LayerNames = { "하늘 ∞", "원경 산 900 m", "언덕 120 m", "근경 18 m" };

    private readonly object _gate = new();
    private readonly RealtimeIcvfxLedWallCameraTrackingEngine _track;
    private readonly NeuralColorGradingAcesMasteringPipeline _grade;
    private readonly VirtualProductionRenderSyncGuard _sync;
    private readonly float[] _r = new float[W * H], _g = new float[W * H], _b = new float[W * H];
    private readonly Vector3[] _splatPos = new Vector3[Splats], _splatCol = new Vector3[Splats];
    private readonly float[] _splatSize = new float[Splats];
    private readonly Vector2[] _splatUv = new Vector2[Splats];
    private CancellationTokenSource? _cts;
    private ulong _rng = 0x1680_ACE5_0001UL;
    private long _frame;
    private Vector3 _truePos;

    public VirtualProductionMasteringStudioRunner(IServiceProvider sp)
        : this((RealtimeIcvfxLedWallCameraTrackingEngine?)sp.GetService(typeof(RealtimeIcvfxLedWallCameraTrackingEngine)) ?? new RealtimeIcvfxLedWallCameraTrackingEngine(),
               (NeuralColorGradingAcesMasteringPipeline?)sp.GetService(typeof(NeuralColorGradingAcesMasteringPipeline)) ?? new NeuralColorGradingAcesMasteringPipeline(),
               (VirtualProductionRenderSyncGuard?)sp.GetService(typeof(VirtualProductionRenderSyncGuard)) ?? new VirtualProductionRenderSyncGuard()) { }

    public VirtualProductionMasteringStudioRunner(RealtimeIcvfxLedWallCameraTrackingEngine track, NeuralColorGradingAcesMasteringPipeline grade, VirtualProductionRenderSyncGuard sync)
    {
        _track = track; _grade = grade; _sync = sync;
        _track.SetLayers(LayerDepths);
        _grade.Configure(W, H);
        var rnd = new Random(1680);
        for (var i = 0; i < Splats; i++)
        {
            var z = -(6f + 34f * (float)rnd.NextDouble());
            _splatPos[i] = new Vector3(-9f + 18f * (float)rnd.NextDouble(), 0.3f + 4.5f * (float)rnd.NextDouble(), z);
            var warm = (float)rnd.NextDouble();
            _splatCol[i] = new Vector3(0.9f + 0.6f * warm, 0.7f + 0.3f * warm, 0.35f) * (0.6f + 1.6f * (float)rnd.NextDouble());
            _splatSize[i] = 0.08f + 0.18f * (float)rnd.NextDouble();
        }
        Reset();
    }

    public event Action? OnChange;

    public RealtimeIcvfxLedWallCameraTrackingEngine Track => _track;
    public NeuralColorGradingAcesMasteringPipeline Grade => _grade;
    public VirtualProductionRenderSyncGuard Sync => _sync;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public float SceneTime => (float)(_frame / _sync.Rate.Fps % SceneSeconds);
    public Vector3 TruePosition => _truePos;
    public double LastRenderCostMs { get; private set; }
    public string? LastAction { get; private set; }

    public static string SectionName(float t) => t < 10f ? "새벽 · 고요" : t < 18f ? "따뜻한 전개" : t < 27f ? "긴장 고조" : t < 36f ? "클라이맥스" : "해소";

    // (t, valence, arousal, tension)
    private static readonly float[] EmotionKeys = { 0, 0.3f, 0.15f, 0.1f, 12, 0.7f, 0.4f, 0.1f, 20, -0.2f, 0.7f, 0.6f, 30, -0.6f, 0.95f, 0.95f, 38, 0.1f, 0.4f, 0.3f, 48, 0.3f, 0.15f, 0.1f };

    /// <summary>감정선 키프레임(48 s) 선형 보간.</summary>
    public static SceneEmotion EmotionAt(float t)
    {
        ReadOnlySpan<float> k = EmotionKeys;
        t = Math.Clamp(t, 0f, SceneSeconds);
        for (var i = 0; i < k.Length - 4; i += 4)
        {
            if (t > k[i + 4]) continue;
            var a = (t - k[i]) / (k[i + 4] - k[i]);
            return new SceneEmotion(k[i + 1] + (k[i + 5] - k[i + 1]) * a, k[i + 2] + (k[i + 6] - k[i + 2]) * a, k[i + 3] + (k[i + 7] - k[i + 3]) * a);
        }
        return new SceneEmotion(k[^3], k[^2], k[^1]);
    }

    /// <summary>실제 카메라 경로(크레인 + 달리 + 팬) → 위치 · 자세(카메라 −Z 전방).</summary>
    public static void CameraPath(double t, out Vector3 pos, out Quaternion rot)
    {
        var ft = (float)t;
        pos = new Vector3(2.4f * MathF.Sin(0.21f * ft), 1.55f + 0.35f * MathF.Sin(0.37f * ft + 0.5f), 5.2f + 1.1f * MathF.Sin(0.13f * ft));
        var target = new Vector3(0.8f * MathF.Sin(0.17f * ft), 1.8f, 0f);
        var f = Vector3.Normalize(target - pos);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0, u.X, u.Y, u.Z, 0, -f.X, -f.Y, -f.Z, 0, 0, 0, 0, 1);
        rot = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frame = 0;
            _track.Reset();
            _sync.ResetCounters();
        }
        LastAction = "🎬 새 테이크";
        Raise();
    }

    // ── 제어 평면 ──
    public void ConfigureSync(GenlockRate rate, double ledHz, float shutter, double readoutMs)
    {
        lock (_gate) _sync.Configure(rate, ledHz, shutter, readoutMs);
        LastAction = _sync.LastAction; Raise();
    }

    public void SetGuard(bool on) { lock (_gate) { _sync.Enabled = on; _sync.ResetCounters(); } LastAction = on ? "🛡 프레임 가드 ON" : "⚠ 프레임 가드 OFF(즉시 스왑)"; Raise(); }
    public void SetPrediction(bool on) { _track.Prediction = on; LastAction = on ? "🔮 지연 보상 예측 ON" : "🔮 지연 보상 예측 OFF"; Raise(); }
    public void SetPreset(int i) { lock (_gate) _grade.SetPreset(i); LastAction = _grade.LastAction; Raise(); }
    public void SetNeuralMix(float m) { lock (_gate) _grade.SetNeuralMix(m); Raise(); }
    public void SetTarget(MasteringTarget t) { lock (_gate) _grade.Target = t; LastAction = t == MasteringTarget.SdrRec709 ? "📺 SDR Rec.709 / BT.1886" : "🌟 HDR PQ 1000 nit / Rec.2020"; Raise(); }

    /// <summary>1프레임(무할당): 트래킹 → LED 월 렌더 → 그레이딩 → Genlock 가드.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("VpMaster.Tick");
            var t0 = Stopwatch.GetTimestamp();
            var periodTicks = TimeSpan.TicksPerSecond / _sync.Rate.Fps;
            var edge = 10_000_000L + (long)(_frame * periodTicks) + (long)((NextFloat() - 0.5f) * 300f);
            var t = _frame / _sync.Rate.Fps;

            // 1) 트래커(프레임당 4 샘플 · 노이즈)
            for (var k = 0; k < TrackerPerFrame; k++)
            {
                var ts = edge - (long)periodTicks + (long)(periodTicks * (k + 1) / TrackerPerFrame);
                CameraPath(ts / (double)TimeSpan.TicksPerSecond - 1.0, out var p, out var q);
                p += new Vector3(Noise(), Noise(), Noise()) * 0.0004f;
                q = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Noise() * 0.0003f, Noise() * 0.0003f, 0f) * q);
                _track.Push(new TrackerSample(ts, p, q));
            }
            CameraPath(t + _track.LatencyCompensationSec, out _truePos, out _);
            _track.Solve(edge);

            // 2) LED 월 렌더(ACEScg) → AP0
            RenderWall((float)(t % SceneSeconds));
            var ap1To0 = AcesColorManagementAndMoireSuppressionEngine.Ap1ToAp0;
            NeuralColorGradingAcesMasteringPipeline.MatrixPlanar(ap1To0, _r, _g, _b, _r, _g, _b);

            // 3) 감정선 → 신경망 룩 · 마스터링
            _grade.SetEmotion(EmotionAt((float)(t % SceneSeconds)));
            _grade.Process(_r, _g, _b);

            // 4) 렌더 비용(실측 + 합성 3DGS 부하 · 스파이크) → Genlock 가드
            var measured = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var cost = 26.0 + measured + (NextFloat() + NextFloat() + NextFloat() - 1.5f) * 6.0;
            if (NextFloat() < 0.06f) cost += 55.0 + 15.0 * NextFloat();
            LastRenderCostMs = cost;
            var done = edge - (long)periodTicks + (long)(cost * TimeSpan.TicksPerMillisecond);
            _sync.Tick(edge, done);
            _frame++;
        }
    }

    private void RenderWall(float t)
    {
        var wall = _track.Wall;
        var inner = _track.InnerQuad;
        var innerOk = _track.InnerValid;
        _track.ProjectToWall(_splatPos, _splatUv);
        var sun = new Vector2(0.72f - 0.004f * t, 0.6f + 0.002f * t);
        for (var y = 0; y < H; y++)
        {
            var v = 1f - (y + 0.5f) / H;
            for (var x = 0; x < W; x++)
            {
                var u = (x + 0.5f) / W;
                var tracked = innerOk && RealtimeIcvfxLedWallCameraTrackingEngine.InQuad(inner, new Vector2(u, v));
                Vector2 s0 = tracked ? _track.LayerShift(0) : default, s1 = tracked ? _track.LayerShift(1) : default, s2 = tracked ? _track.LayerShift(2) : default, s3 = tracked ? _track.LayerShift(3) : default;

                // 하늘(∞)
                float su = u - s0.X, sv = v - s0.Y;
                var c = Vector3.Lerp(new Vector3(0.95f, 0.62f, 0.38f), new Vector3(0.12f, 0.26f, 0.62f), Math.Clamp((sv - 0.3f) * 1.6f, 0f, 1f));
                var dx = (su - sun.X) * wall.WidthM / wall.HeightM; var dy = sv - sun.Y; var d2 = dx * dx + dy * dy;
                c += new Vector3(1f, 0.85f, 0.6f) * (d2 < 0.0012f ? 24f : 0.9f * MathF.Exp(-d2 * 60f));

                // 원경 산 · 언덕 · 근경(앞 레이어가 덮음)
                float u1 = u - s1.X, v1 = v - s1.Y;
                if (v1 < 0.40f + 0.06f * MathF.Sin(u1 * 9f + 1f) + 0.03f * MathF.Sin(u1 * 23f)) c = Vector3.Lerp(c, new Vector3(0.16f, 0.18f, 0.26f), 0.85f);
                float u2 = u - s2.X, v2 = v - s2.Y;
                if (v2 < 0.27f + 0.05f * MathF.Sin(u2 * 6f + 2f) + 0.02f * MathF.Sin(u2 * 17f)) c = new Vector3(0.05f, 0.11f, 0.045f) + new Vector3(0.02f) * MathF.Sin(u2 * 40f);
                float u3 = u - s3.X, v3 = v - s3.Y;
                if (v3 < 0.12f + 0.03f * MathF.Sin(u3 * 11f)) c = new Vector3(0.045f, 0.035f, 0.025f);

                var i = y * W + x;
                _r[i] = c.X; _g[i] = c.Y; _b[i] = c.Z;
            }
        }

        // 3DGS 스플랫(가우시안 · 가산)
        for (var k = 0; k < Splats; k++)
        {
            var uv = _splatUv[k];
            if (float.IsNaN(uv.X)) continue;
            var depth = Vector3.Distance(_splatPos[k], _track.EyePosition);
            var rad = MathF.Max(0.6f, _splatSize[k] / depth * 24f * W / wall.WidthM);
            var flick = 0.7f + 0.3f * MathF.Sin(t * 3f + k);
            float cx = uv.X * W, cy = (1f - uv.Y) * H;
            int x0 = Math.Max(0, (int)(cx - rad * 2)), x1 = Math.Min(W - 1, (int)(cx + rad * 2)), y0 = Math.Max(0, (int)(cy - rad * 2)), y1 = Math.Min(H - 1, (int)(cy + rad * 2));
            var inv = 1f / (2f * rad * rad);
            for (var py = y0; py <= y1; py++)
                for (var px = x0; px <= x1; px++)
                {
                    float ddx = px + 0.5f - cx, ddy = py + 0.5f - cy;
                    var wgt = MathF.Exp(-(ddx * ddx + ddy * ddy) * inv) * flick;
                    if (wgt < 0.01f) continue;
                    var i = py * W + px;
                    _r[i] += _splatCol[k].X * wgt; _g[i] += _splatCol[k].Y * wgt; _b[i] += _splatCol[k].Z * wgt;
                }
        }
    }

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    private float Noise() => (NextFloat() + NextFloat() + NextFloat() - 1.5f) * 1.4f;

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 가상 프로덕션 가동 · Genlock " + _sync.Rate.Label + " fps";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_sync.Rate.PeriodMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("VpMaster.Tick", ex); }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VpMaster.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
