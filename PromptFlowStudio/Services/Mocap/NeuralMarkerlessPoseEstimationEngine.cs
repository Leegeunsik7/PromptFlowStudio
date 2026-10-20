using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Mocap;

/// <summary>
/// 2D 키포인트 검출기 결선점(신경망 · ONNX 등). 프레임 RGBA → 76 키포인트 (u, v, 신뢰도) · 미검출 신뢰도 0. 무할당 구현 필요. 반환 = 검출 수.
/// </summary>
public interface I2dKeypointDetector
{
    int Detect(ReadOnlySpan<byte> rgba, int width, int height, Span<Vector3> uvConf);
}

/// <summary>
/// [PromptFlow Studio v16.75.0] 💃 다중 시점 마커리스 3D 포즈 추정 엔진.
///   ▸ 입력: 보정된 카메라(최대 8) 별 2D 키포인트(u, v, 신뢰도) — 직접 주입 또는 <see cref="I2dKeypointDetector"/> 결선(현재 BCL 내장 신경망 검출기 없음).
///   ▸ 삼각측량: 신뢰도 가중 선형 최소제곱(DLT 행 → 3×3 정규방정식 · 크라머) — 키포인트 76개를 <see cref="Vector{T}"/> 레인으로 일괄 SIMD 해석.
///   ▸ 강건화: 시점별 재투영 오차 &gt; <see cref="OutlierPx"/> 이고 3시점 이상이면 최악 시점 제외 후 재해석(최대 2회) · 2시점 고오차 해는 이전 위치 유지 · 신뢰도 = 검출 신뢰도 × 재투영 가우시안.
///   ▸ 단일 시점 폴백: 1시점만 보이는 관절은 부모 3D + 보정 뼈 길이로 광선 위 깊이 해석(Taylor) — 2해 중 이전 프레임에 가까운 해 · 루트는 이전 깊이 유지.
///   ▸ 시간 필터: 좌표별 One-Euro 필터(속도 적응 차단 주파수) · 고신뢰 프레임 뼈 길이 EMA(피험자 보정) · 손가락 굽힘/표정 블렌드셰이프 추출.
/// </summary>
public sealed class NeuralMarkerlessPoseEstimationEngine
{
    public const int MaxCameras = 8, K = MocapSkeleton.Keypoints;
    private static readonly int Padded = (K + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    private readonly object _gate = new();
    private readonly PinholeCamera[] _cams = new PinholeCamera[MaxCameras];
    private int _camCount;
    private readonly float[] _u = new float[MaxCameras * Padded], _v = new float[MaxCameras * Padded], _w = new float[MaxCameras * Padded];
    private readonly float[] _sx = new float[Padded], _sy = new float[Padded], _sz = new float[Padded], _cnt = new float[Padded];
    private readonly Vector3[] _raw = new Vector3[K], _out = new Vector3[K], _prevOut = new Vector3[K];
    private readonly float[] _conf = new float[K], _err = new float[K];
    private readonly int[] _views = new int[K];
    private readonly bool[] _valid = new bool[K];
    private readonly float[] _fx = new float[K * 3], _fdx = new float[K * 3];
    private readonly float[] _bone = new float[MocapSkeleton.Body];
    private readonly bool[] _excl = new bool[MaxCameras];
    private readonly Vector3[] _detScratch = new Vector3[K];
    private bool _hasPrev;
    private long _tPrev, _frames, _ticks, _outliers, _monocular;
    private float _meanErr, _meanConf;

    public NeuralMarkerlessPoseEstimationEngine()
    {
        for (var j = 1; j < MocapSkeleton.Body; j++) _bone[j] = MocapSkeleton.RestOffset[j].Length();
    }

    public I2dKeypointDetector? Detector { get; set; }
    public float OutlierPx { get; set; } = 12f;
    public float ReprojSigmaPx { get; set; } = 8f;
    public bool Smoothing { get; set; } = true;
    public float MinCutoff { get; set; } = 1.5f;
    public float Beta { get; set; } = 15f;
    public float DCutoff { get; set; } = 1.0f;

    public int CameraCount => _camCount;
    public PinholeCamera Camera(int c) => _cams[c];
    public long Frames => Interlocked.Read(ref _frames);
    public long OutliersRejected => Interlocked.Read(ref _outliers);
    public long MonocularJoints => Interlocked.Read(ref _monocular);
    public double LastMs => _ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public float MeanReprojectionPx => _meanErr;
    /// <summary>전신 24관절 평균 신뢰도.</summary>
    public float MeanConfidence => _meanConf;
    public static int SimdLanes => Vector<float>.Count;

    public Vector3 Position(int k) => _out[k];
    public Vector3 RawPosition(int k) => _raw[k];
    public float Confidence(int k) => _conf[k];
    public float ReprojectionError(int k) => _err[k];
    public int Views(int k) => _views[k];
    public bool IsValid(int k) => _valid[k];
    public float BoneLength(int j) => _bone[j];
    public ReadOnlySpan<Vector3> Positions => _out;

    // ══════════════════════ 구성 · 입력 ══════════════════════

    public int AddCamera(in PinholeCamera cam)
    {
        lock (_gate)
        {
            if (_camCount >= MaxCameras) return -1;
            _cams[_camCount] = cam;
            return _camCount++;
        }
    }

    public void ClearCameras() { lock (_gate) { _camCount = 0; Array.Clear(_w); _hasPrev = false; } }

    public void Reset() { lock (_gate) { _hasPrev = false; Array.Clear(_valid); } }

    /// <summary>카메라 c 의 키포인트 검출(u, v, 신뢰도). 길이 &lt; K 이면 나머지 미검출.</summary>
    public void SetDetections(int c, ReadOnlySpan<Vector3> uvConf)
    {
        lock (_gate)
        {
            var o = c * Padded;
            for (var k = 0; k < K; k++)
            {
                var d = k < uvConf.Length ? uvConf[k] : default;
                _u[o + k] = d.X; _v[o + k] = d.Y; _w[o + k] = d.Z > 0 && float.IsFinite(d.X + d.Y) ? MathF.Min(d.Z, 1f) : 0f;
            }
        }
    }

    /// <summary>프레임 → 결선 검출기 → 검출 주입. 검출기 미결선이면 false.</summary>
    public bool SubmitFrame(int c, ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (Detector is not { } det) return false;
        lock (_gate)
        {
            Array.Clear(_detScratch);
            det.Detect(rgba, width, height, _detScratch);
            SetDetections(c, _detScratch);
            return true;
        }
    }

    // ══════════════════════ 추정(무할당) ══════════════════════

    public void Estimate(long nowMicros)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Mocap.Pose.Estimate");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SolveSimd();
            float errSum = 0; var errN = 0;
            for (var k = 0; k < K; k++)
            {
                var views = (int)_cnt[k];
                if (views >= 2 && float.IsFinite(_sx[k] + _sy[k] + _sz[k]))
                {
                    var x = new Vector3(_sx[k], _sy[k], _sz[k]);
                    Array.Clear(_excl);
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var worst = Worst(k, x, out var worstErr, out var used);
                        if (used < 3 || worstErr <= OutlierPx) break;
                        _excl[worst] = true;
                        if (!SolveScalar(k, _excl, out x)) break;
                        _outliers++;
                    }
                    Stats(k, x, out var meanErr, out var meanW, out var used2);
                    // 2시점 해는 이상치 투표 불가 — 재투영 오차가 크면 이전 위치 유지(신뢰도 하향)
                    if (used2 == 2 && meanErr > OutlierPx && _valid[k]) { x = _prevOut[k]; meanW *= 0.3f; _outliers++; }
                    _raw[k] = x;
                    _views[k] = used2; _err[k] = meanErr;
                    _conf[k] = meanW * MathF.Exp(-(meanErr / ReprojSigmaPx) * (meanErr / ReprojSigmaPx));
                    _valid[k] = true;
                    errSum += meanErr; errN++;
                }
                else if (views == 1 && k < MocapSkeleton.Body && (_valid[k] || MocapSkeleton.Parent[k] >= 0 && _valid[MocapSkeleton.Parent[k]]))
                {
                    _raw[k] = Lift(k);
                    _views[k] = 1; _err[k] = 0;
                    _conf[k] = 0.5f * SingleWeight(k);
                    _valid[k] = true;
                    _monocular++;
                }
                else
                {
                    _views[k] = views;
                    _conf[k] *= 0.8f;   // 미관측: 이전 값 유지 · 신뢰도 감쇠
                }
            }
            // One-Euro
            var dt = _hasPrev ? MathF.Max(1e-4f, (nowMicros - _tPrev) / 1e6f) : 0f;
            for (var k = 0; k < K; k++)
            {
                if (!_valid[k]) continue;
                if (!_hasPrev || !Smoothing) { _out[k] = _raw[k]; _fx[k * 3] = _raw[k].X; _fx[k * 3 + 1] = _raw[k].Y; _fx[k * 3 + 2] = _raw[k].Z; _fdx[k * 3] = _fdx[k * 3 + 1] = _fdx[k * 3 + 2] = 0; continue; }
                _out[k] = new Vector3(Euro(k * 3, _raw[k].X, dt), Euro(k * 3 + 1, _raw[k].Y, dt), Euro(k * 3 + 2, _raw[k].Z, dt));
            }
            // 뼈 길이 EMA
            for (var j = 1; j < MocapSkeleton.Body; j++)
            {
                var p = MocapSkeleton.Parent[j];
                if (_conf[j] > 0.6f && _conf[p] > 0.6f && _views[j] >= 2 && _views[p] >= 2)
                    _bone[j] += (Vector3.Distance(_raw[j], _raw[p]) - _bone[j]) * 0.05f;
            }
            float cs = 0;
            for (var j = 0; j < MocapSkeleton.Body; j++) cs += _conf[j];
            _meanConf = cs / MocapSkeleton.Body;
            _meanErr = errN > 0 ? errSum / errN : 0;
            _out.AsSpan().CopyTo(_prevOut);
            _tPrev = nowMicros; _hasPrev = true;
            _frames++;
            _ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>모든 키포인트를 SIMD 레인으로 일괄 해석(정규방정식 누적 → 크라머).</summary>
    private void SolveSimd()
    {
        var vc = Vector<float>.Count;
        var one = Vector<float>.One; var zero = Vector<float>.Zero;
        for (var i = 0; i < Padded; i += vc)
        {
            Vector<float> m00 = zero, m01 = zero, m02 = zero, m11 = zero, m12 = zero, m22 = zero, r0 = zero, r1 = zero, r2 = zero, cnt = zero;
            for (var c = 0; c < _camCount; c++)
            {
                ref readonly var cam = ref _cams[c];
                var o = c * Padded + i;
                var w = new Vector<float>(_w, o);
                cnt += Vector.ConditionalSelect(Vector.GreaterThan(w, zero), one, zero);
                var u = (new Vector<float>(_u, o) - new Vector<float>(cam.Cx)) * new Vector<float>(1f / cam.Fx);
                var v = (new Vector<float>(_v, o) - new Vector<float>(cam.Cy)) * new Vector<float>(1f / cam.Fy);
                // 행 a = s·F − R(또는 D) · b = a·C
                Accumulate(u, cam.Forward, cam.Right, cam.Position, w, ref m00, ref m01, ref m02, ref m11, ref m12, ref m22, ref r0, ref r1, ref r2);
                Accumulate(v, cam.Forward, cam.Down, cam.Position, w, ref m00, ref m01, ref m02, ref m11, ref m12, ref m22, ref r0, ref r1, ref r2);
            }
            var c00 = m11 * m22 - m12 * m12; var c01 = m02 * m12 - m01 * m22; var c02 = m01 * m12 - m02 * m11;
            var det = m00 * c00 + m01 * c01 + m02 * c02;
            var ok = Vector.GreaterThan(Vector.Abs(det), new Vector<float>(1e-9f));
            var inv = Vector.ConditionalSelect(ok, one / Vector.ConditionalSelect(ok, det, one), zero);
            var c11 = m00 * m22 - m02 * m02; var c12 = m01 * m02 - m00 * m12; var c22 = m00 * m11 - m01 * m01;
            (c00 * r0 + c01 * r1 + c02 * r2).CopyTo(_sx, i);
            (c01 * r0 + c11 * r1 + c12 * r2).CopyTo(_sy, i);
            (c02 * r0 + c12 * r1 + c22 * r2).CopyTo(_sz, i);
            var vx = new Vector<float>(_sx, i) * inv; vx.CopyTo(_sx, i);
            var vy = new Vector<float>(_sy, i) * inv; vy.CopyTo(_sy, i);
            var vz = new Vector<float>(_sz, i) * inv; vz.CopyTo(_sz, i);
            Vector.ConditionalSelect(ok, cnt, zero).CopyTo(_cnt, i);
        }
    }

