using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.HairCloth;

public enum HairStyle : byte { Long, Bob, Curly, Fur }

/// <summary>
/// [PromptFlow Studio v16.83.0] 💇 실시간 헤어 스트랜드 · 모피 다이내믹스(<see cref="Step"/> 무할당).
/// 가닥 = 8 노드(모근 고정) SoA · 동역학 = DFTL(Müller 2012 · 동적 Follow-The-Leader: 비신장 정확 + 속도 보정 감쇠) +
/// 그룸 형상 유지(머리 좌표 휴지 형상 → 머리 포즈 아핀 [SIMD] → 노드별 강성 이완 [SIMD] · 스텝 길이 정규화) ·
/// 중력/감쇠 [SIMD] · 가닥별 돌풍(난류 위상)+공기 저항 · 머리 타원체 + 목/어깨/몸통 캡슐 충돌.
/// 모근 = R2 저불일치 수열(점진 균일 → 활성 가닥 수 변경 시에도 두피 균등 분포) · 스타일 4(롱 · 단발 · 곱슬 · 모피).
/// </summary>
public sealed class NeuralStrandHairDynamicsEngine
{
    public const int NodesPerStrand = 8, DefaultCapacity = 24000;
    public const float CollisionMargin = 0.003f, ReferenceDt = 1f / 60f;

    public readonly int Capacity;
    public float[] X, Y, Z, PX, PY, PZ, VX, VY, VZ;
    public readonly float[] TX, TY, TZ, RX, RY, RZ, DX, DY, DZ, K, KEff;
    private readonly float[] _segLen, _phase, _sp, _cp, _sp2, _cp2;
    private readonly Vector3[] _dir;
    private float _kDt = -1f, _time;
    private int _active;

    public NeuralStrandHairDynamicsEngine(int capacity = DefaultCapacity)
    {
        Capacity = Math.Max(1, capacity);
        var n = Capacity * NodesPerStrand;
        X = new float[n]; Y = new float[n]; Z = new float[n]; PX = new float[n]; PY = new float[n]; PZ = new float[n];
        VX = new float[n]; VY = new float[n]; VZ = new float[n];
        TX = new float[n]; TY = new float[n]; TZ = new float[n]; RX = new float[n]; RY = new float[n]; RZ = new float[n];
        DX = new float[n]; DY = new float[n]; DZ = new float[n]; K = new float[n]; KEff = new float[n];
        _segLen = new float[Capacity]; _phase = new float[Capacity]; _sp = new float[Capacity]; _cp = new float[Capacity]; _sp2 = new float[Capacity]; _cp2 = new float[Capacity]; _dir = new Vector3[Capacity];
        GenerateRoots();
        for (var s = 0; s < Capacity; s++)
        {
            _sp[s] = MathF.Sin(_phase[s]); _cp[s] = MathF.Cos(_phase[s]); _sp2[s] = MathF.Sin(2.1f * _phase[s]); _cp2[s] = MathF.Cos(2.1f * _phase[s]);
        }
        _active = Capacity;
        SetStyle(HairStyle.Long);
    }

    public HumanBodyColliderRig Body { get; set; } = new();
    public HairStyle Style { get; private set; }
    public float Length { get; private set; }
    public float Stiffness { get; private set; }
    public float Curl { get; private set; }
    public Vector3 Wind { get; set; }
    public float Turbulence { get; set; } = 0.35f;
    public float Drag { get; set; } = 2.2f;
    public float Damping { get; set; } = 0.6f;
    public float FtlDamping { get; set; } = 0.9f;
    public float Gravity { get; set; } = -9.81f;

    public int ActiveStrands => _active;
    public int NodeCount => _active * NodesPerStrand;
    public float SegmentLength(int s) => _segLen[s];
    public Vector3 Position(int node) => new(X[node], Y[node], Z[node]);
    public Vector3 Target(int node) => new(TX[node], TY[node], TZ[node]);
    public Vector3 RootDirection(int s) => _dir[s];

