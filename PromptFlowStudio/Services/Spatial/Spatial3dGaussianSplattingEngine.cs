using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService

namespace PromptFlowStudio.Services.Spatial;

/// <summary>3D 가우시안 1개(등방성 σ · 월드 좌표 미터 · x 우 / y 상 / z 전방).</summary>
public struct GaussianSplat
{
    public float X, Y, Z, Scale, Opacity;
    public byte R, G, B;
}

/// <summary>카메라 자세(월드←카메라: 피치(X축) → 요(Y축) 회전 후 평행 이동 · 라디안/미터).</summary>
public readonly record struct SpatialCameraPose(float Tx, float Ty, float Tz, float Yaw, float Pitch)
{
    /// <summary>오빗 경로 — t∈[0,1] 동안 피벗(전방 pivot m) 주위를 ±arc/2 도 회전하며 피벗을 응시.</summary>
    public static SpatialCameraPose Orbit(double t, float pivotMeters, float arcDeg)
    {
        var a = (float)((Math.Clamp(t, 0, 1) - 0.5) * arcDeg * Math.PI / 180.0);
        return new(pivotMeters * MathF.Sin(a), 0, pivotMeters * (1 - MathF.Cos(a)), -a, 0);
    }
}

public sealed record SplatReconstructionResult(bool Ok, int Frames, int Keyframes, int Splats, float PanYawDeg, float PanPitchDeg, string Message);

/// <summary>
/// [PromptFlow Studio v16.31.0] 3D 가우시안 스플래팅 씬 복원 엔진(순수 BCL · 외부 3D DLL 0).
///   ▸ 2D 프레임 → 단안 깊이 추정(지면 원근 prior + 대기 원근(헤이즈) + 국소 대비 · 분리형 박스 블러) — 실 뎁스넷은 <see cref="ExternalDepthEstimator"/> 결선(실패 시 BCL 폴백).
///   ▸ 연속 프레임 전역 SAD 정합 → 카메라 패닝(요/피치) 누적 · 시점 변화 3° 이상 키프레임만 언프로젝션(중복 스플랫 억제).
///   ▸ 깊이 불연속 경계는 반투명(고무막 아티팩트 억제) · 스플랫 상한 <see cref="MaxSplats"/> 고정 버퍼(재할당 0).
///   ▸ 렌더: 2패스 표면 스플래팅(최근접 z → 깊이 허용 내 가우시안 가중 누적) · 스테레오(IPD) · 오빗 신규 시점.
///   ▸ 내보내기: 표준 3DGS PLY(binary_little_endian · f_dc/opacity logit/log scale/quat) 원자 교체.
///   ▸ 핫패스(깊이·정합·통합·렌더)는 호출자 Span/고정 버퍼만 사용 — LINQ/박싱/보간 0.
/// </summary>
public sealed class Spatial3dGaussianSplattingEngine
{
    public const int MaxSplats = 262_144;
    public const int ReconWidth = 256;
    public const int ReconFps = 2;
    public const int PanRadius = 8;
    public const float NearMeters = 0.6f;
    public const float FarMeters = 12f;
    public const float DefaultHfovDeg = 63.4f;
    public const float KeyframeDeg = 3f;
    public const float DepthTolerance = 0.06f;
    public const int MaxFootprintPx = 8;
    public static readonly TimeSpan ReconTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<string?> _ffmpeg;
    private readonly object _gate = new();
    private GaussianSplat[]? _splats;
    private float[]? _zbuf, _acc;
    private int _count, _busy, _frames, _recons;
    private float _pivot = 3f, _hfov = DefaultHfovDeg;