    private static void Accumulate(Vector<float> s, Vector3 f, Vector3 axis, Vector3 c, Vector<float> w,
        ref Vector<float> m00, ref Vector<float> m01, ref Vector<float> m02, ref Vector<float> m11, ref Vector<float> m12, ref Vector<float> m22,
        ref Vector<float> r0, ref Vector<float> r1, ref Vector<float> r2)
    {
        var ax = s * new Vector<float>(f.X) - new Vector<float>(axis.X);
        var ay = s * new Vector<float>(f.Y) - new Vector<float>(axis.Y);
        var az = s * new Vector<float>(f.Z) - new Vector<float>(axis.Z);
        var b = ax * new Vector<float>(c.X) + ay * new Vector<float>(c.Y) + az * new Vector<float>(c.Z);
        var wx = w * ax; var wy = w * ay; var wz = w * az;
        m00 += wx * ax; m01 += wx * ay; m02 += wx * az; m11 += wy * ay; m12 += wy * az; m22 += wz * az;
        r0 += wx * b; r1 += wy * b; r2 += wz * b;
    }

    /// <summary>스칼라 삼각측량(제외 카메라 마스크) — 이상치 재해석 · 검증 기준.</summary>
    public bool SolveScalar(int k, ReadOnlySpan<bool> exclude, out Vector3 x)
    {
        double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, r0 = 0, r1 = 0, r2 = 0;
        var n = 0;
        for (var c = 0; c < _camCount; c++)
        {
            var w = _w[c * Padded + k];
            if (w <= 0 || (c < exclude.Length && exclude[c])) continue;
            n++;
            ref readonly var cam = ref _cams[c];
            for (var row = 0; row < 2; row++)
            {
                var s = row == 0 ? (_u[c * Padded + k] - cam.Cx) / cam.Fx : (_v[c * Padded + k] - cam.Cy) / cam.Fy;
                var axis = row == 0 ? cam.Right : cam.Down;
                var a = cam.Forward * s - axis;
                double b = Vector3.Dot(a, cam.Position);
                m00 += w * a.X * a.X; m01 += w * a.X * a.Y; m02 += w * a.X * a.Z; m11 += w * a.Y * a.Y; m12 += w * a.Y * a.Z; m22 += w * a.Z * a.Z;
                r0 += w * a.X * b; r1 += w * a.Y * b; r2 += w * a.Z * b;
            }
        }
        x = default;
        if (n < 2) return false;
        double c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
        var det = m00 * c00 + m01 * c01 + m02 * c02;
        if (Math.Abs(det) < 1e-12) return false;
        double c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
        x = new Vector3((float)((c00 * r0 + c01 * r1 + c02 * r2) / det), (float)((c01 * r0 + c11 * r1 + c12 * r2) / det), (float)((c02 * r0 + c12 * r1 + c22 * r2) / det));
        return true;
    }

