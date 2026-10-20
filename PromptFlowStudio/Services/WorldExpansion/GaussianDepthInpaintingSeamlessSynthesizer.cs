using System.Numerics;
using PromptFlowStudio.Services.SemanticMatting;

namespace PromptFlowStudio.Services.WorldExpansion;

/// <summary>
/// [PromptFlow Studio v17.27.0] 🩹 4D 가우시안 심도 유도 인페인팅 &amp; 심리스 경계 합성기(<see cref="Render"/> · <see cref="Inpaint"/> 무할당).
///   ▸ G-버퍼 스플래팅(160×90 · 수평 90°): 풀 가우시안 → 깊이 테스트 원판 · 알베도/법선/깊이/커버리지 · 페이드 알파는 4×4 바이어 디더 알파-디졸브.
///   ▸ 구멍 마스크: v17.11 액터 캡슐 실루엣(제거 대상) ∪ 시선이 v17.26 파괴 크레이터 구를 먼저 지나는 픽셀 ∪ 지면 미커버(LoD 틈) — 하늘(지평선 위)은 제외.
///   ▸ 심도 유도 양방향 푸시-풀: 축소 시 2×2 유효 표본을 배경 깊이 쪽으로 가중(exp(−((z_max − z)/σ)²)) → 확대 시 이중선형 × 깊이 유사도 양방향 가중으로 알베도 · 법선 · 깊이를 채움.
///   ▸ 조명 정합: 채운 알베도 × v17.08 HDRI SH(9) 조도(법선) 로 재셰이딩 → 주변 SH 조명 텐서와 일치.
///   ▸ 4D: 4 프레임 링버퍼(색 · 깊이 · 카메라) — 채운 픽셀의 월드 위치를 직전 프레임으로 재투영 · 깊이 일치 시 시간 혼합(깜빡임 억제).
/// </summary>
public sealed class GaussianDepthInpaintingSeamlessSynthesizer
{
    public const int W = 160, H = 90, Px = W * H, RingLen = 4, MaxLevels = 8, MaxSplatRadius = 5;
    public const float FovDeg = 90f, Focal = W * 0.5f, Near = 0.3f, Far = 400f, DepthSigma = 0.15f, CoverThreshold = 0.5f;
    private static readonly float[] Bayer = { 0f, 8f, 2f, 10f, 12f, 4f, 14f, 6f, 3f, 11f, 1f, 9f, 15f, 7f, 13f, 5f };

    // 풀 해상도 G-버퍼
    private readonly float[] _ar = new float[Px], _ag = new float[Px], _ab = new float[Px], _nx = new float[Px], _ny = new float[Px], _nz = new float[Px], _z = new float[Px];
    private readonly float[] _cr = new float[Px], _cg = new float[Px], _cb = new float[Px];
    private readonly byte[] _hole = new byte[Px], _actor = new byte[Px], _sky = new byte[Px], _crater = new byte[Px];
    private readonly float[] _actorZ = new float[Px];
    // 피라미드(레벨 0 = 풀 해상도 작업 사본): 채널 7(알베도 3 · 법선 3 · 깊이) + 가중치
    private readonly float[][] _pyr = new float[MaxLevels][];
    private readonly float[][] _pw = new float[MaxLevels][];
    private readonly int[] _lw = new int[MaxLevels], _lh = new int[MaxLevels];
    private readonly int _levels;
    // 4D 링버퍼
    private readonly float[] _ringR = new float[RingLen * Px], _ringG = new float[RingLen * Px], _ringB = new float[RingLen * Px], _ringZ = new float[RingLen * Px];
    private readonly WorldCamera[] _ringCam = new WorldCamera[RingLen];
    private int _ringHead, _ringCount;
    private WorldCamera _cam;

    public GaussianDepthInpaintingSeamlessSynthesizer(HdriEnvironment env)
    {
        Env = env;
        int w = W, h = H, l = 0;
        while (l < MaxLevels && w >= 1 && h >= 1)
        {
            _lw[l] = w; _lh[l] = h;
            _pyr[l] = new float[7 * w * h];
            _pw[l] = new float[w * h];
            l++;
            if (w == 1 && h == 1) break;
            w = Math.Max(1, (w + 1) / 2); h = Math.Max(1, (h + 1) / 2);
        }
        _levels = l;
    }

