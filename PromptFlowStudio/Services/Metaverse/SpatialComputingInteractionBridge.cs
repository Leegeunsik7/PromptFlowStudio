using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Metaverse;

public enum XrHand : byte { Left = 0, Right = 1 }

public readonly record struct XrGazeSample(Vector3 Origin, Vector3 Direction, float Confidence, long TimestampUs);

public readonly record struct XrHandSample(XrHand Hand, Vector3 ThumbTip, Vector3 IndexTip, Vector3 Palm, float Confidence, long TimestampUs);

public enum HoloTargetKind : byte { OverlayToggle = 0, CameraAngle, BranchChoice }

public readonly record struct HoloTarget(int Id, HoloTargetKind Kind, int Arg, Vector3 Center, float Radius, string Label);

public enum XrEventKind : byte { None = 0, HoverEnter, HoverExit, Select, DragStart, Drag, DragEnd, Zoom, CameraCut, BranchTrigger, OverlayToggled, Rejected }

public readonly record struct XrInteractionEvent(XrEventKind Kind, int Target, float A, float B, long TimestampUs);

/// <summary>1€ 필터(Casiez 2012) — 3D 벡터 · 무할당 값형.</summary>
public struct OneEuroFilter3
{
    private Vector3 _x, _dx;
    private bool _init;
    public float MinCutoff, Beta, DCutoff;

    public OneEuroFilter3(float minCutoff, float beta, float dCutoff = 1f) { MinCutoff = minCutoff; Beta = beta; DCutoff = dCutoff; _x = default; _dx = default; _init = false; }

    private static float Alpha(float cutoff, float dt) { var tau = 1f / (2f * MathF.PI * cutoff); return 1f / (1f + tau / dt); }

    public Vector3 Filter(Vector3 x, float dt)
    {
        if (!_init || dt <= 0f) { _init = true; _x = x; _dx = Vector3.Zero; return x; }
        var dx = (x - _x) / dt;
        _dx = Vector3.Lerp(_dx, dx, Alpha(DCutoff, dt));
        var cutoff = MinCutoff + Beta * _dx.Length();
        _x = Vector3.Lerp(_x, x, Alpha(cutoff, dt));
        return _x;
    }

    public void Reset() => _init = false;
}

/// <summary>
/// [PromptFlow Studio v16.61.0] 🖐 공간 컴퓨팅 인터랙티브 제스처 &amp; 시선 추적 브릿지(Vision Pro · Quest 3 · WebXR).
///   ▸ 시선: 신뢰도 게이트 → 1€ 필터(방향) → 홀로그램 타깃 레이-구 교차(거리 비례 1° 관용) → 호버 · 고정(fixation) 각 분산 판정 → 드웰 선택.
///   ▸ 핀치: 디바이스 프로파일별 엄지-검지 거리 히스테리시스(on/off · 감도 스케일) → 시선+핀치 선택(Look-and-Pinch) / 빈 공간 핀치 드래그 = 6DoF 카메라 오빗 / 양손 핀치 = 줌.
///   ▸ 액션: 오버레이 레이어 토글 · 카메라 앵글 컷 · v16.54 인터랙티브 서사 분기(<see cref="InteractiveVideoBranchingTranspiler.Choose"/>) — 이벤트 링(64) · 제스처→액션 지연 측정.
///   ▸ 수신: 디바이스 JSON(UTF-8) <see cref="Utf8JsonReader"/> 무할당 파싱.
/// </summary>
public sealed class SpatialComputingInteractionBridge
{
    public const int MaxTargets = 32, EventRing = 64;
    public const float FixationDeg = 1.5f;