    private int Worst(int k, Vector3 x, out float worstErr, out int used)
    {
        var worst = -1; worstErr = 0; used = 0;
        for (var c = 0; c < _camCount; c++)
        {
            var w = _w[c * Padded + k];
            if (w <= 0 || _excl[c]) continue;
            used++;
            if (!_cams[c].Project(x, out var uv)) { worst = c; worstErr = float.MaxValue; continue; }
            var e = Vector2.Distance(uv, new Vector2(_u[c * Padded + k], _v[c * Padded + k]));
            if (e > worstErr) { worstErr = e; worst = c; }
        }
        return worst;
    }

    private void Stats(int k, Vector3 x, out float meanErr, out float meanW, out int used)
    {
        float es = 0, ws = 0; used = 0;
        for (var c = 0; c < _camCount; c++)
        {
            var w = _w[c * Padded + k];
            if (w <= 0 || _excl[c]) continue;
            used++; ws += w;
            es += _cams[c].Project(x, out var uv) ? Vector2.Distance(uv, new Vector2(_u[c * Padded + k], _v[c * Padded + k])) : 1000f;
        }
        meanErr = used > 0 ? es / used : 0; meanW = used > 0 ? ws / used : 0;
    }

    private float SingleWeight(int k)
    {
        for (var c = 0; c < _camCount; c++) if (_w[c * Padded + k] > 0) return _w[c * Padded + k];
        return 0;
    }