    public HdriEnvironment Env { get; }
    public bool Enabled { get; set; } = true;
    public bool DepthGuided { get; set; } = true;
    public bool Relight { get; set; } = true;
    public bool Temporal { get; set; } = true;
    public bool RemoveActor { get; set; } = true;
    public int HolePixels { get; private set; }
    public int ActorPixels { get; private set; }
    public int CoveredPixels { get; private set; }
    public int SkyPixels { get; private set; }
    public int Splatted { get; private set; }
    public int TemporalHits { get; private set; }
    public float SeamError { get; private set; }
    public float Flicker { get; private set; }
    public double LastRenderUs { get; private set; }
    public double LastInpaintUs { get; private set; }
    public int PyramidLevels => _levels;
    public bool IsHole(int i) => _hole[i] != 0;
    public bool IsActor(int i) => _actor[i] != 0;
    public bool IsSky(int i) => _sky[i] != 0;
    public bool IsCrater(int i) => _crater[i] != 0;
    public int CraterPixels { get; private set; }
    public Vector3 Color(int i) => new(_cr[i], _cg[i], _cb[i]);
    public float Depth(int i) => _z[i];

    /// <summary>하늘 색(HDRI 조도의 위쪽 근사 · 높이 그라디언트).</summary>
    public Vector3 SkyColor(float dirY)
    {
        var t = Math.Clamp(dirY * 2f, 0f, 1f);
        return Vector3.Lerp(new Vector3(0.78f, 0.82f, 0.88f), new Vector3(0.38f, 0.55f, 0.85f), t);
    }

    public Vector3 Shade(Vector3 albedo, Vector3 n) => albedo * Vector3.Max(Env.Irradiance(n) * 0.6f, new Vector3(0.05f));

