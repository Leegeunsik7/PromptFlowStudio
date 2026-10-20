using System.Numerics;
namespace PromptFlowStudio.Services.OpticalInertialTracking;

/// <summary>
/// [PromptFlow Studio v17.45.0] 📐 렌더 파이프라인 레이턴시 선제 예측 &amp; 시차 보상 합성기(<see cref="Push"/> · <see cref="Issue"/> · <see cref="Evaluate"/> 무할당).
/// 융합 포즈(위치 · 속도 · 오일러 · 각속도)를 128 프레임 링버퍼에 적재하며 가속도/저크를 EWMA 유한차분으로 추정하고,
/// v17.44 LED 월 렌더 지연(측정 EWMA 또는 수동 16.7~33.3 ms) 만큼 미래 포즈를 3차 테일러 전개(p + v·h + ½a·h² + ⅙j·h³)로 선제 예측한다.
/// 렌더에 송출한 원시/예측 포즈를 각각 링에 기록하고, 표시 시점(now − 지연)의 포즈를 3차 에르미트 스플라인으로 재표본화해
/// 실제 카메라 포즈 대비 인너 프러스텀 배경 시차 미끄러짐(Swim, px = 초점 px × (각 오차 + 위치 오차 / 월 거리))을 상쇄 전/후로 집계한다.
/// </summary>
public sealed class RenderLatencyParallaxPredictionSynthesizer
{
    public const int Cap = 128;
    public const double FocalPx = 1662.8, WallDistM = 4.0, AccBeta = 0.5, JerkBeta = 0.25, EwmaA = 0.05, MinMs = 0.0, MaxMs = 50.0;
    private readonly double[] _t = new double[Cap], _it = new double[Cap];
    private readonly Vector3[] _p = new Vector3[Cap], _v = new Vector3[Cap], _e = new Vector3[Cap], _w = new Vector3[Cap];
    private readonly Vector3[] _rp = new Vector3[Cap], _rv = new Vector3[Cap], _re = new Vector3[Cap], _rw = new Vector3[Cap];
    private readonly Vector3[] _qp = new Vector3[Cap], _qv = new Vector3[Cap], _qe = new Vector3[Cap], _qw = new Vector3[Cap];
    private Vector3 _a, _j, _aa, _aj;
    private int _head, _count, _ihead, _icount;

    public bool Enabled { get; set; } = true;
    /// <summary>0 = 자동(측정 지연).</summary>
    public double PredictionMs { get; set; }
    public double MeasuredLatencyMs { get; private set; } = 25.0;
    public double HorizonMs => PredictionMs > 0 ? PredictionMs : MeasuredLatencyMs;
    public Vector3 Accel => _a;
    public Vector3 Jerk => _j;
    public Vector3 PredPos { get; private set; }
    public Vector3 PredEuler { get; private set; }
    public double CompensationPx { get; private set; }
    public double SwimRawPx { get; private set; }
    public double SwimPredPx { get; private set; }
    public double SwimRawAvg { get; private set; }
    public double SwimPredAvg { get; private set; }
    public double MaxSwimPredPx { get; private set; }
    public long Evaluated { get; private set; }
    public int Count => _count;
    public double ReductionPct => SwimRawAvg > 1e-9 ? 100.0 * (1.0 - SwimPredAvg / SwimRawAvg) : 0.0;

    public void Reset()
    {
        _head = _count = _ihead = _icount = 0;
        _a = _j = _aa = _aj = Vector3.Zero;
        ResetMetrics();
    }

    public void ResetMetrics() { SwimRawAvg = SwimPredAvg = MaxSwimPredPx = 0; Evaluated = 0; }

    /// <summary>렌더 피드백 타임스탬프 에코로 지연 측정(EWMA).</summary>
    public void ReportLatency(double ms) => MeasuredLatencyMs += 0.1 * (Math.Clamp(ms, MinMs, MaxMs) - MeasuredLatencyMs);

    /// <summary>융합 포즈 적재 — 가속도/저크 유한차분 EWMA.</summary>
    public void Push(double t, Vector3 pos, Vector3 vel, Vector3 euler, Vector3 rate)
    {
        if (_count > 0)
        {
            var pv = (_head - 1 + Cap) % Cap;
            var dt = t - _t[pv];
            if (dt > 1e-6)
            {
                var inv = (float)(1.0 / dt);
                var a = (vel - _v[pv]) * inv; var aa = (rate - _w[pv]) * inv;
                var na = _a + (float)AccBeta * (a - _a); var naa = _aa + (float)AccBeta * (aa - _aa);
                _j += (float)JerkBeta * ((na - _a) * inv - _j); _aj += (float)JerkBeta * ((naa - _aa) * inv - _aj);
                _a = na; _aa = naa;
            }
        }
        _t[_head] = t; _p[_head] = pos; _v[_head] = vel; _e[_head] = euler; _w[_head] = rate;
        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
    }

