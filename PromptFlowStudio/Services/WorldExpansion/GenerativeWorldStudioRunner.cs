using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.SemanticMatting;

namespace PromptFlowStudio.Services.WorldExpansion;

/// <summary>
/// [PromptFlow Studio v17.27.0] 🌍 AI 생성형 월드 확장 &amp; 4D 인페인팅 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 프레임 v17.21 시네마틱 돌리 카메라(지형 추종 · 사인 선회)를 전진 → LoD 가드가 예측 궤적 반경의 타일을 해시 그리드 엔진으로 생성(예산 N)
/// → 4 s 마다 v17.26 파괴 크레이터 → G-버퍼 스플래팅(v17.11 액터 캡슐 제거 마스크) → 심도 유도 인페인팅 순으로 처리한다.
/// </summary>
public sealed class GenerativeWorldStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz, EyeHeight = 1.7f, CraterPeriod = 4f, CraterRadius = 2.5f;
    private const int BmpHeader = 54, BmpRow = GaussianDepthInpaintingSeamlessSynthesizer.W * 3;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _occS = new float[SeriesLen], _genS = new float[SeriesLen], _popS = new float[SeriesLen], _holeS = new float[SeriesLen], _seamS = new float[SeriesLen];
    private readonly byte[] _bmpColor = new byte[BmpHeader + BmpRow * GaussianDepthInpaintingSeamlessSynthesizer.H];
    private readonly byte[] _bmpMask = new byte[BmpHeader + BmpRow * GaussianDepthInpaintingSeamlessSynthesizer.H];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _t, _yaw, _camY = float.NaN, _nextCrater = CraterPeriod;
    private Vector3 _pos;

    public GenerativeWorldStudioRunner(IServiceProvider sp) : this() { }

    public GenerativeWorldStudioRunner() : this(32768) { }

    public GenerativeWorldStudioRunner(int poolCapacity)
    {
        Engine = new GenerativeNeuralWorldExpansionEngine();
        Guard = new DynamicOctreeLoDAntiPoppingGuard(poolCapacity);
        Env = new HdriEnvironment();
        Env.SetPreset(HdriPreset.Studio);
        Inpaint = new GaussianDepthInpaintingSeamlessSynthesizer(Env);
        Camera = new WorldCamera(Vector3.Zero, 0f, -0.12f, Speed, 0f, GaussianDepthInpaintingSeamlessSynthesizer.FovDeg);
        LastAction = "📋 대기 · 프롬프트 「" + Engine.Prompt + "」 → " + Engine.Style.Name + " · 풀 " + Guard.Pool.Capacity + " 가우시안(" + (Guard.Pool.Bytes / 1024) + " KB 고정)";
    }

    public event Action? OnChange;

    public GenerativeNeuralWorldExpansionEngine Engine { get; }
    public DynamicOctreeLoDAntiPoppingGuard Guard { get; }
    public GaussianDepthInpaintingSeamlessSynthesizer Inpaint { get; }
    public HdriEnvironment Env { get; }
    public WorldCamera Camera { get; private set; }
    public Vector3 ActorA { get; private set; }
    public Vector3 ActorB { get; private set; }
    public float ActorRadius { get; } = 0.35f;
    public float Speed { get; private set; } = 6f;
    public bool Craters { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public float SimTime => _t;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float OccupancySeries(int i) => _occS[Idx(i)];
    public float GenSeries(int i) => _genS[Idx(i)];
    public float AlphaStepSeries(int i) => _popS[Idx(i)];
    public float HoleSeries(int i) => _holeS[Idx(i)];
    public float SeamSeries(int i) => _seamS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void SetPrompt(string prompt) { lock (_gate) Engine.SetPrompt(prompt); LastAction = "✍ 프롬프트 「" + Engine.Prompt + "」 → 스타일 " + Engine.Style.Name + " · 시드 0x" + Engine.Seed.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + " · 전 타일 교차 디졸브 재생성"; Raise(); }
    public void SetRadius(float r) { lock (_gate) Guard.Radius = Math.Clamp(r, 16f, 240f); LastAction = "📏 월드 확장 반경 → " + F0(Guard.Radius) + " m"; Raise(); }
    public void SetSpeed(float v) { lock (_gate) { Speed = Math.Clamp(v, 0f, 30f); } LastAction = "🎥 카메라 돌리 속도 → " + F1(Speed) + " m/s"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 LoD 가드 ON(예산 바이어스 · 알파-디졸브 " + Guard.FadeFrames + " 프레임)" : "⛔ 가드 OFF(즉시 출현 · 풀 고갈 시 누락)"; Raise(); }
    public void SetPrediction(bool on) { lock (_gate) Guard.Prediction = on; LastAction = on ? "🔮 궤적 예측 생성 ON(0.5/1/1.5 s)" : "🔮 예측 OFF(현재 위치 반경만)"; Raise(); }
    public void SetFadeFrames(int f) { lock (_gate) Guard.FadeFrames = Math.Clamp(f, 0, 120); LastAction = "🌫 알파-디졸브 " + Guard.FadeFrames + " 프레임"; Raise(); }
    public void SetGenBudget(int n) { lock (_gate) Guard.GenBudget = Math.Clamp(n, 1, 64); LastAction = "⚙ 프레임당 생성 예산 " + Guard.GenBudget + " 타일"; Raise(); }
    public void SetInpaint(bool on) { lock (_gate) Inpaint.Enabled = on; LastAction = on ? "🩹 4D 인페인팅 ON" : "🩹 인페인팅 OFF(구멍 노출)"; Raise(); }
    public void SetDepthGuided(bool on) { lock (_gate) Inpaint.DepthGuided = on; LastAction = on ? "🧭 심도 유도 양방향 가중 ON" : "🧭 심도 가중 OFF(균일 푸시-풀)"; Raise(); }
    public void SetTemporal(bool on) { lock (_gate) Inpaint.Temporal = on; LastAction = on ? "⏳ 4D 링버퍼 재투영 ON" : "⏳ 시간 혼합 OFF"; Raise(); }
    public void SetRemoveActor(bool on) { lock (_gate) Inpaint.RemoveActor = on; LastAction = on ? "🧍 액터 제거(배경 인페인팅)" : "🧍 액터 표시"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) Engine.UseSimd = on; LastAction = on ? "⚡ SIMD MLP" : "🐢 스칼라 MLP(비트 동일)"; Raise(); }
    public void ResetMetrics() { lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; } LastAction = "↺ 지표 초기화"; Raise(); }

    /// <summary>1 프레임(무할당): 카메라 전진 → LoD 가드 생성 → 크레이터 → 스플래팅 → 인페인팅.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("WorldExpansion.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _t += Dt;
            const float period = 24f;
            var yawRate = 0.4f * (2f * MathF.PI / period) * MathF.Cos(2f * MathF.PI * _t / period);
            _yaw += yawRate * Dt;
            _pos += new Vector3(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw)) * Speed * Dt;
            var ground = Engine.Height(_pos.X, _pos.Z) + EyeHeight;
            _camY = float.IsNaN(_camY) ? ground : _camY + 0.1f * (ground - _camY);
            Camera = new WorldCamera(new Vector3(_pos.X, _camY, _pos.Z), _yaw, -0.12f, Speed, yawRate, GaussianDepthInpaintingSeamlessSynthesizer.FovDeg);
            var fwd = new Vector3(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw));
            var side = new Vector3(fwd.Z, 0f, -fwd.X);
            var ap = _pos + fwd * 6f + side * (1.2f * MathF.Sin(_t * 0.9f));
            var ag = Engine.Height(ap.X, ap.Z);
            ActorA = new Vector3(ap.X, ag + ActorRadius, ap.Z);
            ActorB = new Vector3(ap.X, ag + 1.8f - ActorRadius, ap.Z);

            Guard.Update(Engine, Camera);
            if (Craters && _t >= _nextCrater)
            {
                var cp = _pos + fwd * 16f - side * 3f;
                Guard.AddCrater(new Vector3(cp.X, Engine.Height(cp.X, cp.Z), cp.Z), CraterRadius);
                _nextCrater += CraterPeriod;
            }
            Inpaint.Render(Guard.Pool, Guard, Camera, ActorA, ActorB, ActorRadius, true);
            Inpaint.Inpaint();

            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _occS[h] = Guard.Occupancy * 100f; _genS[h] = Guard.GeneratedThisFrame; _popS[h] = Guard.FrameAlphaStep;
            _holeS[h] = Inpaint.HolePixels * 100f / GaussianDepthInpaintingSeamlessSynthesizer.Px; _seamS[h] = Inpaint.SeamError;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }
    }

    /// <summary>UI 미리보기 BMP(24 bit · 하향식 아님) — mask = 구멍 마스크 오버레이. 반환 배열은 재사용 버퍼.</summary>
    public byte[] EncodeBmp(bool mask)
    {
        lock (_gate)
        {
            const int w = GaussianDepthInpaintingSeamlessSynthesizer.W, h = GaussianDepthInpaintingSeamlessSynthesizer.H;
            var b = mask ? _bmpMask : _bmpColor;
            var size = b.Length;
            b[0] = (byte)'B'; b[1] = (byte)'M';
            BitConverter.TryWriteBytes(b.AsSpan(2), size);
            BitConverter.TryWriteBytes(b.AsSpan(10), BmpHeader);
            BitConverter.TryWriteBytes(b.AsSpan(14), 40);
            BitConverter.TryWriteBytes(b.AsSpan(18), w);
            BitConverter.TryWriteBytes(b.AsSpan(22), h);
            BitConverter.TryWriteBytes(b.AsSpan(26), (short)1);
            BitConverter.TryWriteBytes(b.AsSpan(28), (short)24);
            for (var y = 0; y < h; y++)
            {
                var row = BmpHeader + (h - 1 - y) * BmpRow;
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    var c = Inpaint.Color(i);
                    if (mask)
                    {
                        var g = (c.X + c.Y + c.Z) / 3f * 0.45f;
                        c = !Inpaint.IsHole(i) ? new Vector3(g) : Inpaint.IsActor(i) ? new Vector3(1f, 0.3f, 0.75f) : Inpaint.IsCrater(i) ? new Vector3(1f, 0.15f, 0.1f) : new Vector3(1f, 0.6f, 0.1f);
                    }
                    var o = row + 3 * x;
                    b[o] = (byte)(255f * MathF.Sqrt(Math.Clamp(c.Z, 0f, 1f)));
                    b[o + 1] = (byte)(255f * MathF.Sqrt(Math.Clamp(c.Y, 0f, 1f)));
                    b[o + 2] = (byte)(255f * MathF.Sqrt(Math.Clamp(c.X, 0f, 1f)));
                }
            }
            return b;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.WorldExpansion" };
            _loop.Start();
            LastAction = "▶ 월드 확장 가동 · 60 Hz · 예측 궤적 타일 생성 → 옥트리 LoD 예산 → 알파-디졸브 → 심도 유도 4D 인페인팅";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("WorldExpansion.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("WorldExpansion.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
