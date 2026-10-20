using System.Numerics;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SplatRelighting;

/// <summary>
/// [PromptFlow Studio v17.56.0] 💡 3DGS 환경광 역추출 &amp; 액터 상호 릴라이팅 합성기(<see cref="Step"/> 무할당).
/// 매 프레임 장면을 16 384 스플랫씩 순환 스캔해 배우 중심 반경 4 m 안(불투명도 &gt; 0.02 · 배우 스플랫 제외) 입자의 방사휘도를
/// v17.55 SH 로 배우 방향에서 평가(가중 = 불투명도 / (1 + d²))하고, 2048 슬롯 링버퍼(6 축 방향 버킷 · 슬라이딩 합)에 누적한다.
/// 출력: 앰비언트(전체 평균) · 바운스(−Y 버킷 = 지면 반사) · 림(카메라 반대편 버킷 · 방향) · 색온도(McCamy CCT · sRGB→XYZ) · 휘도.
/// 듀얼웨이: 환경 → 배우(디지털 휴먼 피부 셰이더 앰비언트/림/필 · v17.21 DIT 키 CCT) · 배우 → 환경(섀도우 엔진 바운스/그림자).
/// </summary>
public sealed class SplatEnvironmentMutualRelightingSynthesizer
{
    public const int Ring = 2048, ScanPerStep = 16384, Buckets = 6;
    public const float MinOpacity = 0.02f;
    private static readonly Vector3[] Axis = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
    private readonly float[] _r = new float[Ring], _g = new float[Ring], _b = new float[Ring], _w = new float[Ring];
    private readonly byte[] _k = new byte[Ring];
    private readonly double[] _sum = new double[Buckets * 4];
    private readonly float[] _y = new float[16];
    private int _head, _n, _cursor;

    public float RadiusM { get; set; } = 4f;
    /// <summary>환경 반사 강도(0..2) — 배우 셰이더 주입 게인.</summary>
    public float Gain { get; set; } = 1f;
    public Vector3 Ambient { get; private set; }
    public Vector3 Bounce { get; private set; }
    public Vector3 Rim { get; private set; }
    public Vector3 RimDir { get; private set; } = -Vector3.UnitZ;
    public float CctK { get; private set; } = 6500f;
    public float Luminance { get; private set; }
    public int Samples => _n;
    public long Steps { get; private set; }
    public double LastStepUs { get; private set; }
    public Vector3 BucketMean(int b) { var o = b * 4; var w = _sum[o + 3]; return w > 1e-9 ? new Vector3((float)(_sum[o] / w), (float)(_sum[o + 1] / w), (float)(_sum[o + 2] / w)) : Vector3.Zero; }
    public static Vector3 BucketAxis(int b) => Axis[b];

    public void Reset() { Array.Clear(_sum); _head = _n = _cursor = 0; Ambient = Bounce = Rim = Vector3.Zero; CctK = 6500f; Luminance = 0; Steps = 0; }

    private static int Bucket(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return d.X >= 0 ? 0 : 1;
        if (ay >= az) return d.Y >= 0 ? 2 : 3;
        return d.Z >= 0 ? 4 : 5;
    }

    /// <summary>McCamy CCT(선형 sRGB → XYZ → xy).</summary>
    public static float Cct(Vector3 rgb)
    {
        var X = 0.4124f * rgb.X + 0.3576f * rgb.Y + 0.1805f * rgb.Z;
        var Y = 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;
        var Z = 0.0193f * rgb.X + 0.1192f * rgb.Y + 0.9505f * rgb.Z;
        var s = X + Y + Z;
        if (!(s > 1e-6f)) return 6500f;
        float x = X / s, y = Y / s;
        var n = (x - 0.3320f) / (0.1858f - y);
        return Math.Clamp(449f * n * n * n + 3525f * n * n + 6823.3f * n + 5520.33f, 1500f, 15000f);
    }

    private void Push(int bucket, Vector3 c, float w)
    {
        if (_n == Ring)
        {
            var ob = _k[_head] * 4; var ow = _w[_head];
            _sum[ob] -= _r[_head] * ow; _sum[ob + 1] -= _g[_head] * ow; _sum[ob + 2] -= _b[_head] * ow; _sum[ob + 3] -= ow;
        }
        else _n++;
        _k[_head] = (byte)bucket; _r[_head] = c.X; _g[_head] = c.Y; _b[_head] = c.Z; _w[_head] = w;
        var o = bucket * 4;
        _sum[o] += c.X * w; _sum[o + 1] += c.Y * w; _sum[o + 2] += c.Z * w; _sum[o + 3] += w;
        _head = (_head + 1) % Ring;
    }

    /// <summary>프레임 샘플링 + 출력 갱신(무할당).</summary>
    public void Step(GaussianSplatScene s, SphericalHarmonicsLightingSynthesizer sh, Vector3 actorCenter, Vector3 cameraPos)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Steps++;
        var cnt = s.Count;
        if (cnt > 0)
        {
            var r2 = RadiusM * RadiusM;
            var scan = Math.Min(ScanPerStep, cnt);
            if (_cursor >= cnt) _cursor = 0;
            for (var k = 0; k < scan; k++)
            {
                var i = _cursor; if (++_cursor >= cnt) _cursor = 0;
                if (s.Kind[i] == RealtimeSplatShadowCastingEngine.ActorKind || !(s.Opacity[i] > MinOpacity)) continue;
                var d = new Vector3(s.X[i] - actorCenter.X, s.Y[i] - actorCenter.Y, s.Z[i] - actorCenter.Z);
                var d2 = d.LengthSquared();
                if (d2 < 0.04f || d2 > r2) continue;
                var c = sh.Color(s, i, d, _y);
                Push(Bucket(d), c, s.Opacity[i] / (1f + d2));
            }
        }
        double tr = 0, tg = 0, tb = 0, tw = 0;
        for (var b = 0; b < Buckets; b++) { var o = b * 4; tr += _sum[o]; tg += _sum[o + 1]; tb += _sum[o + 2]; tw += _sum[o + 3]; }
        if (tw > 1e-9)
        {
            var amb = new Vector3((float)(tr / tw), (float)(tg / tw), (float)(tb / tw));
            Ambient = amb * (0.25f * Gain);
            Bounce = BucketMean(3) * (0.35f * Gain);
            var rd = actorCenter - cameraPos; rd.Y = 0f;
            RimDir = rd.LengthSquared() > 1e-6f ? Vector3.Normalize(rd) : -Vector3.UnitZ;
            Rim = BucketMean(Bucket(RimDir)) * (0.6f * Gain);
            CctK = Cct(amb);
            Luminance = 0.2126f * amb.X + 0.7152f * amb.Y + 0.0722f * amb.Z;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