    public double LastStepMs { get; private set; }
    public double MaxStepMs { get; private set; }
    public long Steps { get; private set; }
    public long Contacts { get; private set; }
    public int LastContacts { get; private set; }
    public float MaxStretchError { get; private set; }
    public float MeanTipSpeed { get; private set; }
    public float MeanTipDeviation { get; private set; }
    public string? LastAction { get; private set; }

    public static string StyleName(HairStyle s) => s switch
    {
        HairStyle.Bob => "단발", HairStyle.Curly => "곱슬", HairStyle.Fur => "모피", _ => "롱 헤어",
    };

    // ───────────────────────── 그룸 ─────────────────────────

    private static float Frac(double x) => (float)(x - Math.Floor(x));

    /// <summary>두피 영역(이마 헤어라인 · 관자 · 뒤통수 · 목덜미 위).</summary>
    public static bool OnScalp(Vector3 d) =>
        d.Y > 0.3f || (d.Z < -0.15f && d.Y > -0.5f) || (MathF.Abs(d.X) > 0.72f && d.Y > -0.15f && d.Z < 0.3f);

    private void GenerateRoots()
    {
        var accepted = 0;
        for (var i = 0; accepted < Capacity && i < Capacity * 8; i++)
        {
            // R2 수열(Roberts 2018) → 구면 균등
            var u = Frac(0.5 + i * 0.7548776662466927); var v = Frac(0.5 + i * 0.5698402909980532);
            var zc = 1f - 2f * v; var r = MathF.Sqrt(MathF.Max(0f, 1f - zc * zc)); var a = 2f * MathF.PI * u;
            var d = new Vector3(r * MathF.Cos(a), zc, r * MathF.Sin(a));
            if (!OnScalp(d)) continue;
            _dir[accepted] = d;
            _phase[accepted] = Frac(i * 0.6180339887) * 6.2831853f;
            accepted++;
        }
        for (var s = accepted; s < Capacity; s++) { _dir[s] = _dir[s % Math.Max(accepted, 1)]; _phase[s] = _phase[s % Math.Max(accepted, 1)]; }
    }

    private static readonly HumanBodyColliderRig RestRig = new();

