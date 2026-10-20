using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService
using PromptFlowStudio.Services.Spatial;       // 단안 깊이 · BMP 코덱

namespace PromptFlowStudio.Services.Vfx;

public enum VfxLightKind : byte { Point, Spot, Directional }

/// <summary>
/// 가상 광원 — 화면 좌표계(X 정규화 -1~1 좌→우 · Y -1~1 하→상 · Z 깊이 0~1(+ = 화면 안쪽, 음수 = 카메라 앞)).
/// 방향(Dx,Dy,Dz)은 스포트/디렉셔널의 광선 진행 방향. 각도 도 · 범위 = 감쇠 반경(정규화 단위).
/// </summary>
public readonly record struct VfxLight(VfxLightKind Kind, float X, float Y, float Z, float Dx, float Dy, float Dz,
    float R, float G, float B, float Intensity, float Range, float ConeDeg, float SoftDeg, bool Shadows)
{
    public static VfxLight Point(float x, float y, float z, float r, float g, float b, float intensity, float range = 1.5f, bool shadows = true)
        => new(VfxLightKind.Point, x, y, z, 0, 0, 1, r, g, b, intensity, range, 0, 0, shadows);

    public static VfxLight Spot(float x, float y, float z, float dx, float dy, float dz, float r, float g, float b, float intensity, float cone = 30, float soft = 10, bool shadows = true)
        => new(VfxLightKind.Spot, x, y, z, dx, dy, dz, r, g, b, intensity, 3f, cone, soft, shadows);

    public static VfxLight Directional(float dx, float dy, float dz, float r, float g, float b, float intensity, bool shadows = true)
        => new(VfxLightKind.Directional, 0, 0, 0, dx, dy, dz, r, g, b, intensity, 0, 0, 0, shadows);
}

/// <summary>재질/합성 설정 — 원본 보존(1=원본 조명 유지) · 앰비언트 · 스페큘러 강도/광택 · 법선 강도 · 그림자 농도 · 깊이 스케일.</summary>
public readonly record struct VfxShading(float Preserve, float Ambient, float Specular, float Shininess, float NormalStrength, float ShadowDensity, float DepthScale)
{
    public static readonly VfxShading Default = new(0.35f, 0.25f, 0.35f, 32f, 1.0f, 0.65f, 0.35f);

    public VfxShading Clamp() => new(Math.Clamp(Preserve, 0f, 1f), Math.Clamp(Ambient, 0f, 1f), Math.Clamp(Specular, 0f, 2f), Math.Clamp(Shininess, 1f, 256f),
        Math.Clamp(NormalStrength, 0.1f, 4f), Math.Clamp(ShadowDensity, 0f, 1f), Math.Clamp(DepthScale, 0.05f, 2f));
}

public sealed record RelightPreview(int Width, int Height, string DepthDataUrl, string NormalDataUrl, string ResultDataUrl, double Milliseconds);

public sealed record VfxVideoResult(bool Ok, string? OutputPath, int Frames, string Engine, string Message);

/// <summary>
/// [PromptFlow Studio v16.34.0] AI 신경망 VFX 조명 재구성 엔진(순수 BCL CPU 셰이더).
///   ▸ 깊이: 단안 깊이(<see cref="Spatial3dGaussianSplattingEngine"/> 결선점 공유 — 외부 뎁스넷 우선 · BCL 폴백).
///   ▸ 법선: 깊이 중앙차분(소벨 3×3) + 휘도 디테일 범프 → 정규화 · 표면 위치 = (x, y, 깊이×스케일).
///   ▸ 셰이딩: 알베도(원본 ÷ 추정 기존 조명) × (앰비언트 + Σ 램버트 디퓨즈) + 블린-퐁 스페큘러
///     · 포인트(역제곱 + 반경 창) · 스포트(콘 smoothstep 소프트 엣지) · 디렉셔널 · 화면공간 깊이 레이마치 그림자(16스텝).
///   ▸ 원본 보존 블렌드 · ACES 근사 톤매핑 · <see cref="ProcessFrame"/> 인스턴스 버퍼 재사용(프레임당 할당 0).
///   ▸ 영상: ffmpeg rawvideo 파이프(원본 오디오 유지) · 프리뷰 BMP(깊이/법선/결과).
/// </summary>
public sealed class AiNeuralVfxRelightingEngine
{
    public const int MaxLights = 8;
    public const int ShadowSteps = 16;
    public const int PreviewWidth = 480;
    public const int MaxFrames = 36_000;