    private readonly object _gate = new();
    private readonly HoloTarget[] _targets = new HoloTarget[MaxTargets];
    private readonly XrInteractionEvent[] _ring = new XrInteractionEvent[EventRing];
    private readonly bool[] _pinched = new bool[2];
    private readonly Vector3[] _palm = new Vector3[2], _dragFrom = new Vector3[2];
    private OneEuroFilter3 _gazeFilter = new(1.0f, 4f);   // 고정 시 지터 억제 · 단속(saccade) 시 β로 즉시 추종
    private InteractiveVideoBranchingTranspiler? _branch;
    private int _targetCount, _hover = -1, _dragHand = -1;
    private long _head, _lastGazeUs, _hoverSinceUs, _fixSinceUs;
    private Vector3 _gazeOrigin, _gazeDir = Vector3.UnitZ, _fixAnchor = Vector3.UnitZ;
    private bool _dwellFired;
    private float _zoomRef;
    private long _gazeSamples, _handSamples, _selects, _branchTriggers, _cameraCuts, _rejected, _parseErrors;
    private double _lastLatencyUs;

    public SpatialComputingInteractionBridge(InteractiveVideoBranchingTranspiler? branch = null) { _branch = branch; }

    public event Action? OnChange;

    public XrDeviceKind Device { get; set; } = XrDeviceKind.VisionPro;
    /// <summary>제스처 감도(0.25..2) — 핀치 임계 · 드래그 게인 · 드웰 시간 스케일.</summary>
    public float Sensitivity { get => _sens; set => _sens = Math.Clamp(value, 0.25f, 2f); }
    private float _sens = 1f;
    public bool DwellSelect { get; set; } = true;
    public float DwellMs => 700f / _sens;
    public float MinConfidence { get; set; } = 0.5f;
    public int OverlayMask { get; private set; } = 0xF;
    public int CameraPreset { get; private set; }
    public float CameraYawDeg { get; private set; }
    public float CameraPitchDeg { get; private set; } = 12f;
    public float CameraDistance { get; private set; } = 4f;
    public int HoverTarget => Volatile.Read(ref _hover);
    public bool IsFixating { get; private set; }
    public float FixationMs { get; private set; }
    public Vector3 GazeOrigin => _gazeOrigin;
    public Vector3 GazeDirection => _gazeDir;
    public bool IsPinched(XrHand h) => _pinched[(int)h];
    public int TargetCount => _targetCount;
    public long GazeSamples => Interlocked.Read(ref _gazeSamples);
    public long HandSamples => Interlocked.Read(ref _handSamples);
    public long Selects => Interlocked.Read(ref _selects);
    public long BranchTriggers => Interlocked.Read(ref _branchTriggers);
    public long CameraCuts => Interlocked.Read(ref _cameraCuts);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long ParseErrors => Interlocked.Read(ref _parseErrors);
    public long EventCount => Interlocked.Read(ref _head);
    public double LastActionLatencyMicros => Volatile.Read(ref _lastLatencyUs);
    public string? LastAction { get; private set; }

    /// <summary>프로파일별 핀치 on/off 임계(m) — 감도↑ = on 임계↑(쉽게 잡힘).</summary>
    public (float On, float Off) PinchThresholds()
    {
        var (on, off) = Device switch { XrDeviceKind.VisionPro => (0.015f, 0.03f), XrDeviceKind.Quest3 => (0.02f, 0.035f), _ => (0.025f, 0.04f) };
        on *= _sens;
        return (on, MathF.Max(off * MathF.Sqrt(_sens), on * 1.5f));
    }

    public void AttachBranch(InteractiveVideoBranchingTranspiler? branch) => _branch = branch;

    public HoloTarget Target(int i) => _targets[i];

    public int AddTarget(HoloTargetKind kind, int arg, Vector3 center, float radius, string label)
    {
        lock (_gate)
        {
            if (_targetCount >= MaxTargets) return -1;
            var id = _targetCount++;
            _targets[id] = new HoloTarget(id, kind, arg, center, radius, label);
            return id;
        }
    }

    public void ClearTargets() { lock (_gate) { _targetCount = 0; _hover = -1; } }