    /// <summary>스타일 → 휴지 형상(머리 좌표) · 강성 → 포즈 재배치.</summary>
    public void SetStyle(HairStyle style)
    {
        Style = style;
        float bend, k0;
        (Length, bend, Curl, k0) = style switch
        {
            HairStyle.Bob => (0.15f, 1.2f, 0f, 0.35f),
            HairStyle.Curly => (0.22f, 1.1f, 0.014f, 0.45f),
            HairStyle.Fur => (0.035f, 0.15f, 0f, 0.8f),
            _ => (0.32f, 1.35f, 0f, 0.25f),
        };
        Stiffness = k0;
        var tip = style == HairStyle.Fur ? 0.75f : 0.08f; // 모피 = 전 구간 강성(직립)
        const int N = NodesPerStrand;
        for (var s = 0; s < Capacity; s++)
        {
            var d = _dir[s];
            var root = d * HumanBodyColliderRig.ScalpRadii;
            var nrm = Vector3.Normalize(d / (HumanBodyColliderRig.ScalpRadii * HumanBodyColliderRig.ScalpRadii));
            var comb = Vector3.Normalize(new Vector3(0.35f * d.X, -1f, 0.35f * d.Z - 0.15f));
            var h = Frac(s * 0.41421356) ; var len = Length * (0.85f + 0.3f * h) / (N - 1);
            _segLen[s] = len;
            // 곱슬 나선 기저(빗질 방향 수직)
            var b1 = Vector3.Normalize(Vector3.Cross(comb, MathF.Abs(comb.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)); var b2 = Vector3.Cross(comb, b1);
            var b = s * N;
            RX[b] = root.X; RY[b] = root.Y; RZ[b] = root.Z;
            var prev = root; var prevOff = Vector3.Zero;
            for (var k = 1; k < N; k++)
            {
                var t = Vector3.Normalize(Vector3.Lerp(nrm, comb, MathF.Min(1f, bend * k / (N - 1))));
                var ang = 2f * MathF.PI * 2.5f * k / (N - 1) + _phase[s];
                var off = Curl * (MathF.Cos(ang) * b1 + MathF.Sin(ang) * b2) * MathF.Min(1f, k / 2f);
                var p = prev + t * len + (off - prevOff);
                for (var it = 0; it < 3; it++)
                {
                    RestRig.Resolve(ref p, CollisionMargin + 0.002f, true, out _);
                    p = prev + Vector3.Normalize(p - prev) * len;
                }
                var i = b + k;
                RX[i] = p.X; RY[i] = p.Y; RZ[i] = p.Z;
                prev = p; prevOff = off;
                var f = 1f - (float)k / (N - 1);
                K[i] = k0 * (tip + (1f - tip) * f * f);
            }
            K[b] = 1f;
        }
        _kDt = -1f;
        Reset();
        LastAction = "💇 스타일 " + StyleName(style);
    }

    /// <summary>활성 가닥 수(1..용량 · 무할당) — 새로 활성화된 가닥은 그룸 포즈로 배치.</summary>
    public void SetActiveStrands(int n)
    {
        n = Math.Clamp(n, 1, Capacity);
        var old = _active; _active = n;
        if (n > old) PlaceRest(old, n);
    }

    /// <summary>전 가닥 = 현재 머리 포즈의 그룸 형상 · 속도 0.</summary>
    public void Reset()
    {
        PlaceRest(0, _active);
        _time = 0f;
    }

    private void PlaceRest(int s0, int s1)
    {
        var m = Body.HeadPose;
        for (var i = s0 * NodesPerStrand; i < s1 * NodesPerStrand; i++)
        {
            var p = Vector3.Transform(new Vector3(RX[i], RY[i], RZ[i]), m);
            X[i] = TX[i] = p.X; Y[i] = TY[i] = p.Y; Z[i] = TZ[i] = p.Z;
            VX[i] = VY[i] = VZ[i] = 0f; DX[i] = DY[i] = DZ[i] = 0f;
        }
    }

    // ───────────────────────── 스텝 ─────────────────────────

    /// <summary>1스텝(무할당).</summary>
    public void Step(float dt)
    {
        if (dt <= 0f) return;
        var t0 = Stopwatch.GetTimestamp();
        const int N = NodesPerStrand;
        var n = _active * N;
        _time += dt;
        if (dt != _kDt)
        {
            // 스텝 길이 정규화: 기준 60 Hz 에서 k → 임의 dt 에서 동일 시상수
            var e = dt / ReferenceDt;
            for (var i = 0; i < K.Length; i++) KEff[i] = 1f - MathF.Pow(1f - MathF.Min(K[i], 0.999f), e);
            _kDt = dt;
        }
        var m = Body.HeadPose;
        // ① 형상 목표(머리 포즈 아핀) [SIMD]
        StrandSimd.Affine(RX.AsSpan(0, n), RY.AsSpan(0, n), RZ.AsSpan(0, n), m.M11, m.M21, m.M31, m.M41, TX.AsSpan(0, n));
        StrandSimd.Affine(RX.AsSpan(0, n), RY.AsSpan(0, n), RZ.AsSpan(0, n), m.M12, m.M22, m.M32, m.M42, TY.AsSpan(0, n));
        StrandSimd.Affine(RX.AsSpan(0, n), RY.AsSpan(0, n), RZ.AsSpan(0, n), m.M13, m.M23, m.M33, m.M43, TZ.AsSpan(0, n));
        // ② 감쇠 + 중력 [SIMD]
        var damp = MathF.Max(0f, 1f - Damping * dt);
        StrandSimd.ScaleAdd(VX.AsSpan(0, n), damp, 0f);
        StrandSimd.ScaleAdd(VY.AsSpan(0, n), damp, Gravity * dt);
        StrandSimd.ScaleAdd(VZ.AsSpan(0, n), damp, 0f);
        // ③ 바람(가닥별 돌풍 · 끝단 가중 공기 저항)
        var w = Wind; var wl = w.Length();
        if (wl > 1e-4f || Drag > 0f)
        {
            var kd = MathF.Min(1f, Drag * dt);
            // 돌풍 위상 합성: sin(ωt + φ) = sin ωt·cos φ + cos ωt·sin φ (프레임당 삼각함수 8회 · 가닥당 0회)
            float s1 = MathF.Sin(_time * 1.7f), c1 = MathF.Cos(_time * 1.7f), s2 = MathF.Sin(_time * 4.3f), c2 = MathF.Cos(_time * 4.3f);
            float s3 = MathF.Sin(_time * 2.3f), c3 = MathF.Cos(_time * 2.3f), s4 = MathF.Sin(_time * 2.9f), c4 = MathF.Cos(_time * 2.9f);
            var sw = Turbulence * wl * 0.3f;
            for (var s = 0; s < _active; s++)
            {
                float sn = _sp[s], cp = _cp[s];
                var g = 1f + Turbulence * (0.6f * (s1 * cp + c1 * sn) + 0.4f * (s2 * _cp2[s] + c2 * _sp2[s]));
                float wx = w.X * g + sw * (c3 * cp - s3 * sn), wy = w.Y * g, wz = w.Z * g + sw * (s4 * cp + c4 * sn);
                var b = s * N;
                for (var k = 1; k < N; k++)
                {
                    var i = b + k; var c = kd * (0.4f + 0.6f * k / (N - 1));
                    VX[i] += c * (wx - VX[i]); VY[i] += c * (wy - VY[i]); VZ[i] += c * (wz - VZ[i]);
                }
            }
        }
        // ④ 예측 [SIMD] → ⑤ 형상 이완 [SIMD]
        StrandSimd.Predict(X.AsSpan(0, n), VX.AsSpan(0, n), PX.AsSpan(0, n), dt);
        StrandSimd.Predict(Y.AsSpan(0, n), VY.AsSpan(0, n), PY.AsSpan(0, n), dt);
        StrandSimd.Predict(Z.AsSpan(0, n), VZ.AsSpan(0, n), PZ.AsSpan(0, n), dt);
        StrandSimd.Relax(PX.AsSpan(0, n), TX.AsSpan(0, n), KEff.AsSpan(0, n));
        StrandSimd.Relax(PY.AsSpan(0, n), TY.AsSpan(0, n), KEff.AsSpan(0, n));
        StrandSimd.Relax(PZ.AsSpan(0, n), TZ.AsSpan(0, n), KEff.AsSpan(0, n));
        // ⑥ FTL(비신장) + 충돌
        var contacts = 0; var maxErr = 0f; var body = Body;
        for (var s = 0; s < _active; s++)
        {
            var b = s * N; var len = _segLen[s];
            PX[b] = TX[b]; PY[b] = TY[b]; PZ[b] = TZ[b]; DX[b] = DY[b] = DZ[b] = 0f;
            float ax = PX[b], ay = PY[b], az = PZ[b];
            for (var k = 1; k < N; k++)
            {
                var i = b + k;
                float dx = PX[i] - ax, dy = PY[i] - ay, dz = PZ[i] - az;
                var l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                var sc = l > 1e-9f ? len / l : 0f;
                var p = l > 1e-9f ? new Vector3(ax + dx * sc, ay + dy * sc, az + dz * sc) : new Vector3(ax, ay - len, az);
                if (body.MayCollideHair(p.X, p.Y, p.Z, CollisionMargin) && body.Resolve(ref p, CollisionMargin, true, out _))
                {
                    // 길이 재투영 ↔ 재충돌(3회) — 충돌 세그먼트 신장 억제
                    var a0 = new Vector3(ax, ay, az);
                    for (var it = 0; it < 3; it++)
                    {
                        var q = p - a0; var ql = q.Length();
                        if (ql > 1e-9f) p = a0 + q * (len / ql);
                        if (!body.Resolve(ref p, CollisionMargin, true, out _)) break;
                    }
                    contacts++;
                    var e = MathF.Abs(Vector3.Distance(p, new Vector3(ax, ay, az)) - len) / len;
                    if (e > maxErr) maxErr = e;
                }
                DX[i] = p.X - PX[i]; DY[i] = p.Y - PY[i]; DZ[i] = p.Z - PZ[i];
                PX[i] = p.X; PY[i] = p.Y; PZ[i] = p.Z;
                ax = p.X; ay = p.Y; az = p.Z;
            }
        }
        // ⑦ 속도 = (p − x)/dt − s·d(i+1)/dt [SIMD · 모근 d = 0 이 가닥 경계 차단]
        var inv = 1f / dt;
        StrandSimd.VelocityFrom(VX.AsSpan(0, n), PX.AsSpan(0, n), X.AsSpan(0, n), inv);
        StrandSimd.VelocityFrom(VY.AsSpan(0, n), PY.AsSpan(0, n), Y.AsSpan(0, n), inv);
        StrandSimd.VelocityFrom(VZ.AsSpan(0, n), PZ.AsSpan(0, n), Z.AsSpan(0, n), inv);
        if (n > 1)
        {
            var c = -FtlDamping * inv;
            StrandSimd.AddScaled(VX.AsSpan(0, n - 1), DX.AsSpan(1, n - 1), c);
            StrandSimd.AddScaled(VY.AsSpan(0, n - 1), DY.AsSpan(1, n - 1), c);
            StrandSimd.AddScaled(VZ.AsSpan(0, n - 1), DZ.AsSpan(1, n - 1), c);
        }
        // ⑧ 커밋(배열 교환 · 무할당)
        (X, PX) = (PX, X); (Y, PY) = (PY, Y); (Z, PZ) = (PZ, Z);
        // 진단(끝단)
        float sp = 0f, dev = 0f;
        for (var s = 0; s < _active; s++)
        {
            var i = s * N + N - 1;
            sp += MathF.Sqrt(VX[i] * VX[i] + VY[i] * VY[i] + VZ[i] * VZ[i]);
            float ex = X[i] - TX[i], ey = Y[i] - TY[i], ez = Z[i] - TZ[i];
            dev += MathF.Sqrt(ex * ex + ey * ey + ez * ez);
        }
        MeanTipSpeed = sp / _active; MeanTipDeviation = dev / _active;
        LastContacts = contacts; Contacts += contacts; MaxStretchError = maxErr;
        Steps++;
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastStepMs = ms; if (ms > MaxStepMs) MaxStepMs = ms;
    }

    /// <summary>진단: 전 세그먼트 최대 상대 길이 오차.</summary>
    public float MeasureStretch()
    {
        var m = 0f;
        for (var s = 0; s < _active; s++)
        {
            var b = s * NodesPerStrand; var len = _segLen[s];
            for (var k = 1; k < NodesPerStrand; k++)
            {
                var e = MathF.Abs(Vector3.Distance(Position(b + k), Position(b + k - 1)) - len) / len;
                if (e > m) m = e;
            }
        }
        return m;
    }

    /// <summary>진단: 최대 관통 깊이(m).</summary>
    public float MeasurePenetration()
    {
        var m = 0f;
        for (var s = 0; s < _active; s++)
            for (var k = 1; k < NodesPerStrand; k++) m = MathF.Max(m, Body.Penetration(Position(s * NodesPerStrand + k), true));
        return m;
    }

    /// <summary>진단: 끝단 평균 위치.</summary>
    public Vector3 MeanTip()
    {
        var a = Vector3.Zero;
        for (var s = 0; s < _active; s++) a += Position(s * NodesPerStrand + NodesPerStrand - 1);
        return a / _active;
    }
}