    /// <summary>3차 테일러 예측(h 초) — 위치/오일러 및 해당 시점 속도.</summary>
    public void Predict(double h, out Vector3 pos, out Vector3 vel, out Vector3 euler, out Vector3 rate)
    {
        var k = (_head - 1 + Cap) % Cap;
        var hf = (float)h; var h2 = 0.5f * hf * hf; var h3 = hf * hf * hf / 6f;
        pos = _p[k] + _v[k] * hf + _a * h2 + _j * h3;
        vel = _v[k] + _a * hf + _j * h2;
        euler = _e[k] + _w[k] * hf + _aa * h2 + _aj * h3;
        rate = _w[k] + _aa * hf + _aj * h2;
    }

    /// <summary>렌더 송출: 원시 · 예측 포즈를 송출 링에 기록하고 LED 월로 보낼 포즈 반환.</summary>
    public void Issue(out Vector3 pos, out Vector3 euler)
    {
        var k = (_head - 1 + Cap) % Cap;
        var t = _t[k];
        Predict(HorizonMs * 1e-3, out var pp, out var pv, out var pe, out var pw);
        if (!Enabled) { pp = _p[k]; pv = _v[k]; pe = _e[k]; pw = _w[k]; }
        _it[_ihead] = t;
        _rp[_ihead] = _p[k]; _rv[_ihead] = _v[k]; _re[_ihead] = _e[k]; _rw[_ihead] = _w[k];
        _qp[_ihead] = pp; _qv[_ihead] = pv; _qe[_ihead] = pe; _qw[_ihead] = pw;
        _ihead = (_ihead + 1) % Cap;
        if (_icount < Cap) _icount++;
        PredPos = pp; PredEuler = pe;
        CompensationPx = Swim(pp - _p[k], pe - _e[k]);
        pos = pp; euler = pe;
    }

    /// <summary>표시 시점 now 의 화면 포즈 = now − 지연 시점 송출 포즈(에르미트 재표본화) → 실제 포즈 대비 Swim(px).</summary>
    public bool Evaluate(double now, double latencySec, Vector3 truthPos, Vector3 truthEuler)
    {
        var tq = now - latencySec;
        if (!SampleIssued(tq, false, out var rp, out var re) || !SampleIssued(tq, true, out var qp, out var qe)) return false;
        SwimRawPx = Swim(truthPos - rp, truthEuler - re);
        SwimPredPx = Swim(truthPos - qp, truthEuler - qe);
        if (Evaluated == 0) { SwimRawAvg = SwimRawPx; SwimPredAvg = SwimPredPx; }
        else { SwimRawAvg += EwmaA * (SwimRawPx - SwimRawAvg); SwimPredAvg += EwmaA * (SwimPredPx - SwimPredAvg); }
        if (SwimPredPx > MaxSwimPredPx) MaxSwimPredPx = SwimPredPx;
        Evaluated++;
        return true;
    }

    public static double Swim(Vector3 dPos, Vector3 dEuler)
    {
        var dp = RealtimeOpticalInertialCameraTrackingEngine.WrapRad(dEuler.X);
        var ang = Math.Sqrt(dp * dp + (double)dEuler.Y * dEuler.Y) + 0.25 * Math.Abs(dEuler.Z);
        return FocalPx * (ang + dPos.Length() / WallDistM);
    }

    /// <summary>송출 링 3차 에르미트 표본화(접선 = 저장 속도/각속도).</summary>
    public bool SampleIssued(double tq, bool predicted, out Vector3 pos, out Vector3 euler)
    {
        pos = default; euler = default;
        if (_icount < 2) return false;
        var P = predicted ? _qp : _rp; var V = predicted ? _qv : _rv; var E = predicted ? _qe : _re; var W = predicted ? _qw : _rw;
        var k1 = (_ihead - 1 + Cap) % Cap;
        for (var n = 1; n < _icount; n++)
        {
            var k0 = (k1 - 1 + Cap) % Cap;
            if (_it[k0] <= tq && tq <= _it[k1])
            {
                var h = _it[k1] - _it[k0];
                var s = h > 1e-9 ? (float)((tq - _it[k0]) / h) : 0f;
                var hf = (float)h;
                var e1 = E[k1];
                e1.X = E[k0].X + (float)RealtimeOpticalInertialCameraTrackingEngine.WrapRad(e1.X - E[k0].X);
                pos = Hermite(P[k0], V[k0], P[k1], V[k1], s, hf);
                euler = Hermite(E[k0], W[k0], e1, W[k1], s, hf);
                return true;
            }
            k1 = k0;
        }
        return false;
    }

    public static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float s, float h)
    {
        float s2 = s * s, s3 = s2 * s;
        return (2f * s3 - 3f * s2 + 1f) * p0 + (s3 - 2f * s2 + s) * h * m0 + (-2f * s3 + 3f * s2) * p1 + (s3 - s2) * h * m1;
    }
}