    /// <summary>단일 시점 Taylor 리프팅(부모 3D + 뼈 길이 → 광선 위 2해 중 이전 위치에 가까운 해).</summary>
    private Vector3 Lift(int k)
    {
        var c = 0;
        for (; c < _camCount; c++) if (_w[c * Padded + k] > 0) break;
        ref readonly var cam = ref _cams[c];
        var d = cam.Ray(new Vector2(_u[c * Padded + k], _v[c * Padded + k]));
        var o = cam.Position;
        var prev = _valid[k] ? _prevOut[k] : default;
        var parent = MocapSkeleton.Parent[k];
        if (parent < 0 || !_valid[parent])
        {
            var t0 = MathF.Max(0.1f, Vector3.Dot(prev - o, d));   // 루트: 이전 깊이 유지
            return o + d * t0;
        }
        var p = _raw[parent];
        var L = _bone[k];
        var tc = Vector3.Dot(p - o, d);
        var closest = o + d * tc;
        var h2 = L * L - (closest - p).LengthSquared();
        if (h2 <= 0) return _valid[k] && (closest - p).LengthSquared() > 2.25f * L * L ? prev : closest;   // 광선이 뼈 구 밖 = 검출 이상치 → 이전 유지
        var h = MathF.Sqrt(h2);
        var a = o + d * (tc - h); var b = o + d * (tc + h);
        if (!_valid[k]) return Vector3.Dot(a - p, p - o) < 0 ? a : b;
        return Vector3.DistanceSquared(a, prev) <= Vector3.DistanceSquared(b, prev) ? a : b;
    }