    /// <summary>풀 → G-버퍼 스플래팅 · 액터 캡슐(a, b, r) · 하늘/구멍 마스크 · 셰이딩.</summary>
    public void Render(GaussianWorldPool pool, DynamicOctreeLoDAntiPoppingGuard guard, in WorldCamera cam, Vector3 actorA, Vector3 actorB, float actorR, bool drawActor)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _cam = cam;
        Array.Fill(_z, float.PositiveInfinity);
        Array.Fill(_actorZ, float.PositiveInfinity);
        Array.Clear(_actor);
        var f = cam.Forward;
        var right = Vector3.Normalize(new Vector3(f.Z, 0f, -f.X));
        var up = Vector3.Cross(f, right);
        var splats = 0;
        for (var s = 0; s < pool.Slabs; s++)
        {
            var a = guard.SlabAlpha(s);
            if (a <= 0f) continue;
            for (var k = s * GaussianWorldPool.SlabSize; k < (s + 1) * GaussianWorldPool.SlabSize; k++)
            {
                var op = pool.Opacity[k] * a;
                if (op < 0.02f) continue;
                var d = new Vector3(pool.X[k], pool.Y[k], pool.Z[k]) - cam.Position;
                var zc = Vector3.Dot(d, f);
                if (zc < Near || zc > Far) continue;
                var sx = W * 0.5f + Focal * Vector3.Dot(d, right) / zc;
                var sy = H * 0.5f - Focal * Vector3.Dot(d, up) / zc;
                var rad = MathF.Min(MaxSplatRadius, MathF.Max(0.6f, pool.Scale[k] * Focal / zc));
                int x0 = Math.Max(0, (int)(sx - rad)), x1 = Math.Min(W - 1, (int)(sx + rad));
                int y0 = Math.Max(0, (int)(sy - rad)), y1 = Math.Min(H - 1, (int)(sy + rad));
                if (x0 > x1 || y0 > y1) continue;
                splats++;
                var r2 = rad * rad;
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var dx = x + 0.5f - sx; var dy = y + 0.5f - sy;
                        if (dx * dx + dy * dy > r2) continue;
                        if (op < 1f && Bayer[(y & 3) * 4 + (x & 3)] / 16f >= op) continue;   // 알파-디졸브(디더)
                        var i = y * W + x;
                        if (zc >= _z[i]) continue;
                        _z[i] = zc; _ar[i] = pool.R[k]; _ag[i] = pool.G[k]; _ab[i] = pool.B[k]; _nx[i] = pool.NX[k]; _ny[i] = pool.NY[k]; _nz[i] = pool.NZ[k];
                    }
            }
        }
        Splatted = splats;
        // v17.26 파괴 크레이터: 시선이 파괴 구를 먼저 통과하는 픽셀 = 소실 지오메트리(구멍)
        Array.Clear(_crater);
        for (var q = 0; q < guard.CraterCount; q++)
        {
            var cv = guard.Crater(q);
            var cc = new Vector3(cv.X, cv.Y, cv.Z);
            var c = cc - cam.Position;
            var zc = Vector3.Dot(c, f);
            if (zc < Near + cv.W) continue;
            var sx = W * 0.5f + Focal * Vector3.Dot(c, right) / zc;
            var sy = H * 0.5f - Focal * Vector3.Dot(c, up) / zc;
            var rad = cv.W * Focal / (zc - cv.W);
            int x0 = Math.Max(0, (int)(sx - rad)), x1 = Math.Min(W - 1, (int)(sx + rad));
            int y0 = Math.Max(0, (int)(sy - rad)), y1 = Math.Min(H - 1, (int)(sy + rad));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var dir = Vector3.Normalize(f * Focal + right * (x + 0.5f - W * 0.5f) + up * (H * 0.5f - y - 0.5f));
                    var b = Vector3.Dot(c, dir);
                    var disc = b * b - c.LengthSquared() + cv.W * cv.W;
                    if (disc <= 0f) continue;
                    var tz = (b - MathF.Sqrt(disc)) * Vector3.Dot(dir, f);
                    var i = y * W + x;
                    if (tz > Near && tz < _z[i]) _crater[i] = 1;
                }
        }
        // 액터 캡슐(구 9개 근사)
        for (var q = 0; q <= 8; q++)
        {
            var c = Vector3.Lerp(actorA, actorB, q / 8f) - cam.Position;
            var zc = Vector3.Dot(c, f);
            if (zc < Near) continue;
            var sx = W * 0.5f + Focal * Vector3.Dot(c, right) / zc;
            var sy = H * 0.5f - Focal * Vector3.Dot(c, up) / zc;
            var rad = actorR * Focal / zc;
            int x0 = Math.Max(0, (int)(sx - rad)), x1 = Math.Min(W - 1, (int)(sx + rad));
            int y0 = Math.Max(0, (int)(sy - rad)), y1 = Math.Min(H - 1, (int)(sy + rad));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5f - sx; var dy = y + 0.5f - sy;
                    if (dx * dx + dy * dy > rad * rad) continue;
                    var i = y * W + x;
                    if (zc < _actorZ[i]) _actorZ[i] = zc;
                }
        }
        int holes = 0, actors = 0, cov = 0, sky = 0, craters = 0;
        for (var y = 0; y < H; y++)
        {
            var dirY = Vector3.Dot(Vector3.Normalize(f * Focal + up * (H * 0.5f - y - 0.5f)), Vector3.UnitY);
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var act = drawActor && _actorZ[i] < _z[i];
                _actor[i] = act ? (byte)1 : (byte)0;
                var covered = !float.IsPositiveInfinity(_z[i]);
                var isSky = !covered && dirY > -0.02f;
                _sky[i] = isSky ? (byte)1 : (byte)0;
                var cr = _crater[i] != 0 && !isSky;
                if (cr) craters++;
                var hole = (act && RemoveActor) || (!covered && !isSky) || cr;
                _hole[i] = hole ? (byte)1 : (byte)0;
                if (act) actors++;
                if (hole) holes++;
                if (covered) cov++;
                if (isSky) sky++;
                Vector3 col;
                if (act && !RemoveActor) col = new Vector3(0.55f, 0.35f, 0.3f);
                else if (hole) col = Vector3.Zero;
                else if (isSky) col = SkyColor(dirY);
                else col = Shade(new Vector3(_ar[i], _ag[i], _ab[i]), new Vector3(_nx[i], _ny[i], _nz[i]));
                _cr[i] = col.X; _cg[i] = col.Y; _cb[i] = col.Z;
            }
        }
        HolePixels = holes; ActorPixels = actors; CoveredPixels = cov; SkyPixels = sky; CraterPixels = craters;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>구멍 채움: 심도 유도 양방향 푸시-풀 → SH 재셰이딩 → 링버퍼 재투영 시간 혼합 → 경계 이음새 측정.</summary>
    public void Inpaint()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        TemporalHits = 0;
        if (Enabled && HolePixels > 0)
        {
            // 레벨 0 = 유효(비구멍 · 비하늘 · 비액터 가림) 픽셀만 가중 1
            var p0 = _pyr[0]; var w0 = _pw[0];
            for (var i = 0; i < Px; i++)
            {
                var valid = _hole[i] == 0 && _sky[i] == 0 && !float.IsPositiveInfinity(_z[i]) && _actor[i] == 0;
                w0[i] = valid ? 1f : 0f;
                p0[i] = _ar[i]; p0[Px + i] = _ag[i]; p0[2 * Px + i] = _ab[i];
                p0[3 * Px + i] = _nx[i]; p0[4 * Px + i] = _ny[i]; p0[5 * Px + i] = _nz[i];
                p0[6 * Px + i] = valid ? _z[i] : 0f;
            }
            for (var l = 1; l < _levels; l++) Push(l);
            for (var l = _levels - 2; l >= 0; l--) Pull(l);
            for (var i = 0; i < Px; i++)
            {
                if (_hole[i] == 0) continue;
                var alb = new Vector3(p0[i], p0[Px + i], p0[2 * Px + i]);
                var n = new Vector3(p0[3 * Px + i], p0[4 * Px + i], p0[5 * Px + i]);
                var nl = n.Length();
                n = nl > 1e-6f ? n / nl : Vector3.UnitY;
                var col = Relight ? Shade(alb, n) : alb * 0.8f;
                _z[i] = p0[6 * Px + i];
                if (Temporal && _ringCount > 0 && Reproject(i, _z[i], out var prev))
                {
                    col = 0.5f * (col + prev);
                    TemporalHits++;
                }
                _cr[i] = col.X; _cg[i] = col.Y; _cb[i] = col.Z;
            }
        }
        Measure();
        Store();
        LastInpaintUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>비교 기준(심도·조명 무시): 셰이딩된 색을 균일 가중 푸시-풀로 직접 채움 — 같은 버퍼에 덮어씀(테스트/UI 비교용).</summary>
    public void InpaintNaive()
    {
        var p0 = _pyr[0]; var w0 = _pw[0];
        for (var i = 0; i < Px; i++)
        {
            var valid = _hole[i] == 0 && _sky[i] == 0 && _actor[i] == 0 && !float.IsPositiveInfinity(_z[i]);
            w0[i] = valid ? 1f : 0f;
            p0[i] = _cr[i]; p0[Px + i] = _cg[i]; p0[2 * Px + i] = _cb[i];
            p0[3 * Px + i] = 0f; p0[4 * Px + i] = 1f; p0[5 * Px + i] = 0f; p0[6 * Px + i] = 0f;
        }
        var dg = DepthGuided;
        DepthGuided = false;
        for (var l = 1; l < _levels; l++) Push(l);
        for (var l = _levels - 2; l >= 0; l--) Pull(l);
        DepthGuided = dg;
        for (var i = 0; i < Px; i++)
        {
            if (_hole[i] == 0) continue;
            _cr[i] = p0[i]; _cg[i] = p0[Px + i]; _cb[i] = p0[2 * Px + i];
        }
        Measure();
    }

    private void Push(int l)
    {
        int fw = _lw[l - 1], fh = _lh[l - 1], cw = _lw[l], ch = _lh[l];
        var fp = _pyr[l - 1]; var fwt = _pw[l - 1];
        var cp = _pyr[l]; var cwt = _pw[l];
        int fn = fw * fh, cn = cw * ch;
        Span<int> idx = stackalloc int[4];
        Span<float> wt = stackalloc float[4];
        for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var m = 0;
                var zmax = 0f;
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                    {
                        int fx = Math.Min(fw - 1, 2 * x + dx), fy = Math.Min(fh - 1, 2 * y + dy);
                        var fi = fy * fw + fx;
                        idx[m] = fi; wt[m] = fwt[fi];
                        if (fwt[fi] > 0f) zmax = MathF.Max(zmax, fp[6 * fn + fi]);
                        m++;
                    }
                var sw = 0f;
                for (var k = 0; k < 4; k++)
                {
                    if (wt[k] <= 0f) continue;
                    if (DepthGuided && zmax > 0f)
                    {
                        var rz = (zmax - fp[6 * fn + idx[k]]) / (DepthSigma * zmax);
                        wt[k] *= MathF.Exp(-rz * rz);   // 배경(먼 깊이) 우선
                    }
                    sw += wt[k];
                }
                var ci = y * cw + x;
                for (var c = 0; c < 7; c++)
                {
                    var v = 0f;
                    if (sw > 0f)
                    {
                        for (var k = 0; k < 4; k++) if (wt[k] > 0f) v += wt[k] * fp[c * fn + idx[k]];
                        v /= sw;
                    }
                    cp[c * cn + ci] = v;
                }
                cwt[ci] = MathF.Min(1f, sw);
            }
    }

    private void Pull(int l)
    {
        int fw = _lw[l], fh = _lh[l], cw = _lw[l + 1], ch = _lh[l + 1];
        var fp = _pyr[l]; var fwt = _pw[l];
        var cp = _pyr[l + 1]; var cwt = _pw[l + 1];
        int fn = fw * fh, cn = cw * ch;
        Span<int> ti = stackalloc int[4];
        Span<float> tw = stackalloc float[4];
        for (var y = 0; y < fh; y++)
            for (var x = 0; x < fw; x++)
            {
                var fi = y * fw + x;
                var wf = fwt[fi];
                if (wf >= 1f) continue;
                // 이중선형 4탭(조밀 → 성긴 좌표)
                var gx = (x + 0.5f) * 0.5f - 0.5f; var gy = (y + 0.5f) * 0.5f - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(gx), 0, cw - 1), y0 = Math.Clamp((int)MathF.Floor(gy), 0, ch - 1);
                int x1 = Math.Min(cw - 1, x0 + 1), y1 = Math.Min(ch - 1, y0 + 1);
                float ux = Math.Clamp(gx - x0, 0f, 1f), uy = Math.Clamp(gy - y0, 0f, 1f);
                ti[0] = y0 * cw + x0; ti[1] = y0 * cw + x1; ti[2] = y1 * cw + x0; ti[3] = y1 * cw + x1;
                tw[0] = (1 - ux) * (1 - uy); tw[1] = ux * (1 - uy); tw[2] = (1 - ux) * uy; tw[3] = ux * uy;
                // 양방향: 깊이 기준(탭 가중 평균) 대비 유사도
                float zr = 0f, zw = 0f;
                for (var k = 0; k < 4; k++) { var w = tw[k] * cwt[ti[k]]; zr += w * cp[6 * cn + ti[k]]; zw += w; }
                zr = zw > 0f ? zr / zw : 0f;
                var sw = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var w = tw[k] * MathF.Max(cwt[ti[k]], 1e-4f);
                    if (DepthGuided && zr > 0f)
                    {
                        var rz = (cp[6 * cn + ti[k]] - zr) / (DepthSigma * zr);
                        w *= MathF.Exp(-rz * rz);
                    }
                    tw[k] = w; sw += w;
                }
                if (sw <= 0f) continue;
                for (var c = 0; c < 7; c++)
                {
                    var v = 0f;
                    for (var k = 0; k < 4; k++) v += tw[k] * cp[c * cn + ti[k]];
                    v /= sw;
                    fp[c * fn + fi] = wf * fp[c * fn + fi] + (1f - wf) * v;
                }
                fwt[fi] = 1f;
            }
    }

    private bool Reproject(int i, float z, out Vector3 color)
    {
        color = default;
        var cam = _cam;
        var f = cam.Forward;
        var right = Vector3.Normalize(new Vector3(f.Z, 0f, -f.X));
        var up = Vector3.Cross(f, right);
        int x = i % W, y = i / W;
        var world = cam.Position + f * z + right * ((x + 0.5f - W * 0.5f) * z / Focal) + up * ((H * 0.5f - y - 0.5f) * z / Focal);
        var slot = (_ringHead - 1 + RingLen) % RingLen;
        ref readonly var pc = ref _ringCam[slot];
        var pf = pc.Forward;
        var pr = Vector3.Normalize(new Vector3(pf.Z, 0f, -pf.X));
        var pu = Vector3.Cross(pf, pr);
        var d = world - pc.Position;
        var zc = Vector3.Dot(d, pf);
        if (zc < Near) return false;
        var px = (int)(W * 0.5f + Focal * Vector3.Dot(d, pr) / zc);
        var py = (int)(H * 0.5f - Focal * Vector3.Dot(d, pu) / zc);
        if ((uint)px >= W || (uint)py >= H) return false;
        var pi = slot * Px + py * W + px;
        var pz = _ringZ[pi];
        if (!(MathF.Abs(pz - zc) < 0.05f * zc)) return false;
        color = new Vector3(_ringR[pi], _ringG[pi], _ringB[pi]);
        return true;
    }

    private void Store()
    {
        var o = _ringHead * Px;
        _cr.AsSpan().CopyTo(_ringR.AsSpan(o, Px));
        _cg.AsSpan().CopyTo(_ringG.AsSpan(o, Px));
        _cb.AsSpan().CopyTo(_ringB.AsSpan(o, Px));
        for (var i = 0; i < Px; i++) _ringZ[o + i] = _sky[i] != 0 || (_actor[i] != 0 && !RemoveActor) ? float.PositiveInfinity : _z[i];
        _ringCam[_ringHead] = _cam;
        _ringHead = (_ringHead + 1) % RingLen;
        if (_ringCount < RingLen) _ringCount++;
    }

    /// <summary>이음새: 구멍 경계 픽셀과 인접 유효 픽셀 색 차 평균 · 깜빡임: 채운 픽셀과 직전 링 프레임 재투영 색의 차(카메라 이동 보상).</summary>
    private void Measure()
    {
        double seam = 0;
        var ns = 0;
        for (var y = 1; y < H - 1; y++)
            for (var x = 1; x < W - 1; x++)
            {
                var i = y * W + x;
                if (_hole[i] == 0) continue;
                for (var k = 0; k < 4; k++)
                {
                    var j = k == 0 ? i - 1 : k == 1 ? i + 1 : k == 2 ? i - W : i + W;
                    if (_hole[j] != 0 || _sky[j] != 0 || (_actor[j] != 0 && RemoveActor)) continue;
                    seam += MathF.Abs(_cr[i] - _cr[j]) + MathF.Abs(_cg[i] - _cg[j]) + MathF.Abs(_cb[i] - _cb[j]);
                    ns++;
                }
            }
        SeamError = ns > 0 ? (float)(seam / ns / 3) : 0f;
        if (_ringCount == 0) { Flicker = 0f; return; }
        double fl = 0;
        var nf = 0;
        for (var i = 0; i < Px; i++)
        {
            if (_hole[i] == 0 || !Reproject(i, _z[i], out var prev)) continue;
            fl += MathF.Abs(_cr[i] - prev.X) + MathF.Abs(_cg[i] - prev.Y) + MathF.Abs(_cb[i] - prev.Z);
            nf++;
        }
        Flicker = nf > 0 ? (float)(fl / nf / 3) : 0f;
    }

    /// <summary>구멍 픽셀 색 RMSE(기준 색 버퍼 대비 · 테스트/진단).</summary>
    public float HoleRmse(ReadOnlySpan<float> refR, ReadOnlySpan<float> refG, ReadOnlySpan<float> refB, ReadOnlySpan<byte> mask)
    {
        double s = 0;
        var n = 0;
        for (var i = 0; i < Px; i++)
        {
            if (mask[i] == 0) continue;
            float dr = _cr[i] - refR[i], dg = _cg[i] - refG[i], db = _cb[i] - refB[i];
            s += dr * dr + dg * dg + db * db;
            n++;
        }
        return n > 0 ? (float)Math.Sqrt(s / n / 3) : 0f;
    }

    public void CopyColor(Span<float> r, Span<float> g, Span<float> b) { _cr.AsSpan().CopyTo(r); _cg.AsSpan().CopyTo(g); _cb.AsSpan().CopyTo(b); }
    public void CopyHoleMask(Span<byte> m) => _hole.AsSpan().CopyTo(m);
}