    private readonly Func<string?> _ffmpeg;
    private readonly Spatial3dGaussianSplattingEngine? _depth;
    private readonly object _gate = new();
    private readonly List<VfxLight> _lights = new() { VfxLight.Point(0.6f, 0.5f, -0.3f, 1f, 0.85f, 0.65f, 1.4f), VfxLight.Directional(-0.4f, -0.3f, 1f, 0.45f, 0.6f, 1f, 0.35f, false) };
    private float[]? _d, _s, _nx, _ny, _nz;
    private byte[]? _pv;
    private int _pvW, _pvH, _busy;
    private long _frames;

    public AiNeuralVfxRelightingEngine(IVideoMergeService merge, Spatial3dGaussianSplattingEngine depth) : this(() => merge.ResolvedFfmpegPath, depth) { }

    public AiNeuralVfxRelightingEngine(Func<string?> ffmpeg, Spatial3dGaussianSplattingEngine? depth)
    {
        _ffmpeg = ffmpeg;
        _depth = depth;
    }

    public event Action? OnChange;

    public VfxShading Shading { get; set; } = VfxShading.Default;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public long FramesRelit => Interlocked.Read(ref _frames);
    public double Progress { get; private set; }
    public RelightPreview? LastPreview { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<VfxLight> Lights { get { lock (_gate) return _lights.ToArray(); } }

    public bool AddLight(VfxLight l)
    {
        lock (_gate) { if (_lights.Count >= MaxLights) return false; _lights.Add(l); }
        VfxProcess.Raise(OnChange, "Relight.OnChange");
        return true;
    }

    public void SetLight(int index, VfxLight l) { lock (_gate) { if ((uint)index < (uint)_lights.Count) _lights[index] = l; } VfxProcess.Raise(OnChange, "Relight.OnChange"); }
    public void RemoveLight(int index) { lock (_gate) { if ((uint)index < (uint)_lights.Count) _lights.RemoveAt(index); } VfxProcess.Raise(OnChange, "Relight.OnChange"); }
    public void ReplaceLights(IEnumerable<VfxLight> lights) { lock (_gate) { _lights.Clear(); foreach (var l in lights) if (_lights.Count < MaxLights) _lights.Add(l); } VfxProcess.Raise(OnChange, "Relight.OnChange"); }

    // ══════════════════════ 법선 ══════════════════════

    /// <summary>깊이(+휘도 범프) → 법선(카메라 향함 = -Z · 화면 안쪽 = +Z). 무할당.</summary>
    public static void ComputeNormals(ReadOnlySpan<float> depth, ReadOnlySpan<byte> rgb, int w, int h, float strength, Span<float> nx, Span<float> ny, Span<float> nz)
    {
        var n = w * h;
        if (w < 3 || h < 3 || depth.Length < n || nx.Length < n || ny.Length < n || nz.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        var bump = rgb.Length >= n * 3;
        var k = strength * w * 0.5f;
        for (var y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1) * w, y0 = y * w, yp = Math.Min(h - 1, y + 1) * w;
            for (var x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                var gx = (depth[ym + xp] + 2 * depth[y0 + xp] + depth[yp + xp]) - (depth[ym + xm] + 2 * depth[y0 + xm] + depth[yp + xm]);
                var gy = (depth[yp + xm] + 2 * depth[yp + x] + depth[yp + xp]) - (depth[ym + xm] + 2 * depth[ym + x] + depth[ym + xp]);
                gx *= 0.125f; gy *= 0.125f;
                if (bump)
                {
                    gx += (Luma(rgb, y0 + xp) - Luma(rgb, y0 + xm)) * 0.02f;
                    gy += (Luma(rgb, yp + x) - Luma(rgb, ym + x)) * 0.02f;
                }
                // 표면 z = 깊이(화면 안쪽 +) → 법선 = (∂z/∂x, -∂z/∂y(화면 y 하향 → 월드 상향), -1)
                float ax = gx * k, ay = -gy * k, az = -1f;
                var inv = 1f / MathF.Sqrt(ax * ax + ay * ay + az * az);
                var i0 = y0 + x;
                nx[i0] = ax * inv; ny[i0] = ay * inv; nz[i0] = az * inv;
            }
        }
    }

    private static float Luma(ReadOnlySpan<byte> rgb, int i) => (rgb[i * 3] * 0.299f + rgb[i * 3 + 1] * 0.587f + rgb[i * 3 + 2] * 0.114f) * (1f / 255f);

    // ══════════════════════ 셰이딩 ══════════════════════

    /// <summary>재조명 합성(무할당). lights ≤ 8.</summary>
    public static void Shade(ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, ReadOnlySpan<float> nx, ReadOnlySpan<float> ny, ReadOnlySpan<float> nz, int w, int h,
        ReadOnlySpan<VfxLight> lights, in VfxShading shading, Span<byte> outRgb)
    {
        var n = w * h;
        if (rgb.Length < n * 3 || depth.Length < n || outRgb.Length < n * 3) throw new ArgumentException("버퍼 크기 불일치");
        var s = shading.Clamp();
        var aspect = (float)h / w;
        var lc = Math.Min(lights.Length, MaxLights);
        // 기존 조명 추정: 원본 평균 휘도 — 알베도 = 원본 / (0.5 + 평균)
        double sumL = 0;
        for (var i = 0; i < n; i += 7) sumL += rgb[i * 3] * 0.299 + rgb[i * 3 + 1] * 0.587 + rgb[i * 3 + 2] * 0.114;
        var meanL = (float)(sumL / Math.Max(1, (n + 6) / 7) / 255.0);
        var albedoK = 1f / MathF.Max(0.15f, 0.5f + meanL * 0.5f);
        for (var y = 0; y < h; y++)
        {
            var py = (1f - 2f * y / (h - 1)) * aspect;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var px = 2f * x / (w - 1) - 1f;
                var pz = depth[i] * s.DepthScale;
                float Nx = nx[i], Ny = ny[i], Nz = nz[i];
                float ar = rgb[i * 3] * (1f / 255f) * albedoK, ag = rgb[i * 3 + 1] * (1f / 255f) * albedoK, ab = rgb[i * 3 + 2] * (1f / 255f) * albedoK;
                float dr = s.Ambient, dg = s.Ambient, db = s.Ambient, sr = 0, sg = 0, sb = 0;
                for (var k = 0; k < lc; k++)
                {
                    ref readonly var L = ref lights[k];
                    float lx, ly, lz, att;
                    if (L.Kind == VfxLightKind.Directional)
                    {
                        lx = -L.Dx; ly = -L.Dy; lz = -L.Dz;
                        var ll = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
                        if (ll < 1e-6f) continue;
                        lx /= ll; ly /= ll; lz /= ll; att = 1f;
                    }
                    else
                    {
                        lx = L.X - px; ly = L.Y * aspect - py; lz = L.Z * s.DepthScale - pz;
                        var d2 = lx * lx + ly * ly + lz * lz;
                        var d = MathF.Sqrt(d2);
                        if (d < 1e-5f) continue;
                        lx /= d; ly /= d; lz /= d;
                        var range = MathF.Max(0.05f, L.Range);
                        var win = Math.Clamp(1f - d / range, 0f, 1f);
                        att = win * win / (1f + 4f * d2);
                        if (L.Kind == VfxLightKind.Spot)
                        {
                            var sl = MathF.Sqrt(L.Dx * L.Dx + L.Dy * L.Dy + L.Dz * L.Dz);
                            if (sl < 1e-6f) continue;
                            var cosA = -(lx * L.Dx + ly * L.Dy + lz * L.Dz) / sl;
                            var outer = MathF.Cos((L.ConeDeg + L.SoftDeg) * (MathF.PI / 180f));
                            var inner = MathF.Cos(L.ConeDeg * (MathF.PI / 180f));
                            att *= Sound.SoundDsp.Smoothstep(outer, inner, cosA);
                        }
                    }
                    if (att <= 1e-5f) continue;
                    var ndl = Nx * lx + Ny * ly + Nz * lz;
                    if (ndl <= 0) continue;
                    if (L.Shadows && s.ShadowDensity > 0) att *= 1f - s.ShadowDensity * ShadowFactor(depth, w, h, x, y, lx, ly, lz, s.DepthScale, aspect);
                    var e = L.Intensity * att;
                    dr += L.R * e * ndl; dg += L.G * e * ndl; db += L.B * e * ndl;
                    // 블린-퐁(시선 = -Z)
                    float hx = lx, hy = ly, hz = lz - 1f;
                    var hl = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
                    if (hl > 1e-6f)
                    {
                        var ndh = MathF.Max(0f, (Nx * hx + Ny * hy + Nz * hz) / hl);
                        var sp = MathF.Pow(ndh, s.Shininess) * s.Specular * e;
                        sr += L.R * sp; sg += L.G * sp; sb += L.B * sp;
                    }
                }
                float r = ar * dr + sr, g = ag * dg + sg, b = ab * db + sb;
                var o = i * 3;
                r = Aces(r) * (1 - s.Preserve) + rgb[o] * (1f / 255f) * s.Preserve;
                g = Aces(g) * (1 - s.Preserve) + rgb[o + 1] * (1f / 255f) * s.Preserve;
                b = Aces(b) * (1 - s.Preserve) + rgb[o + 2] * (1f / 255f) * s.Preserve;
                outRgb[o] = (byte)Math.Clamp(r * 255f + 0.5f, 0f, 255f);
                outRgb[o + 1] = (byte)Math.Clamp(g * 255f + 0.5f, 0f, 255f);
                outRgb[o + 2] = (byte)Math.Clamp(b * 255f + 0.5f, 0f, 255f);
            }
        }
    }

