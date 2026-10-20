using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.SpatialXr;

/// <summary>OpenXR XR_EXT_hand_tracking 26 관절 순서.</summary>
public enum XrHandJoint : byte
{
    Palm, Wrist,
    ThumbMetacarpal, ThumbProximal, ThumbDistal, ThumbTip,
    IndexMetacarpal, IndexProximal, IndexIntermediate, IndexDistal, IndexTip,
    MiddleMetacarpal, MiddleProximal, MiddleIntermediate, MiddleDistal, MiddleTip,
    RingMetacarpal, RingProximal, RingIntermediate, RingDistal, RingTip,
    LittleMetacarpal, LittleProximal, LittleIntermediate, LittleDistal, LittleTip,
}

public enum XrGesture : byte { None, Point, Pinch, Grab }
public enum HapticCause : byte { Contact, GrabStart, Release, PointHover }
public enum XrPropSource : byte { Manual, OpenUsd, Gaussian }

/// <summary>햅틱 임펄스(OpenXR XrHapticVibration 대응: 진폭 0..1 · 주파수 Hz · 지속 ms).</summary>
public readonly record struct HapticImpulse(long Seq, long AtUs, int Hand, int Prop, float Amplitude, float FrequencyHz, float DurationMs, HapticCause Cause);

/// <summary>
/// [PromptFlow Studio v16.97.0] 🥽 공간 핸드 제스처 &amp; 3D 햅틱 피드백 가드.
/// · 26관절 × 2손 SoA · 입력 가드(NaN · 저신뢰 · 손목-손바닥 비정상 거리 거부).
/// · 제스처: 핀치(엄지-검지 끝 거리 히스테리시스 2/3 cm) · 그랩(검지/중지/약지/소지 평균 굽힘) · 포인트(검지 펴짐 + 나머지 굽힘) — 굽힘 = 1 − 끝-중수골 거리 ÷ 마디 길이 합.
/// · 충돌: 소품 구(SoA 패딩) × 관절 Vector&lt;float&gt; 일괄 침투 판정 → 접촉 진입 엣지 햅틱 · 핀치/그랩 시작 시 소품 부착(앵커 오프셋) · 해제 시 분리.
/// · 햅틱 가드: 손별 최소 간격 · 초당 상한 · 진폭 상한 — 초과는 드롭 집계. 소품은 OpenUSD 프림/3DGS 그리드 군집에서 가져옴.
/// </summary>
public sealed class SpatialHandGestureHapticGuard
{
    public const int Joints = 26, MaxProps = 64, HapticCap = 256;
    public static readonly string[] GestureNames = { "없음", "포인트", "핀치", "그랩" };
    public static readonly string[] CauseNames = { "접촉", "잡기", "놓기", "가리킴" };
    private static readonly int Pad = (MaxProps + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
    private static readonly int[] Tips = { (int)XrHandJoint.IndexTip, (int)XrHandJoint.MiddleTip, (int)XrHandJoint.RingTip, (int)XrHandJoint.LittleTip, (int)XrHandJoint.ThumbTip, (int)XrHandJoint.Palm };

    private readonly object _gate = new();
    private readonly Vector3[] _j = new Vector3[Joints * 2];
    private readonly float[] _jr = new float[Joints];
    private readonly float[] _cx = new float[Pad], _cy = new float[Pad], _cz = new float[Pad], _cr = new float[Pad], _pen = new float[Pad];
    private readonly string[] _propName = new string[MaxProps];
    private readonly XrPropSource[] _propSrc = new XrPropSource[MaxProps];
    private readonly bool[] _contact = new bool[MaxProps * 2];
    private readonly HapticImpulse[] _ring = new HapticImpulse[HapticCap];
    private readonly long[] _lastHapticUs = new long[2], _windowUs = new long[2];
    private readonly int[] _windowCount = new int[2], _held = { -1, -1 }, _pointTarget = { -1, -1 };
    private readonly Vector3[] _holdOffset = new Vector3[2], _prevPalm = new Vector3[2];
    private readonly long[] _prevTs = new long[2], _ts = new long[2];
    private readonly bool[] _tracked = new bool[2], _pinching = new bool[2];
    private readonly XrGesture[] _gesture = new XrGesture[2], _prevGesture = new XrGesture[2];
    private readonly float[] _pinchStr = new float[2], _grabStr = new float[2], _curl = new float[10], _speed = new float[2];
    private int _props;
    private long _hapticSeq, _dropped, _invalid, _updates, _propVersion;

    public SpatialHandGestureHapticGuard()
    {
        for (var i = 0; i < Joints; i++) _jr[i] = i == (int)XrHandJoint.Palm ? 0.035f : 0.009f;
        for (var i = 0; i < Pad; i++) _cr[i] = -1f; // 패딩 = 비활성
    }

    public float MinConfidence { get; set; } = 0.5f;
    public double MinHapticIntervalMs { get; set; } = 25;
    public int MaxHapticsPerSec { get; set; } = 30;
    public float MaxAmplitude { get; set; } = 1f;
    public float Gain { get; set; } = 1f;
    public int PropCount => Volatile.Read(ref _props);
    public long PropVersion => Interlocked.Read(ref _propVersion);
    public long HapticSeq => Interlocked.Read(ref _hapticSeq);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Invalid => Interlocked.Read(ref _invalid);
    public long Updates => Interlocked.Read(ref _updates);
    public double LastUpdateUs { get; private set; }

    public bool Tracked(int h) => _tracked[h];
    public XrGesture Gesture(int h) => _gesture[h];
    public float PinchStrength(int h) => _pinchStr[h];
    public float GrabStrength(int h) => _grabStr[h];
    public float Curl(int h, int finger) => _curl[h * 5 + finger];
    public int Held(int h) => _held[h];
    public int PointTarget(int h) => _pointTarget[h];
    public float HandSpeed(int h) => _speed[h];
    public Vector3 Joint(int h, XrHandJoint j) => _j[h * Joints + (int)j];
    public bool InContact(int h, int prop) => _contact[h * MaxProps + prop];

    // ── 소품 ──
    public string PropName(int i) => _propName[i];
    public XrPropSource PropSource(int i) => _propSrc[i];
    public Vector3 PropCenter(int i) => new(_cx[i], _cy[i], _cz[i]);
    public float PropRadius(int i) => _cr[i];
    public ReadOnlySpan<float> PropX => _cx.AsSpan(0, _props);
    public ReadOnlySpan<float> PropY => _cy.AsSpan(0, _props);
    public ReadOnlySpan<float> PropZ => _cz.AsSpan(0, _props);

    public int AddProp(string name, Vector3 c, float r, XrPropSource src = XrPropSource.Manual)
    {
        lock (_gate)
        {
            if (_props >= MaxProps) return -1;
            var i = _props;
            _propName[i] = name; _propSrc[i] = src; _cx[i] = c.X; _cy[i] = c.Y; _cz[i] = c.Z; _cr[i] = Math.Max(0.005f, r);
            Volatile.Write(ref _props, i + 1);
            Interlocked.Increment(ref _propVersion);
            return i;
        }
    }

    public void ClearProps()
    {
        lock (_gate)
        {
            for (var i = 0; i < Pad; i++) _cr[i] = -1f;
            Array.Clear(_contact);
            _held[0] = _held[1] = -1; _pointTarget[0] = _pointTarget[1] = -1;
            _props = 0;
            Interlocked.Increment(ref _propVersion);
        }
    }

    /// <summary>OpenUSD 스테이지 가시 프림 → 소품 구(scale = 반지름 · 반경 상한 0.5 m).</summary>
    public int ImportUsd(OpenUsdSpatialAssetCompositorEngine comp, Vector3 offset, float scale = 1f)
    {
        var n = 0;
        var count = comp.Stage.Count;
        for (var i = 0; i < count && PropCount < MaxProps; i++)
        {
            var s = comp.State(i);
            if (!s.Defined || !s.Visible) continue;
            if (AddProp("usd:" + i, offset + new Vector3(s.X, s.Y, s.Z) * scale, Math.Clamp(s.Scale * 0.5f * scale, 0.02f, 0.5f), XrPropSource.OpenUsd) >= 0) n++;
        }
        return n;
    }

    /// <summary>3DGS 장면 → 그리드(g³) 군집 구(중심 = 평균 · 반지름 = 평균 거리 + 평균 스케일) — 콜드 경로.</summary>
    public int ImportGaussians(GaussianScene gs, Vector3 offset, int grid = 3, int minSplats = 8)
    {
        if (gs.Count == 0) return 0;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (var i = 0; i < gs.Count; i++) { var p = gs.Position(i); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var cells = grid * grid * grid;
        var sum = new Vector3[cells]; var cnt = new int[cells]; var ext = new float[cells];
        var size = Vector3.Max(max - min, new Vector3(1e-4f));
        int Cell(Vector3 p)
        {
            var t = (p - min) / size * grid;
            return Math.Clamp((int)t.X, 0, grid - 1) + grid * (Math.Clamp((int)t.Y, 0, grid - 1) + grid * Math.Clamp((int)t.Z, 0, grid - 1));
        }
        for (var i = 0; i < gs.Count; i++) { var c = Cell(gs.Position(i)); sum[c] += gs.Position(i); cnt[c]++; }
        for (var i = 0; i < gs.Count; i++)
        {
            var c = Cell(gs.Position(i));
            var s = gs.ScaleOf(i);
            ext[c] += (gs.Position(i) - sum[c] / cnt[c]).Length() + (s.X + s.Y + s.Z) / 3f;
        }
        var n = 0;
        for (var c = 0; c < cells && PropCount < MaxProps; c++)
        {
            if (cnt[c] < minSplats) continue;
            if (AddProp("3dgs:" + c, offset + sum[c] / cnt[c], Math.Clamp(ext[c] / cnt[c], 0.03f, 0.6f), XrPropSource.Gaussian) >= 0) n++;
        }
        return n;
    }

    // ── 입력 ──
    /// <summary>손 관절 수신(26 · 무할당) — NaN/저신뢰/비정상 골격 = 추적 상실.</summary>
    public bool SubmitHand(int hand, ReadOnlySpan<Vector3> joints, float confidence, long tsUs)
    {
        var ok = joints.Length >= Joints && confidence >= MinConfidence;
        if (ok)
            for (var i = 0; i < Joints; i++) { var p = joints[i]; if (!float.IsFinite(p.X + p.Y + p.Z)) { ok = false; break; } }
        if (ok)
        {
            var wp = (joints[(int)XrHandJoint.Wrist] - joints[(int)XrHandJoint.Palm]).Length();
            ok = wp > 0.01f && wp < 0.2f;
        }
        lock (_gate)
        {
            if (!ok)
            {
                Interlocked.Increment(ref _invalid);
                _tracked[hand] = false;
                return false;
            }
            joints[..Joints].CopyTo(_j.AsSpan(hand * Joints, Joints));
            _tracked[hand] = true;
            _ts[hand] = tsUs;
            return true;
        }
    }

    public void LoseHand(int hand) { lock (_gate) _tracked[hand] = false; }

    /// <summary>1틱(무할당): 제스처 → 충돌 → 잡기/놓기 → 햅틱.</summary>
    public void Update(long nowUs)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var h = 0; h < 2; h++)
            {
                if (!_tracked[h])
                {
                    if (_held[h] >= 0) { _held[h] = -1; Emit(h, -1, 0.2f, 80f, 15f, HapticCause.Release, nowUs); }
                    for (var p = 0; p < _props; p++) _contact[h * MaxProps + p] = false;
                    _prevGesture[h] = _gesture[h] = XrGesture.None;
                    continue;
                }
                var b = h * Joints;
                var palm = _j[b + (int)XrHandJoint.Palm];
                var dt = (_ts[h] - _prevTs[h]) * 1e-6;
                _speed[h] = _prevTs[h] != 0 && dt > 0 && dt < 0.5 ? (float)((palm - _prevPalm[h]).Length() / dt) : 0f;
                _prevPalm[h] = palm; _prevTs[h] = _ts[h];
                Classify(h);
                Collide(h, nowUs);
                Interact(h, nowUs);
                _prevGesture[h] = _gesture[h];
            }
            Interlocked.Increment(ref _updates);
            LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private float FingerCurl(int b, int meta, int tip)
    {
        var chain = 0f;
        for (var k = meta; k < tip; k++) chain += (_j[b + k + 1] - _j[b + k]).Length();
        var r = (_j[b + tip] - _j[b + meta]).Length() / Math.Max(1e-4f, chain);
        return Math.Clamp((0.95f - r) / 0.45f, 0f, 1f);
    }

    private void Classify(int h)
    {
        var b = h * Joints;
        _curl[h * 5 + 0] = FingerCurl(b, (int)XrHandJoint.ThumbMetacarpal, (int)XrHandJoint.ThumbTip);
        _curl[h * 5 + 1] = FingerCurl(b, (int)XrHandJoint.IndexMetacarpal, (int)XrHandJoint.IndexTip);
        _curl[h * 5 + 2] = FingerCurl(b, (int)XrHandJoint.MiddleMetacarpal, (int)XrHandJoint.MiddleTip);
        _curl[h * 5 + 3] = FingerCurl(b, (int)XrHandJoint.RingMetacarpal, (int)XrHandJoint.RingTip);
        _curl[h * 5 + 4] = FingerCurl(b, (int)XrHandJoint.LittleMetacarpal, (int)XrHandJoint.LittleTip);
        var pd = (_j[b + (int)XrHandJoint.ThumbTip] - _j[b + (int)XrHandJoint.IndexTip]).Length();
        _pinching[h] = _pinching[h] ? pd < 0.03f : pd < 0.02f;
        _pinchStr[h] = Math.Clamp((0.05f - pd) / 0.035f, 0f, 1f);
        var others = (_curl[h * 5 + 2] + _curl[h * 5 + 3] + _curl[h * 5 + 4]) / 3f;
        _grabStr[h] = (others * 3f + _curl[h * 5 + 1]) / 4f;
        _gesture[h] = others >= 0.7f && _curl[h * 5 + 1] >= 0.6f ? XrGesture.Grab
            : _pinching[h] ? XrGesture.Pinch
            : _curl[h * 5 + 1] < 0.25f && others >= 0.6f ? XrGesture.Point
            : XrGesture.None;
    }

    /// <summary>손끝 5 + 손바닥 × 소품 SIMD 침투 → 접촉 진입 햅틱.</summary>
    private void Collide(int h, long nowUs)
    {
        var b = h * Joints;
        _pen.AsSpan().Clear();
        var vcx = MemoryMarshal.Cast<float, Vector<float>>(_cx.AsSpan()); var vcy = MemoryMarshal.Cast<float, Vector<float>>(_cy.AsSpan());
        var vcz = MemoryMarshal.Cast<float, Vector<float>>(_cz.AsSpan()); var vcr = MemoryMarshal.Cast<float, Vector<float>>(_cr.AsSpan());
        var vp = MemoryMarshal.Cast<float, Vector<float>>(_pen.AsSpan());
        for (var t = 0; t < Tips.Length; t++)
        {
            var ji = Tips[t];
            var p = _j[b + ji];
            Vector<float> jx = new(p.X), jy = new(p.Y), jz = new(p.Z), jr = new(_jr[ji]);
            for (var k = 0; k < vcx.Length; k++)
            {
                var dx = vcx[k] - jx; var dy = vcy[k] - jy; var dz = vcz[k] - jz;
                var d = Vector.SquareRoot(dx * dx + dy * dy + dz * dz);
                var pen = vcr[k] + jr - d;
                var active = Vector.GreaterThan(vcr[k], Vector<float>.Zero);
                vp[k] = Vector.Max(vp[k], Vector.ConditionalSelect(active, pen, Vector<float>.Zero));
            }
        }
        for (var i = 0; i < _props; i++)
        {
            ref var c = ref _contact[h * MaxProps + i];
            var now = _pen[i] > 0;
            if (now && !c && _held[h] != i)
                Emit(h, i, Math.Clamp(0.25f + 0.25f * _speed[h] + _pen[i] / 0.02f * 0.3f, 0f, 1f), 200f, 12f, HapticCause.Contact, nowUs);
            c = now;
        }
    }

    private void Interact(int h, long nowUs)
    {
        var b = h * Joints;
        var g = _gesture[h]; var pg = _prevGesture[h];
        var anchor = g == XrGesture.Pinch ? (_j[b + (int)XrHandJoint.ThumbTip] + _j[b + (int)XrHandJoint.IndexTip]) * 0.5f : _j[b + (int)XrHandJoint.Palm];
        if (_held[h] >= 0)
        {
            if (g is XrGesture.Pinch or XrGesture.Grab)
            {
                var c = anchor + _holdOffset[h];
                var i = _held[h];
                _cx[i] = c.X; _cy[i] = c.Y; _cz[i] = c.Z;
                Interlocked.Increment(ref _propVersion);
            }
            else
            {
                var i = _held[h];
                _held[h] = -1;
                Emit(h, i, 0.3f, 120f, 20f, HapticCause.Release, nowUs);
            }
            return;
        }
        if ((g is XrGesture.Pinch or XrGesture.Grab) && g != pg)
        {
            var best = -1; var bestD = float.MaxValue;
            var margin = g == XrGesture.Pinch ? 0.015f : 0.05f;
            for (var i = 0; i < _props; i++)
            {
                if (_held[1 - h] == i) continue;
                var d = (PropCenter(i) - anchor).Length() - _cr[i];
                if (d < margin && d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0)
            {
                _held[h] = best;
                _holdOffset[h] = PropCenter(best) - anchor;
                Emit(h, best, g == XrGesture.Grab ? 0.8f : 0.55f, 160f, 40f, HapticCause.GrabStart, nowUs);
            }
        }
        // 포인트 레이(검지 근위 → 끝) 히트
        var prevTarget = _pointTarget[h];
        _pointTarget[h] = -1;
        if (g == XrGesture.Point)
        {
            var o = _j[b + (int)XrHandJoint.IndexTip];
            var d = Vector3.Normalize(o - _j[b + (int)XrHandJoint.IndexProximal]);
            var bestT = float.MaxValue;
            for (var i = 0; i < _props; i++)
            {
                var oc = o - PropCenter(i);
                var bb = Vector3.Dot(oc, d); var cc = oc.LengthSquared() - _cr[i] * _cr[i];
                var disc = bb * bb - cc;
                if (disc < 0) continue;
                var t = -bb - MathF.Sqrt(disc);
                if (t > 0 && t < bestT) { bestT = t; _pointTarget[h] = i; }
            }
            if (_pointTarget[h] >= 0 && _pointTarget[h] != prevTarget) Emit(h, _pointTarget[h], 0.15f, 250f, 8f, HapticCause.PointHover, nowUs);
        }
    }

    /// <summary>햅틱 발행 가드: 손별 최소 간격 · 초당 상한 · 진폭 상한(위반 = 드롭).</summary>
    private void Emit(int h, int prop, float amp, float hz, float ms, HapticCause cause, long nowUs)
    {
        if (_lastHapticUs[h] != 0 && (nowUs - _lastHapticUs[h]) < MinHapticIntervalMs * 1000 && cause != HapticCause.GrabStart) { Interlocked.Increment(ref _dropped); return; }
        if (nowUs - _windowUs[h] >= 1_000_000) { _windowUs[h] = nowUs; _windowCount[h] = 0; }
        if (_windowCount[h] >= MaxHapticsPerSec) { Interlocked.Increment(ref _dropped); return; }
        _windowCount[h]++;
        _lastHapticUs[h] = nowUs;
        var seq = _hapticSeq + 1;
        _ring[seq % HapticCap] = new HapticImpulse(seq, nowUs, h, prop, Math.Clamp(amp * Gain, 0f, MaxAmplitude), hz, ms, cause);
        Interlocked.Exchange(ref _hapticSeq, seq);
    }

    public bool TryGetHaptic(long seq, out HapticImpulse e)
    {
        lock (_gate)
        {
            if (seq <= 0 || seq > _hapticSeq || seq <= _hapticSeq - HapticCap) { e = default; return false; }
            e = _ring[seq % HapticCap];
            return e.Seq == seq;
        }
    }

    public HapticImpulse Haptic(int i) { TryGetHaptic(HapticSeq - i, out var e); return e; }
    public int HapticCount => (int)Math.Min(HapticSeq, HapticCap);

    // ── 합성 손(시뮬레이션 · 테스트) ──
    private static readonly Vector3[] FingerBase =
    {
        new(-0.035f, -0.01f, -0.02f), // 엄지 중수골(손바닥 기준 · 오른손 · −z 전방)
        new(-0.022f, 0f, -0.045f), new(-0.007f, 0f, -0.048f), new(0.008f, 0f, -0.046f), new(0.022f, 0f, -0.042f),
    };
    private static readonly float[] Seg = { 0.04f, 0.035f, 0.028f, 0.03f, 0.026f, 0.02f, 0.032f, 0.028f, 0.022f, 0.03f, 0.026f, 0.02f, 0.028f, 0.02f, 0.017f };

    /// <summary>
    /// 합성 손 26관절(손바닥 포즈 + 손가락 굽힘 0..1 + 핀치 0..1 · 왼손은 x 미러). curls = 검지/중지/약지/소지.
    /// </summary>
    public static void PoseHand(Span<Vector3> dst, int hand, Vector3 palm, Quaternion q, float indexCurl, float otherCurl, float pinch)
    {
        var mx = hand == 0 ? -1f : 1f;
        Vector3 W(Vector3 local) => palm + Vector3.Transform(new Vector3(local.X * mx, local.Y, local.Z), q);
        dst[(int)XrHandJoint.Palm] = palm;
        dst[(int)XrHandJoint.Wrist] = W(new Vector3(0, -0.005f, 0.05f));
        // 손가락(검지..소지): 중수골 → 근위 → 중간 → 원위 → 끝, 굽힘 = 손바닥 아래(−y)로 회전
        for (var f = 1; f < 5; f++)
        {
            var curl = f == 1 ? indexCurl : otherCurl;
            var bj = (int)XrHandJoint.IndexMetacarpal + (f - 1) * 5;
            var p = FingerBase[f] + new Vector3(0, 0, 0.035f);
            dst[bj] = W(p);
            p = FingerBase[f];
            dst[bj + 1] = W(p);
            var a = curl * 1.35f; // 마디당 누적 굽힘(rad)
            for (var s = 0; s < 3; s++)
            {
                var dir = new Vector3(0, -MathF.Sin(a * (s + 1)), -MathF.Cos(a * (s + 1)));
                p += dir * Seg[3 + (f - 1) * 3 + s];
                dst[bj + 2 + s] = W(p);
            }
        }
        // 엄지: 중수골 → 근위 → 원위 → 끝(핀치 = 끝/원위를 검지 끝으로 보간)
        var t0 = FingerBase[0];
        var td = Vector3.Normalize(new Vector3(0.45f, 0f, -1f));
        var tp = t0;
        dst[(int)XrHandJoint.ThumbMetacarpal] = W(tp);
        for (var s = 0; s < 3; s++)
        {
            tp += td * Seg[s];
            dst[(int)XrHandJoint.ThumbProximal + s] = W(tp);
        }
        if (pinch > 0)
        {
            var it = dst[(int)XrHandJoint.IndexTip];
            var tt = dst[(int)XrHandJoint.ThumbTip];
            var target = it + Vector3.Normalize(tt - it) * 0.004f;
            dst[(int)XrHandJoint.ThumbTip] = Vector3.Lerp(tt, target, pinch);
            dst[(int)XrHandJoint.ThumbDistal] = Vector3.Lerp(dst[(int)XrHandJoint.ThumbDistal], (dst[(int)XrHandJoint.ThumbProximal] + target) * 0.5f, pinch);
        }
    }
}