    /// <summary>기본 홀로 컨트롤 패널(오버레이 4 · 카메라 3 · 분기 2).</summary>
    public void BuildDefaultPanel()
    {
        ClearTargets();
        string[] ov = ["👥 아바타", "👁 시선 레이", "✨ VFX", "💬 홀로 자막"];
        for (var i = 0; i < 4; i++) AddTarget(HoloTargetKind.OverlayToggle, i, new Vector3(-1.5f + i * 0.5f, 2.4f, 1.2f), 0.16f, ov[i]);
        string[] cam = ["🎥 와이드", "🎥 오빗", "🎥 탑뷰"];
        for (var i = 0; i < 3; i++) AddTarget(HoloTargetKind.CameraAngle, i, new Vector3(-2.2f, 0.9f + i * 0.45f, 0.6f), 0.16f, cam[i]);
        AddTarget(HoloTargetKind.BranchChoice, 0, new Vector3(1.6f, 1.4f, 1.0f), 0.22f, "🔀 분기 A");
        AddTarget(HoloTargetKind.BranchChoice, 1, new Vector3(2.2f, 1.4f, 0.6f), 0.22f, "🔀 분기 B");
    }

    /// <summary>레이-구 교차 최근접 타깃(거리 비례 관용 tan 1°) — 없으면 -1.</summary>
    public int Raycast(Vector3 origin, Vector3 dir)
    {
        var best = -1; var bestT = float.MaxValue;
        for (var i = 0; i < _targetCount; i++)
        {
            ref readonly var t = ref _targets[i];
            var oc = t.Center - origin;
            var tc = Vector3.Dot(oc, dir);
            if (tc <= 0f) continue;
            var d2 = oc.LengthSquared() - tc * tc;
            var r = t.Radius + tc * 0.01745f;
            if (d2 > r * r || tc >= bestT) continue;
            bestT = tc; best = i;
        }
        return best;
    }