    /// <summary>화면공간 레이마치 — 광원 방향으로 16스텝 진행하며 깊이 맵이 레이보다 앞(작음)이면 차폐(0~1).</summary>
    public static float ShadowFactor(ReadOnlySpan<float> depth, int w, int h, int x, int y, float lx, float ly, float lz, float depthScale, float aspect)
    {
        var lat = MathF.Sqrt(lx * lx + ly * ly);
        if (lat < 1e-4f) return 0f;
        var stepPx = MathF.Max(1f, w / 96f);
        float sx = lx / lat * stepPx, sy = -ly / lat * stepPx;           // 화면 y 하향
        var dzRawPerPx = lz / lat * (2f / w) / MathF.Max(1e-3f, depthScale); // 측면 1px 진행당 원시 깊이 변화
        var z0 = depth[y * w + x];
        var occl = 0f;
        float cx = x, cy = y;
        for (var k = 1; k <= ShadowSteps; k++)
        {
            cx += sx; cy += sy;
            var ix = (int)cx; var iy = (int)cy;
            if ((uint)ix >= (uint)w || (uint)iy >= (uint)h) break;
            var rayZ = z0 + dzRawPerPx * k * stepPx;
            var dz = rayZ - depth[iy * w + ix];
            if (dz > 0.02f) occl = MathF.Max(occl, Math.Clamp((dz - 0.02f) * 12f, 0f, 1f) * (1f - (float)k / (ShadowSteps + 1) * 0.5f));
        }
        return occl;
    }