    public Spatial3dGaussianSplattingEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    public Spatial3dGaussianSplattingEngine(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public event Action? OnChange;

    /// <summary>외부 뎁스넷 결선점(rgb24, w, h → 0=근 · 1=원 정규화 깊이, null/예외 시 BCL 폴백).</summary>
    public Func<byte[], int, int, float[]?>? ExternalDepthEstimator { get; set; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public int SplatCount { get { lock (_gate) return _count; } }
    public int IntegratedFrames => Volatile.Read(ref _frames);
    public int Reconstructions => Volatile.Read(ref _recons);
    public float PivotMeters { get { lock (_gate) return _pivot; } }
    public float HfovDeg { get { lock (_gate) return _hfov; } }
    public SplatReconstructionResult? LastResult { get; private set; }
    public string? LastAction { get; private set; }

    public void Reset()
    {
        lock (_gate) { _count = 0; _pivot = 3f; }
        Volatile.Write(ref _frames, 0);
        LastAction = "🧊 씬 초기화";
        Raise();
    }

    // ══════════════════════ 단안 깊이 추정 ══════════════════════

    public static float FocalPx(int width, float hfovDeg) => width * 0.5f / MathF.Tan(Math.Clamp(hfovDeg, 10f, 150f) * MathF.PI / 360f);

    /// <summary>정규화 깊이(0=근 · 1=원) → 미터(역깊이 선형 보간 · 근거리 해상도 우선).</summary>
    public static float DepthToMeters(float d) => 1f / (1f / NearMeters + Math.Clamp(d, 0f, 1f) * (1f / FarMeters - 1f / NearMeters));

    public static void ToLuma(ReadOnlySpan<byte> rgb, Span<byte> luma)
    {
        for (int i = 0, j = 0; i < luma.Length; i++, j += 3)
            luma[i] = (byte)((77 * rgb[j] + 150 * rgb[j + 1] + 29 * rgb[j + 2]) >> 8);
    }

    /// <summary>BCL 단안 깊이 휴리스틱(무할당 · scratch ≥ w·h). 지면 prior 0.55 + 헤이즈 0.25 + 저대비 0.20 → 박스 블러.</summary>
    public static void EstimateDepth(ReadOnlySpan<byte> rgb, int w, int h, Span<float> depth, Span<float> scratch)
    {
        var n = w * h;
        if (w < 2 || h < 2 || rgb.Length < n * 3 || depth.Length < n || scratch.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        for (int i = 0, j = 0; i < n; i++, j += 3)
            scratch[i] = (0.299f * rgb[j] + 0.587f * rgb[j + 1] + 0.114f * rgb[j + 2]) * (1f / 255f);
        var invH = 1f / (h - 1);
        for (var y = 0; y < h; y++)
        {
            var prior = (h - 1 - y) * invH;   // 상단 = 원경
            var row = y * w;
            var up = (y > 0 ? y - 1 : y) * w;
            var dn = (y < h - 1 ? y + 1 : y) * w;
            for (var x = 0; x < w; x++)
            {
                var i = row + x;
                var j = i * 3;
                float r = rgb[j] * (1f / 255f), g = rgb[j + 1] * (1f / 255f), b = rgb[j + 2] * (1f / 255f);
                var max = MathF.Max(r, MathF.Max(g, b));
                var min = MathF.Min(r, MathF.Min(g, b));
                var sat = max > 1e-4f ? (max - min) / max : 0f;
                var haze = Math.Clamp(0.5f + (b - r) * 0.8f, 0f, 1f) * (1f - sat * 0.6f);   // 푸르고 탁할수록 원경
                var xl = x > 0 ? x - 1 : x;
                var xr = x < w - 1 ? x + 1 : x;
                var grad = MathF.Abs(scratch[row + xr] - scratch[row + xl]) + MathF.Abs(scratch[dn + x] - scratch[up + x]);
                var contrast = MathF.Min(1f, grad * 2f);
                depth[i] = Math.Clamp(0.55f * prior + 0.25f * haze + 0.20f * (1f - contrast), 0f, 1f);
            }
        }
        BoxBlur(depth, scratch, w, h, Math.Max(1, w / 64));
    }

    /// <summary>scratch 대여 오버로드(UI·테스트용).</summary>
    public static void EstimateDepth(ReadOnlySpan<byte> rgb, int w, int h, Span<float> depth)
    {
        var s = ArrayPool<float>.Shared.Rent(w * h);
        try { EstimateDepth(rgb, w, h, depth, s); }
        finally { ArrayPool<float>.Shared.Return(s); }
    }

    /// <summary>결선점 우선 → 실패 시 BCL 폴백(예외 격리).</summary>
    public void EstimateDepthWithHook(byte[] rgb, int w, int h, Span<float> depth, Span<float> scratch)
    {
        var hook = ExternalDepthEstimator;
        if (hook is not null)
        {
            try
            {
                if (hook(rgb, w, h) is { } ext && ext.Length >= w * h)
                {
                    ext.AsSpan(0, w * h).CopyTo(depth);
                    return;
                }
            }
            catch (Exception ex) { Program.Log("Splat.DepthHook", ex); }
        }
        EstimateDepth(rgb, w, h, depth, scratch);
    }

    private static void BoxBlur(Span<float> data, Span<float> tmp, int w, int h, int r)
    {
        var inv = 1f / (2 * r + 1);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var sum = 0f;
            for (var k = -r; k <= r; k++) sum += data[row + Math.Clamp(k, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                sum += data[row + Math.Min(x + r + 1, w - 1)] - data[row + Math.Max(x - r, 0)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                data[y * w + x] = sum * inv;
                sum += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Max(y - r, 0) * w + x];
            }
        }
    }

    /// <summary>깊이 중앙값(64빈 히스토그램 · 무할당).</summary>
    public static float MedianDepth(ReadOnlySpan<float> depth)
    {
        if (depth.IsEmpty) return 0.5f;
        Span<int> bins = stackalloc int[64];
        for (var i = 0; i < depth.Length; i++) bins[Math.Clamp((int)(depth[i] * 64f), 0, 63)]++;
        var half = depth.Length / 2;
        var acc = 0;
        for (var b = 0; b < 64; b++) { acc += bins[b]; if (acc > half) return (b + 0.5f) / 64f; }
        return 0.5f;
    }

    // ══════════════════════ 카메라 패닝 정합 ══════════════════════

    /// <summary>전역 SAD 정합 — cur(x+dx, y+dy) ≈ prev(x, y). 반환 = 신뢰도(0~1, 0 이동 대비 SAD 감소율).</summary>
    public static float EstimatePan(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, int w, int h, int radius, out int dx, out int dy)
    {
        dx = 0; dy = 0;
        var m = radius;
        if (w <= 2 * m + 8 || h <= 2 * m + 8 || prev.Length < w * h || cur.Length < w * h) return 0f;
        long best = long.MaxValue, zero = 0;
        for (var oy = -m; oy <= m; oy++)
        {
            for (var ox = -m; ox <= m; ox++)
            {
                long sad = 0;
                for (var y = m; y < h - m; y += 2)
                {
                    var rp = y * w;
                    var rc = (y + oy) * w + ox;
                    for (var x = m; x < w - m; x += 2) sad += Math.Abs(prev[rp + x] - cur[rc + x]);
                }
                if (ox == 0 && oy == 0) zero = sad;
                if (sad < best || (sad == best && Math.Abs(ox) + Math.Abs(oy) < Math.Abs(dx) + Math.Abs(dy))) { best = sad; dx = ox; dy = oy; }
            }
        }
        if (zero == 0) return 1f;
        return Math.Clamp(1f - (float)best / zero, 0f, 1f);
    }

    // ══════════════════════ 스플랫 통합 ══════════════════════

    /// <summary>프레임 1장 언프로젝션 → 가우시안 추가(stride 격자 · 상한 도달 시 중단). 반환 = 추가 개수.</summary>
    public int IntegrateFrame(ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, int w, int h, in SpatialCameraPose pose, float hfovDeg, int stride)
    {
        if (w < 2 || h < 2 || rgb.Length < w * h * 3 || depth.Length < w * h) return 0;
        stride = Math.Clamp(stride, 1, 16);
        var f = FocalPx(w, hfovDeg);
        var invF = 1f / f;
        float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f;
        float cyw = MathF.Cos(pose.Yaw), syw = MathF.Sin(pose.Yaw), cp = MathF.Cos(pose.Pitch), sp = MathF.Sin(pose.Pitch);
        var added = 0;
        lock (_gate)
        {
            _splats ??= new GaussianSplat[MaxSplats];
            _hfov = hfovDeg;
            for (var v = 0; v < h; v += stride)
            {
                for (var u = 0; u < w; u += stride)
                {
                    if (_count >= MaxSplats) goto Full;
                    var i = v * w + u;
                    var d = depth[i];
                    var un = u + stride < w ? i + stride : i;
                    var vn = v + stride < h ? i + stride * w : i;
                    var edge = MathF.Abs(depth[un] - d) + MathF.Abs(depth[vn] - d) > 0.08f;
                    var z = DepthToMeters(d);
                    var x = (u - cx) * invF * z;
                    var y = -(v - cy) * invF * z;
                    RotX(ref y, ref z, cp, sp);
                    RotY(ref x, ref z, cyw, syw);
                    ref var s = ref _splats[_count++];
                    s.X = x + pose.Tx; s.Y = y + pose.Ty; s.Z = z + pose.Tz;
                    s.Scale = DepthToMeters(d) * stride * invF * 0.6f;
                    s.Opacity = edge ? 0.45f : 0.92f;
                    var j = i * 3;
                    s.R = rgb[j]; s.G = rgb[j + 1]; s.B = rgb[j + 2];
                    added++;
                }
            }
        Full:;
        }
        Interlocked.Increment(ref _frames);
        return added;
    }

    private static void RotY(ref float x, ref float z, float c, float s) { var nx = x * c + z * s; var nz = -x * s + z * c; x = nx; z = nz; }
    private static void RotX(ref float y, ref float z, float c, float s) { var ny = y * c - z * s; var nz = y * s + z * c; y = ny; z = nz; }

    // ══════════════════════ 렌더 ══════════════════════

    /// <summary>2패스 표면 스플래팅 → rgb24(w·h·3). 스플랫이 없으면 배경색.</summary>
    public void Render(in SpatialCameraPose pose, int w, int h, float hfovDeg, Span<byte> rgbOut)
    {
        var n = w * h;
        if (w < 2 || h < 2 || rgbOut.Length < n * 3) return;
        var f = FocalPx(w, hfovDeg);
        float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f;
        float cyw = MathF.Cos(pose.Yaw), syw = MathF.Sin(pose.Yaw), cp = MathF.Cos(pose.Pitch), sp = MathF.Sin(pose.Pitch);
        lock (_gate)
        {
            if (_zbuf is null || _zbuf.Length < n) { _zbuf = new float[n]; _acc = new float[n * 4]; }
            var zb = _zbuf.AsSpan(0, n);
            var acc = _acc.AsSpan(0, n * 4);
            zb.Fill(float.PositiveInfinity);
            acc.Clear();
            var splats = _splats is null ? Span<GaussianSplat>.Empty : _splats.AsSpan(0, _count);
            for (var pass = 0; pass < 2; pass++)
            {
                for (var k = 0; k < splats.Length; k++)
                {
                    ref readonly var s = ref splats[k];
                    float x = s.X - pose.Tx, y = s.Y - pose.Ty, z = s.Z - pose.Tz;
                    RotY(ref x, ref z, cyw, -syw);
                    RotX(ref y, ref z, cp, -sp);
                    if (z < 0.05f) continue;
                    var px = cx + f * x / z;
                    var py = cy - f * y / z;
                    var sigma = MathF.Max(0.5f, s.Scale * f / z);
                    var r = Math.Min(MaxFootprintPx, (int)MathF.Ceiling(sigma * 2f));
                    int x0 = (int)MathF.Round(px), y0 = (int)MathF.Round(py);
                    if (x0 + r < 0 || x0 - r >= w || y0 + r < 0 || y0 - r >= h) continue;
                    var inv2s = 1f / (2f * sigma * sigma);
                    for (var yy = Math.Max(0, y0 - r); yy <= Math.Min(h - 1, y0 + r); yy++)
                    {
                        for (var xx = Math.Max(0, x0 - r); xx <= Math.Min(w - 1, x0 + r); xx++)
                        {
                            var p = yy * w + xx;
                            if (pass == 0) { if (z < zb[p]) zb[p] = z; continue; }
                            if (z - zb[p] > zb[p] * DepthTolerance) continue;
                            float ddx = xx - px, ddy = yy - py;
                            var wt = MathF.Exp(-(ddx * ddx + ddy * ddy) * inv2s) * s.Opacity;
                            var a = p * 4;
                            acc[a] += wt * s.R; acc[a + 1] += wt * s.G; acc[a + 2] += wt * s.B; acc[a + 3] += wt;
                        }
                    }
                }
            }
            for (var p = 0; p < n; p++)
            {
                var a = p * 4;
                var o = p * 3;
                var ws = acc[a + 3];
                if (ws > 1e-4f)
                {
                    var iw = 1f / ws;
                    rgbOut[o] = (byte)Math.Min(255f, acc[a] * iw);
                    rgbOut[o + 1] = (byte)Math.Min(255f, acc[a + 1] * iw);
                    rgbOut[o + 2] = (byte)Math.Min(255f, acc[a + 2] * iw);
                }
                else { rgbOut[o] = 8; rgbOut[o + 1] = 10; rgbOut[o + 2] = 16; }
            }
        }
    }

    /// <summary>좌/우 안구 렌더(카메라 우측 벡터 ±IPD/2).</summary>
    public void RenderStereo(in SpatialCameraPose pose, float ipdMeters, int w, int h, float hfovDeg, Span<byte> left, Span<byte> right)
    {
        var half = ipdMeters * 0.5f;
        float rx = MathF.Cos(pose.Yaw), rz = -MathF.Sin(pose.Yaw);
        Render(pose with { Tx = pose.Tx - rx * half, Tz = pose.Tz - rz * half }, w, h, hfovDeg, left);
        Render(pose with { Tx = pose.Tx + rx * half, Tz = pose.Tz + rz * half }, w, h, hfovDeg, right);
    }

    /// <summary>UI 프리뷰 — 오빗 t 시점 렌더 → BMP DataURL(스플랫 없으면 null).</summary>
    public string? RenderPreviewDataUrl(double orbitT, float arcDeg, int w = 320, int h = 180)
    {
        if (SplatCount == 0) return null;
        var buf = new byte[w * h * 3];
        float pivot, hfov;
        lock (_gate) { pivot = _pivot; hfov = _hfov; }
        Render(SpatialCameraPose.Orbit(orbitT, pivot, arcDeg), w, h, hfov, buf);
        return SpatialImageCodec.BmpDataUrl(buf, w, h);
    }

    // ══════════════════════ 비디오/사진 → 씬 복원 ══════════════════════

    /// <summary>
    /// 영상(또는 사진)을 2fps·256폭으로 디코드 → 프레임별 깊이 + 패닝 정합 → 키프레임 스플랫 통합.
    /// ffmpeg 부재/동시 실행/디코드 실패는 Graceful(예외 0) · 기존 씬은 실패 시 보존([부분 구제]).
    /// </summary>
    public async Task<SplatReconstructionResult> ReconstructAsync(string mediaPath, int maxFrames = 24, float hfovDeg = DefaultHfovDeg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return Done(new(false, 0, 0, SplatCount, 0, 0, "미디어 파일이 없습니다."));
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return Done(new(false, 0, 0, SplatCount, 0, 0, "ffmpeg 실행 파일을 찾을 수 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, 0, 0, SplatCount, 0, 0, "다른 복원이 진행 중입니다.");
        LastAction = "🧊 3DGS 복원 중 · " + Path.GetFileName(mediaPath);
        Raise();
        byte[]? rgb = null, luma = null, prevLuma = null;
        float[]? depth = null, scratch = null;
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, mediaPath, ct).ConfigureAwait(false);
            if (probe is null) return Done(new(false, 0, 0, SplatCount, 0, 0, "해상도를 읽지 못했습니다."));
            var w = ReconWidth;
            var h = Math.Max(16, (int)Math.Round(ReconWidth * (double)probe.Value.H / probe.Value.W) & ~1);
            maxFrames = Math.Clamp(maxFrames, 1, 240);
            var ci = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", mediaPath, "-vf", "fps=" + ReconFps.ToString(ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci),
                         "-frames:v", maxFrames.ToString(ci), "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
                psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return Done(new(false, 0, 0, SplatCount, 0, 0, "ffmpeg 시작 실패"));
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(ReconTimeout);
            _ = proc.StandardError.ReadToEndAsync(d.Token);

            var n = w * h;
            rgb = new byte[n * 3];   // 결선점 계약(byte[]) — 복원 1회 1개
            luma = ArrayPool<byte>.Shared.Rent(n);
            prevLuma = ArrayPool<byte>.Shared.Rent(n);
            depth = ArrayPool<float>.Shared.Rent(n);
            scratch = ArrayPool<float>.Shared.Rent(n);
            var f = FocalPx(w, hfovDeg);
            float yaw = 0, pitch = 0, lastKeyYaw = float.NaN, lastKeyPitch = 0;
            int frames = 0, keys = 0;
            var stream = proc.StandardOutput.BaseStream;
            lock (_gate) _count = 0;
            Volatile.Write(ref _frames, 0);
            while (frames < maxFrames && await ReadExactAsync(stream, rgb, n * 3, d.Token).ConfigureAwait(false))
            {
                ToLuma(rgb, luma.AsSpan(0, n));
                if (frames > 0)
                {
                    var conf = EstimatePan(prevLuma.AsSpan(0, n), luma.AsSpan(0, n), w, h, PanRadius, out var dx, out var dy);
                    if (conf > 0.15f)
                    {
                        yaw -= MathF.Atan(dx / f);     // 콘텐츠 좌측 이동 = 카메라 우측 패닝
                        pitch -= MathF.Atan(dy / f);   // 콘텐츠 상향 이동 = 카메라 하향 틸트
                    }
                }
                (prevLuma, luma) = (luma, prevLuma);
                frames++;
                var moved = float.IsNaN(lastKeyYaw) ? float.PositiveInfinity
                    : MathF.Max(MathF.Abs(yaw - lastKeyYaw), MathF.Abs(pitch - lastKeyPitch)) * (180f / MathF.PI);
                if (moved < KeyframeDeg) continue;
                EstimateDepthWithHook(rgb, w, h, depth.AsSpan(0, n), scratch.AsSpan(0, n));
                if (keys == 0)
                {
                    var pv = DepthToMeters(MedianDepth(depth.AsSpan(0, n)));
                    lock (_gate) _pivot = pv;
                }
                IntegrateFrame(rgb, depth.AsSpan(0, n), w, h, new SpatialCameraPose(0, 0, 0, yaw, pitch), hfovDeg, keys == 0 ? 2 : 3);
                lastKeyYaw = yaw; lastKeyPitch = pitch;
                keys++;
                if (SplatCount >= MaxSplats) break;
            }
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            if (frames == 0) return Done(new(false, 0, 0, SplatCount, 0, 0, "디코드된 프레임이 없습니다."));
            Interlocked.Increment(ref _recons);
            var yd = yaw * (180f / MathF.PI);
            var pd = pitch * (180f / MathF.PI);
            var cnt = SplatCount;
            LastAction = "🧊 3DGS 복원 · " + cnt.ToString("N0", ci) + " 스플랫 · 키 " + keys.ToString(ci) + " · 패닝 " + yd.ToString("0.#", ci) + "°";
            return Done(new(true, frames, keys, cnt, yd, pd, "복원 완료"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Done(new(false, 0, 0, SplatCount, 0, 0, "취소됨")); }
        catch (Exception ex) { Program.Log("Splat.Reconstruct", ex); return Done(new(false, 0, 0, SplatCount, 0, 0, "복원 실패: " + ex.Message)); }
        finally
        {
            if (luma is not null) ArrayPool<byte>.Shared.Return(luma);
            if (prevLuma is not null) ArrayPool<byte>.Shared.Return(prevLuma);
            if (depth is not null) ArrayPool<float>.Shared.Return(depth);
            if (scratch is not null) ArrayPool<float>.Shared.Return(scratch);
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private SplatReconstructionResult Done(SplatReconstructionResult r)
    {
        LastResult = r;
        if (!r.Ok) LastAction = "⚠ 3DGS " + r.Message;
        return r;
    }

    // ══════════════════════ PLY 내보내기 ══════════════════════

    /// <summary>표준 3DGS PLY(binary LE · OpenCV y-하향 좌표로 변환) — 임시 파일 후 원자 교체. 반환 = 기록 개수(0=스플랫 없음).</summary>
    public int ExportPly(string path)
    {
        const float C0 = 0.28209479f;
        lock (_gate)
        {
            if (_splats is null || _count == 0) return 0;
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var header = "ply\nformat binary_little_endian 1.0\nelement vertex " + _count.ToString(CultureInfo.InvariantCulture) +
                             "\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz" +
                             "\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float opacity" +
                             "\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2" +
                             "\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
                fs.Write(Encoding.ASCII.GetBytes(header));
                Span<byte> rec = stackalloc byte[68];
                for (var k = 0; k < _count; k++)
                {
                    ref readonly var s = ref _splats[k];
                    var o = Math.Clamp(s.Opacity, 1e-4f, 1f - 1e-4f);
                    var ls = MathF.Log(MathF.Max(s.Scale, 1e-6f));
                    WriteF(rec, 0, s.X); WriteF(rec, 1, -s.Y); WriteF(rec, 2, s.Z);
                    WriteF(rec, 3, 0); WriteF(rec, 4, 0); WriteF(rec, 5, 0);
                    WriteF(rec, 6, (s.R / 255f - 0.5f) / C0); WriteF(rec, 7, (s.G / 255f - 0.5f) / C0); WriteF(rec, 8, (s.B / 255f - 0.5f) / C0);
                    WriteF(rec, 9, MathF.Log(o / (1f - o)));
                    WriteF(rec, 10, ls); WriteF(rec, 11, ls); WriteF(rec, 12, ls);
                    WriteF(rec, 13, 1); WriteF(rec, 14, 0); WriteF(rec, 15, 0); WriteF(rec, 16, 0);
                    fs.Write(rec);
                }
            }
            File.Move(tmp, path, true);
            return _count;
        }
    }

    private static void WriteF(Span<byte> rec, int idx, float v) => BinaryPrimitives.WriteSingleLittleEndian(rec.Slice(idx * 4, 4), v);

    internal static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        var off = 0;
        while (off < count)
        {
            var r = await s.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Splat.OnChange", ex); }
        }
    }
}

/// <summary>공간 프리뷰 이미지 코덱(BMP 24bit · 깊이 컬러맵) — 외부 이미지 라이브러리 0.</summary>
public static class SpatialImageCodec
{
    /// <summary>rgb24 → BMP(BGR · 하향식 아님 · 4바이트 행 정렬) 바이트.</summary>
    public static byte[] Bmp(ReadOnlySpan<byte> rgb, int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        var size = 54 + stride * h;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), size);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), h);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(34), stride * h);
        for (var y = 0; y < h; y++)
        {
            var dst = 54 + (h - 1 - y) * stride;
            var src = y * w * 3;
            for (var x = 0; x < w; x++, dst += 3, src += 3)
            {
                b[dst] = rgb[src + 2]; b[dst + 1] = rgb[src + 1]; b[dst + 2] = rgb[src];
            }
        }
        return b;
    }

    public static string BmpDataUrl(ReadOnlySpan<byte> rgb, int w, int h) => "data:image/bmp;base64," + Convert.ToBase64String(Bmp(rgb, w, h));

    /// <summary>정규화 깊이 → 컬러맵(근=난색 · 원=한색 · 무할당).</summary>
    public static void DepthToRgb(ReadOnlySpan<float> depth, Span<byte> rgb)
    {
        for (int i = 0, j = 0; i < depth.Length && j + 2 < rgb.Length; i++, j += 3)
        {
            var t = Math.Clamp(depth[i], 0f, 1f);
            rgb[j] = (byte)(255f * Math.Clamp(1.6f - 2f * t, 0f, 1f));
            rgb[j + 1] = (byte)(255f * Math.Clamp(1f - MathF.Abs(t - 0.45f) * 2.2f, 0f, 1f));
            rgb[j + 2] = (byte)(255f * Math.Clamp(2f * t - 0.3f, 0f, 1f));
        }
    }
}