    /// <summary>시선 샘플 처리(무할당) — 반환 = 호버 타깃.</summary>
    public int ProcessGaze(in XrGazeSample g)
    {
        if (g.Confidence < MinConfidence || g.Direction.LengthSquared() < 1e-8f) { Interlocked.Increment(ref _rejected); return _hover; }
        var changed = false;
        lock (_gate)
        {
            var dt = _lastGazeUs > 0 ? (g.TimestampUs - _lastGazeUs) * 1e-6f : 0f;
            _lastGazeUs = g.TimestampUs;
            var dir = Vector3.Normalize(_gazeFilter.Filter(Vector3.Normalize(g.Direction), Math.Clamp(dt, 0f, 0.2f)));
            _gazeOrigin = g.Origin; _gazeDir = dir;
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(dir, _fixAnchor), -1f, 1f)) * (180f / MathF.PI);
            if (ang > FixationDeg) { _fixAnchor = dir; _fixSinceUs = g.TimestampUs; IsFixating = false; FixationMs = 0f; }
            else { FixationMs = (g.TimestampUs - _fixSinceUs) / 1000f; IsFixating = FixationMs >= 100f; }
            var hit = Raycast(g.Origin, dir);
            if (hit != _hover)
            {
                if (_hover >= 0) Push(XrEventKind.HoverExit, _hover, 0f, 0f, g.TimestampUs);
                _hover = hit; _hoverSinceUs = g.TimestampUs; _dwellFired = false;
                if (hit >= 0) Push(XrEventKind.HoverEnter, hit, 0f, 0f, g.TimestampUs);
                changed = true;
            }
            else if (hit >= 0 && DwellSelect && !_dwellFired && IsFixating && (g.TimestampUs - _hoverSinceUs) / 1000f >= DwellMs)
            {
                _dwellFired = true;
                Activate(hit, g.TimestampUs, Stopwatch.GetTimestamp());
                changed = true;
            }
        }
        Interlocked.Increment(ref _gazeSamples);
        if (changed) Raise();
        return _hover;
    }

    /// <summary>손 샘플 처리(무할당) — 핀치 상태 전이 · 선택/드래그/줌.</summary>
    public void ProcessHand(in XrHandSample h)
    {
        if (h.Confidence < MinConfidence) { Interlocked.Increment(ref _rejected); return; }
        var t0 = Stopwatch.GetTimestamp();
        var changed = false;
        lock (_gate)
        {
            var hi = (int)h.Hand;
            var (on, off) = PinchThresholds();
            var dist = Vector3.Distance(h.ThumbTip, h.IndexTip);
            _palm[hi] = h.Palm;
            var was = _pinched[hi];
            var now = was ? dist < off : dist < on;
            if (now && !was)
            {
                _pinched[hi] = true;
                var other = 1 - hi;
                if (_pinched[other])
                {
                    _zoomRef = MathF.Max(0.02f, Vector3.Distance(_palm[0], _palm[1]));
                    if (_dragHand >= 0) { Push(XrEventKind.DragEnd, -1, 0f, 0f, h.TimestampUs); _dragHand = -1; }
                }
                else if (_hover >= 0) { _dwellFired = true; Activate(_hover, h.TimestampUs, t0); }
                else { _dragHand = hi; _dragFrom[hi] = h.Palm; Push(XrEventKind.DragStart, -1, 0f, 0f, h.TimestampUs); }
                changed = true;
            }
            else if (!now && was)
            {
                _pinched[hi] = false;
                if (_dragHand == hi) { _dragHand = -1; Push(XrEventKind.DragEnd, -1, 0f, 0f, h.TimestampUs); changed = true; }
            }
            else if (now)
            {
                if (_pinched[0] && _pinched[1])
                {
                    var d = MathF.Max(0.02f, Vector3.Distance(_palm[0], _palm[1]));
                    var ratio = d / _zoomRef;
                    if (MathF.Abs(ratio - 1f) > 0.01f)
                    {
                        CameraDistance = Math.Clamp(CameraDistance / MathF.Pow(ratio, _sens), 0.8f, 12f);
                        _zoomRef = d;
                        Push(XrEventKind.Zoom, -1, CameraDistance, ratio, h.TimestampUs);
                        changed = true;
                    }
                }
                else if (_dragHand == hi)
                {
                    var delta = h.Palm - _dragFrom[hi];
                    if (delta.LengthSquared() > 1e-6f)
                    {
                        var gain = 180f * _sens;
                        CameraYawDeg = Wrap(CameraYawDeg + delta.X * gain);
                        CameraPitchDeg = Math.Clamp(CameraPitchDeg + delta.Y * gain, -80f, 80f);
                        _dragFrom[hi] = h.Palm;
                        Push(XrEventKind.Drag, -1, CameraYawDeg, CameraPitchDeg, h.TimestampUs);
                        changed = true;
                    }
                }
            }
        }
        Interlocked.Increment(ref _handSamples);
        if (changed) Raise();
    }

    private static float Wrap(float deg) { deg %= 360f; return deg > 180f ? deg - 360f : deg < -180f ? deg + 360f : deg; }

    /// <summary>타깃 활성(락 내부).</summary>
    private void Activate(int target, long tUs, long t0)
    {
        ref readonly var t = ref _targets[target];
        Interlocked.Increment(ref _selects);
        Push(XrEventKind.Select, target, t.Arg, 0f, tUs);
        switch (t.Kind)
        {
            case HoloTargetKind.OverlayToggle:
                OverlayMask ^= 1 << t.Arg;
                Push(XrEventKind.OverlayToggled, target, t.Arg, (OverlayMask >> t.Arg) & 1, tUs);
                break;
            case HoloTargetKind.CameraAngle:
                CameraPreset = t.Arg;
                (CameraYawDeg, CameraPitchDeg, CameraDistance) = t.Arg switch { 0 => (0f, 12f, 4.5f), 1 => (60f, 20f, 3.2f), _ => (0f, 75f, 5f) };
                Interlocked.Increment(ref _cameraCuts);
                Push(XrEventKind.CameraCut, target, t.Arg, 0f, tUs);
                break;
            case HoloTargetKind.BranchChoice:
                if (_branch is { } br) br.Choose(t.Arg);
                Interlocked.Increment(ref _branchTriggers);
                Push(XrEventKind.BranchTrigger, target, t.Arg, _branch is null ? 0f : 1f, tUs);
                break;
        }
        Volatile.Write(ref _lastLatencyUs, (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
        LastAction = t.Kind switch { HoloTargetKind.OverlayToggle => "🫧 오버레이 토글 · ", HoloTargetKind.CameraAngle => "🎥 카메라 컷 · ", _ => "🔀 서사 분기 · " } + t.Label;
    }

    /// <summary>UI/테스트용 직접 활성.</summary>
    public void Select(int target)
    {
        if ((uint)target >= (uint)_targetCount) return;
        lock (_gate) Activate(target, 0, Stopwatch.GetTimestamp());
        Raise();
    }

    /// <summary>6DoF 트랙패드 직접 오빗.</summary>
    public void Orbit(float dYawDeg, float dPitchDeg, float zoom = 1f)
    {
        lock (_gate)
        {
            CameraYawDeg = Wrap(CameraYawDeg + dYawDeg * _sens);
            CameraPitchDeg = Math.Clamp(CameraPitchDeg + dPitchDeg * _sens, -80f, 80f);
            CameraDistance = Math.Clamp(CameraDistance * zoom, 0.8f, 12f);
        }
    }

    private void Push(XrEventKind k, int target, float a, float b, long tUs)
    {
        var i = Interlocked.Increment(ref _head) - 1;
        _ring[i % EventRing] = new XrInteractionEvent(k, target, a, b, tUs);
    }

    /// <summary>cursor 이후 이벤트 읽기(덮어쓴 구간은 건너뜀) — 반환 = 개수.</summary>
    public int ReadEvents(ref long cursor, Span<XrInteractionEvent> dst)
    {
        lock (_gate)
        {
            var head = _head;
            if (cursor < head - EventRing) cursor = head - EventRing;
            var n = 0;
            while (cursor < head && n < dst.Length) dst[n++] = _ring[cursor++ % EventRing];
            return n;
        }
    }

    /// <summary>디바이스 JSON(UTF-8) 수신 — {"t":"gaze","o":[..],"d":[..],"c":0.9,"ts":µs} | {"t":"hand","h":"L|R","th":[..],"ix":[..],"pm":[..],"c":..,"ts":..} | "dev":"visionpro|quest3|webxr".</summary>
    public bool TryReceive(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var r = new Utf8JsonReader(utf8);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { Interlocked.Increment(ref _parseErrors); return false; }
            byte type = 0; var hand = XrHand.Right; var conf = 1f; long ts = 0;
            Vector3 o = default, d = default, th = default, ix = default, pm = default;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("t"u8)) { r.Read(); type = r.ValueTextEquals("gaze"u8) ? (byte)1 : r.ValueTextEquals("hand"u8) ? (byte)2 : (byte)0; }
                else if (r.ValueTextEquals("dev"u8)) { r.Read(); Device = r.ValueTextEquals("quest3"u8) ? XrDeviceKind.Quest3 : r.ValueTextEquals("visionpro"u8) ? XrDeviceKind.VisionPro : XrDeviceKind.WebXr; }
                else if (r.ValueTextEquals("h"u8)) { r.Read(); hand = r.ValueTextEquals("L"u8) ? XrHand.Left : XrHand.Right; }
                else if (r.ValueTextEquals("c"u8)) { r.Read(); conf = r.GetSingle(); }
                else if (r.ValueTextEquals("ts"u8)) { r.Read(); ts = r.GetInt64(); }
                else if (r.ValueTextEquals("o"u8)) o = Vec(ref r);
                else if (r.ValueTextEquals("d"u8)) d = Vec(ref r);
                else if (r.ValueTextEquals("th"u8)) th = Vec(ref r);
                else if (r.ValueTextEquals("ix"u8)) ix = Vec(ref r);
                else if (r.ValueTextEquals("pm"u8)) pm = Vec(ref r);
                else { r.Read(); r.Skip(); }
            }
            if (type == 1) { ProcessGaze(new XrGazeSample(o, d, conf, ts)); return true; }
            if (type == 2) { ProcessHand(new XrHandSample(hand, th, ix, pm, conf, ts)); return true; }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (FormatException) { }
        Interlocked.Increment(ref _parseErrors);
        return false;
    }

    private static Vector3 Vec(ref Utf8JsonReader r)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.StartArray) throw new FormatException("vec3");
        r.Read(); var x = r.GetSingle(); r.Read(); var y = r.GetSingle(); r.Read(); var z = r.GetSingle();
        if (!r.Read() || r.TokenType != JsonTokenType.EndArray) throw new FormatException("vec3");
        return new Vector3(x, y, z);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Xr.Interaction.OnChange", ex); }
    }
}