    private float Euro(int i, float x, float dt)
    {
        var dx = (x - _fx[i]) / dt;
        var ad = Alpha(DCutoff, dt);
        _fdx[i] += (dx - _fdx[i]) * ad;
        var cutoff = MinCutoff + Beta * MathF.Abs(_fdx[i]);
        _fx[i] += (x - _fx[i]) * Alpha(cutoff, dt);
        return _fx[i];
    }

    private static float Alpha(float cutoff, float dt)
    {
        var tau = 1f / (MathF.Tau * cutoff);
        return 1f / (1f + tau / dt);
    }

    // ══════════════════════ 손 · 표정 특징 ══════════════════════

    /// <summary>손가락 굽힘 0..1(두 지절 간 꺾임 합 기반). hand 0 = 왼손.</summary>
    public float FingerCurl(int hand, int finger)
    {
        var b = (hand == 0 ? MocapSkeleton.LeftHandBase : MocapSkeleton.RightHandBase) + 1 + finger * 4;
        var s1 = _out[b + 1] - _out[b]; var s2 = _out[b + 2] - _out[b + 1]; var s3 = _out[b + 3] - _out[b + 2];
        var bend = Angle(s1, s2) + Angle(s2, s3);
        return Math.Clamp(bend * 3f / (2f * (finger == 0 ? 0.6f : 1.35f)), 0f, 1f);
    }