    private static float Aces(float x)
    {
        x = MathF.Max(0f, x);
        return Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);
    }

    /// <summary>프레임 1장 — 깊이 추정 → 법선 → 셰이딩(인스턴스 버퍼 재사용 · 첫 호출 외 무할당).</summary>
    public void ProcessFrame(byte[] rgb, int w, int h, ReadOnlySpan<VfxLight> lights, in VfxShading shading, Span<byte> outRgb)
    {
        var n = w * h;
        lock (_gate)
        {
            if (_d is null || _d.Length < n) { _d = new float[n]; _s = new float[n]; _nx = new float[n]; _ny = new float[n]; _nz = new float[n]; }
            var d = _d.AsSpan(0, n);
            if (_depth is not null) _depth.EstimateDepthWithHook(rgb, w, h, d, _s.AsSpan(0, n));
            else Spatial3dGaussianSplattingEngine.EstimateDepth(rgb, w, h, d, _s.AsSpan(0, n));
            ComputeNormals(d, rgb, w, h, shading.NormalStrength, _nx.AsSpan(0, n), _ny!.AsSpan(0, n), _nz!.AsSpan(0, n));
            Shade(rgb, d, _nx.AsSpan(0, n), _ny.AsSpan(0, n), _nz.AsSpan(0, n), w, h, lights, shading, outRgb);
        }
        Interlocked.Increment(ref _frames);
    }

    private VfxLight[] LightSnapshot() { lock (_gate) return _lights.ToArray(); }

    // ══════════════════════ 프리뷰 ══════════════════════

    public RelightPreview SetPreview(byte[] rgb, int w, int h)
    {
        lock (_gate) { _pv = rgb; _pvW = w; _pvH = h; }
        return RebuildPreview()!;
    }

    public RelightPreview? RebuildPreview()
    {
        byte[]? rgb; int w, h;
        lock (_gate) { rgb = _pv; w = _pvW; h = _pvH; }
        if (rgb is null) return null;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var outRgb = new byte[w * h * 3];
        ProcessFrame(rgb, w, h, LightSnapshot(), Shading, outRgb);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var depthRgb = new byte[w * h * 3]; var normalRgb = new byte[w * h * 3];
        lock (_gate)
        {
            SpatialImageCodec.DepthToRgb(_d.AsSpan(0, w * h), depthRgb);
            for (var i = 0; i < w * h; i++)
            {
                normalRgb[i * 3] = (byte)((_nx![i] * 0.5f + 0.5f) * 255f);
                normalRgb[i * 3 + 1] = (byte)((_ny![i] * 0.5f + 0.5f) * 255f);
                normalRgb[i * 3 + 2] = (byte)((-_nz![i] * 0.5f + 0.5f) * 255f);
            }
        }
        var p = new RelightPreview(w, h, SpatialImageCodec.BmpDataUrl(depthRgb, w, h), SpatialImageCodec.BmpDataUrl(normalRgb, w, h), SpatialImageCodec.BmpDataUrl(outRgb, w, h), ms);
        LastPreview = p;
        LastAction = "💡 리라이팅 프리뷰 · 광원 " + Lights.Count.ToString(CultureInfo.InvariantCulture) + " · " + ms.ToString("0", CultureInfo.InvariantCulture) + "ms";
        VfxProcess.Raise(OnChange, "Relight.OnChange");
        return p;
    }

    public async Task<RelightPreview?> PreviewAsync(string path, double seconds, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(path)) { LastAction = "⚠ 프리뷰 불가(ffmpeg/파일 확인)"; VfxProcess.Raise(OnChange, "Relight.OnChange"); return null; }
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, path, ct).ConfigureAwait(false);
            if (probe is null) return null;
            var w = PreviewWidth;
            var h = Math.Max(16, (int)Math.Round(PreviewWidth * (double)probe.Value.H / probe.Value.W) & ~1);
            var t = probe.Value.Duration > 0 ? Math.Clamp(seconds, 0, Math.Max(0, probe.Value.Duration - 0.05)) : 0;
            var frame = await GrabFrameAsync(ffmpeg, path, t, w, h, ct).ConfigureAwait(false);
            return frame is null ? null : SetPreview(frame, w, h);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Relight.Preview", ex); LastAction = "⚠ 프리뷰 실패: " + ex.Message; VfxProcess.Raise(OnChange, "Relight.OnChange"); return null; }
    }

    internal static async Task<byte[]?> GrabFrameAsync(string ffmpeg, string path, double t, int w, int h, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        using var p = VfxProcess.Start(ffmpeg, new[] { "-hide_banner", "-v", "error", "-ss", t.ToString("0.###", ci), "-i", path, "-frames:v", "1", "-vf", "scale=" + w.ToString(ci) + ":" + h.ToString(ci),
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" }, false, ct);
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(30));
        var buf = new byte[w * h * 3];
        try { return await Spatial3dGaussianSplattingEngine.ReadExactAsync(p.StandardOutput.BaseStream, buf, buf.Length, d.Token).ConfigureAwait(false) ? buf : null; }
        finally { try { if (!p.HasExited) p.KillCompat(true); } catch { } }
    }

    // ══════════════════════ 영상 ══════════════════════

    public async Task<VfxVideoResult> RelightVideoAsync(string input, string output, int width = 1280, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(input)) return Done(new(false, null, 0, "BCL", "ffmpeg 또는 입력 파일이 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, 0, "BCL", "다른 리라이팅이 진행 중입니다.");
        Progress = 0;
        LastAction = "💡 리라이팅 중 · " + Path.GetFileName(input);
        VfxProcess.Raise(OnChange, "Relight.OnChange");
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, input, ct).ConfigureAwait(false);
            if (probe is null) return Done(new(false, null, 0, "BCL", "해상도를 읽지 못했습니다."));
            var w = Math.Clamp(width, 160, 3840) & ~1;
            var h = Math.Max(16, (int)Math.Round(w * (double)probe.Value.H / probe.Value.W) & ~1);
            var fps = Math.Clamp(await ProbeFpsAsync(ffmpeg, input, ct).ConfigureAwait(false), 1, 120);
            var lights = LightSnapshot(); var shading = Shading;
            var expected = Math.Max(1, probe.Value.Duration * fps);
            var frames = await VfxProcess.PipeAsync(ffmpeg, input, w, h, w, h, fps, null, (src, dst) => ProcessFrame(src, w, h, lights, shading, dst), output, MaxFrames,
                f => { Progress = Math.Min(0.99, f / expected); VfxProcess.Raise(OnChange, "Relight.OnChange"); }, ct).ConfigureAwait(false);
            if (frames == 0) return Done(new(false, null, 0, "BCL", "리라이팅 인코드 실패"));
            LastAction = "💡 리라이팅 완료 · " + frames.ToString(CultureInfo.InvariantCulture) + "프레임";
            return Done(new(true, output, frames, "BCL", "리라이팅 완료"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Done(new(false, null, 0, "BCL", "취소됨")); }
        catch (Exception ex) { Program.Log("Relight.Video", ex); return Done(new(false, null, 0, "BCL", "리라이팅 실패: " + ex.Message)); }
        finally { Progress = 1; Volatile.Write(ref _busy, 0); VfxProcess.Raise(OnChange, "Relight.OnChange"); }
    }

    /// <summary>ffmpeg -i stderr 의 "NN fps" 파싱(실패 30).</summary>
    public static double ParseFps(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return 30;
        var i = stderr.IndexOf(" fps", StringComparison.Ordinal);
        while (i > 0)
        {
            var j = i - 1;
            while (j >= 0 && (char.IsDigit(stderr[j]) || stderr[j] == '.')) j--;
            if (j < i - 1 && double.TryParse(stderr.AsSpan(j + 1, i - j - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0) return f;
            i = stderr.IndexOf(" fps", i + 4, StringComparison.Ordinal);
        }
        return 30;
    }

    internal static async Task<double> ProbeFpsAsync(string ffmpeg, string path, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!p.StartCompat()) return 30;
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(15));
        try { return ParseFps(await p.StandardError.ReadToEndAsync(d.Token).ConfigureAwait(false)); }
        finally { try { if (!p.HasExited) p.KillCompat(true); } catch { } }
    }

    private VfxVideoResult Done(VfxVideoResult r)
    {
        if (!r.Ok) LastAction = "⚠ 리라이팅 " + r.Message;
        return r;
    }
}
