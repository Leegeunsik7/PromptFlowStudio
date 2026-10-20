using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.DexterousManipulation;

/// <summary>
/// [PromptFlow Studio v17.36.0] 🤲 AI 양손 정밀 조작 &amp; 소품 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 인계/운반 합성기 → 손 슬롯별 덱스그랩 합성(템플릿 · 순차 접촉 닫힘 · 힘 폐쇄) → 폐쇄 체인 팔 IK → 마찰 원뿔 가드(파지력 · 미끄럼 · 관통) → 최종 손 자세 순으로 처리한다.
/// 외력 충격 주입(0.1 s 펄스) · 가드 토글 비교 · UI 스냅숏(잠금 복사).
/// </summary>
public sealed class DexterousManipulationStudioRunner : IDisposable
{
    public const int Hz = 60, Slots = RealtimeDexterousGraspSynthesisEngine.MaxSlots, Pts = RealtimeDexterousGraspSynthesisEngine.Pts, MaxC = RealtimeDexterousGraspSynthesisEngine.MaxContacts;
    public const float Dt = 1f / Hz, ImpulseN = 40f, ImpulseSeconds = 0.1f;

    private readonly object _gate = new();
    private readonly Vector3[] _pts = new Vector3[Slots * Pts], _cp = new Vector3[Slots * MaxC], _cn = new Vector3[Slots * MaxC], _arm = new Vector3[Slots * 3];
    private readonly float[] _cg = new float[Slots * MaxC];
    private readonly int[] _nc = new int[Slots];
    private readonly bool[] _act = new bool[Slots];
    private readonly float[] _msS = new float[MultiActorPropHandoverSynthesizer.Series];
    private Vector3 _propPos, _impulseDir = Vector3.UnitX;
    private Quaternion _propRot = Quaternion.Identity;
    private float _impulseT;
    private int _impulses, _msHead;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public DexterousManipulationStudioRunner(IServiceProvider sp) : this() { }

    public DexterousManipulationStudioRunner()
    {
        Prop = new DexProp(DexPropKind.Cylinder);
        Handover.Begin(ManipulationMode.Handover);
        LastAction = "📋 대기 · " + Prop.Name + " 인계";
        Snapshot();
    }

    public event Action? OnChange;

    public DexProp Prop { get; }
    public RealtimeDexterousGraspSynthesisEngine Engine { get; } = new();
    public MultiActorPropHandoverSynthesizer Handover { get; } = new();
    public FrictionConeSlipPreventionGuard Guard { get; } = new();
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public bool ImpulseActive => _impulseT > 0f;
    public int ViewSlot { get; set; }
    public float TickMsSeries(int i) => _msS[(_msHead - 1 - i + _msS.Length * 2) % _msS.Length];

    // ══════════════════════ UI 스냅숏 ══════════════════════

    public void CopyHand(int slot, Span<Vector3> dst) { lock (_gate) _pts.AsSpan(slot * Pts, Pts).CopyTo(dst); }
    public void CopyArm(int slot, Span<Vector3> dst) { lock (_gate) _arm.AsSpan(slot * 3, 3).CopyTo(dst); }
    public int CopyContacts(int slot, Span<Vector3> p, Span<Vector3> n, Span<float> grip)
    {
        lock (_gate)
        {
            var c = _nc[slot];
            _cp.AsSpan(slot * MaxC, c).CopyTo(p); _cn.AsSpan(slot * MaxC, c).CopyTo(n); _cg.AsSpan(slot * MaxC, c).CopyTo(grip);
            return c;
        }
    }
    public bool SlotActive(int slot) { lock (_gate) return _act[slot]; }
    public void PropPose(out Vector3 pos, out Quaternion rot) { lock (_gate) { pos = _propPos; rot = _propRot; } }

    // ══════════════════════ 제어 ══════════════════════

    public void SetProp(DexPropKind k)
    {
        lock (_gate)
        {
            Prop.Set(k);
            Handover.Begin(k == DexPropKind.Crate ? ManipulationMode.Carry : ManipulationMode.Handover);
            for (var s = 0; s < Slots; s++) { Guard.ResetSlot(s); Engine.SetSlipOffset(s, Vector3.Zero); }
        }
        LastAction = "🧩 소품 → " + Prop.Name + (k == DexPropKind.Crate ? " · 협동 운반(4 손 폐쇄 체인)" : " · 인계");
        Raise();
    }

    public void SetSlipGuard(bool on) { lock (_gate) Guard.SlipGuard = on; LastAction = on ? "🧲 마찰 원뿔 파지력 증폭 ON" : "🧲 파지력 증폭 OFF(기저 파지력 고정 → 충격 미끄럼 비교)"; Raise(); }
    public void SetPenetrationGuard(bool on) { lock (_gate) Guard.PenetrationGuard = on; LastAction = on ? "🛡 관통 스프링-댐퍼 ON" : "🛡 관통 방어 OFF(압착 굴곡 관통 비교)"; Raise(); }

    /// <summary>외력 충격(0.1 s · 40 N · 방향 순환).</summary>
    public void InjectImpulse()
    {
        lock (_gate)
        {
            _impulses++;
            var a = _impulses * 2.39996f;
            _impulseDir = Vector3.Normalize(new Vector3(MathF.Cos(a), -0.8f, MathF.Sin(a)));
            _impulseT = ImpulseSeconds;
        }
        LastAction = "💥 외력 충격 " + ImpulseN.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N × " + (ImpulseSeconds * 1000f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        Raise();
    }

    public void ResetMetrics()
    {
        lock (_gate) { Engine.ResetMetrics(); Handover.ResetMetrics(); Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("DexManipulation.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            var imp = _impulseT > 0f ? _impulseDir * ImpulseN : Vector3.Zero;
            _impulseT = MathF.Max(0f, _impulseT - Dt);
            Handover.Step(Prop, Engine, Guard, imp, Dt);
            for (var s = 0; s < Slots; s++)
            {
                if (!Engine.Active(s)) continue;
                Guard.Process(Engine, s, Prop, Handover.Load(s), Handover.StaticLoad(s), Dt);
                if (Handover.Holding(s) && Guard.Ejected(s) && Handover.Share(s) > 0.5f)
                {
                    if (Handover.Mode == ManipulationMode.Handover) Handover.MarkDropped(Prop, Dt);
                    else { Guard.ResetSlot(s); Engine.SetSlipOffset(s, Vector3.Zero); }
                }
                Engine.Finalize(s, Prop);
            }
            Snapshot();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _msHead = (_msHead + 1) % _msS.Length;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void Snapshot()
    {
        _propPos = Prop.Position; _propRot = Prop.Rotation;
        for (var s = 0; s < Slots; s++)
        {
            _act[s] = Engine.Active(s);
            for (var k = 0; k < Pts; k++) _pts[s * Pts + k] = Engine.Point(s, k);
            _arm[s * 3] = Handover.Shoulder(s); _arm[s * 3 + 1] = Handover.Elbow(s); _arm[s * 3 + 2] = Handover.Wrist(s);
            var n = Engine.CloseBlend(s) > 0.98f ? Engine.ContactCount(s) : 0;
            _nc[s] = n;
            for (var i = 0; i < n; i++) { _cp[s * MaxC + i] = Engine.ContactPoint(s, i); _cn[s * MaxC + i] = Engine.ContactNormal(s, i); _cg[s * MaxC + i] = Guard.Grip(s, i); }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.DexManipulation" };
            _loop.Start();
        }
        LastAction = "▶ 양손 정밀 조작 60 Hz · " + Prop.Name;
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("DexManipulation.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("DexManipulation.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