    private static float Angle(Vector3 a, Vector3 b)
    {
        var la = a.Length(); var lb = b.Length();
        if (la < 1e-6f || lb < 1e-6f) return 0;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f));
    }

    /// <summary>표정 키포인트 → 블렌드셰이프 16(<see cref="FaceShapes"/>) — 얼굴 국소 좌표계(눈 축 · 눈썹-턱 축) 기준 · 눈 간격으로 스케일 정규화.</summary>
    public void FaceBlendshapes(Span<float> w)
    {
        w[..FaceShapes.Count].Clear();
        var f = MocapSkeleton.FaceBase;
        var x = _out[f + 2] - _out[f + 3];
        var scale = x.Length() / 0.066f;
        if (scale < 1e-3f) return;
        x = Vector3.Normalize(x);
        var upRaw = (_out[f] + _out[f + 1]) * 0.5f - _out[f + 9];
        var z = Vector3.Normalize(Vector3.Cross(x, upRaw));
        var y = Vector3.Cross(z, x);
        var nose = _out[f + 4];
        float Y(int i) => Vector3.Dot(_out[f + i] - nose, y) / scale;
        var lip = Vector3.Distance(_out[f + 7], _out[f + 8]) / scale;
        w[FaceShapes.JawOpen] = Math.Clamp((lip - 0.02f) / 0.03f, 0f, 1f);
        // 입꼬리 높이 − 윗입술 높이(레스트 대비) — 턱 개방(아랫입술)과 독립
        var corner = (Y(5) + Y(6)) * 0.5f - Y(7) - (MocapSkeleton.FaceRest[5].Y - MocapSkeleton.FaceRest[7].Y);
        w[FaceShapes.MouthSmile] = Math.Clamp(corner / 0.015f, 0f, 1f);
        w[FaceShapes.MouthFrown] = Math.Clamp(-corner / 0.015f, 0f, 1f);
        var brow = (Y(0) + Y(1)) * 0.5f - MocapSkeleton.FaceRest[0].Y;
        w[FaceShapes.BrowInnerUp] = Math.Clamp(brow / 0.015f, 0f, 1f);
        w[FaceShapes.BrowDownL] = w[FaceShapes.BrowDownR] = Math.Clamp(-brow / 0.012f, 0f, 1f);
        w[FaceShapes.EyeBlinkL] = Math.Clamp((MocapSkeleton.FaceRest[2].Y - Y(2)) / 0.012f, 0f, 1f);
        w[FaceShapes.EyeBlinkR] = Math.Clamp((MocapSkeleton.FaceRest[3].Y - Y(3)) / 0.012f, 0f, 1f);
    }
}

/// <summary>표정 블렌드셰이프 16(ARKit 부분집합).</summary>
public static class FaceShapes
{
    public const int Count = 16;
    public const int BrowInnerUp = 0, BrowDownL = 1, BrowDownR = 2, BrowOuterUp = 3, EyeBlinkL = 4, EyeBlinkR = 5, EyeWide = 6, EyeSquint = 7,
        JawOpen = 8, MouthSmile = 9, MouthFrown = 10, MouthPucker = 11, MouthFunnel = 12, MouthStretch = 13, CheekPuff = 14, NoseSneer = 15;

    public static readonly string[] Names =
    {
        "browInnerUp", "browDown_L", "browDown_R", "browOuterUp", "eyeBlink_L", "eyeBlink_R", "eyeWide", "eyeSquint",
        "jawOpen", "mouthSmile", "mouthFrown", "mouthPucker", "mouthFunnel", "mouthStretch", "cheekPuff", "noseSneer",
    };
}
