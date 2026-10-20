using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService

namespace PromptFlowStudio.Services.Spatial;

public enum SpatialTarget : byte { VisionPro, MetaQuest }

/// <summary>입체 시차 설정 — 최대 시차(폭 %) · 수렴면(정규화 깊이 · 이 깊이가 화면면) · 기준선(mm) · 수평 FOV(도).</summary>
public readonly record struct StereoSettings(float MaxDisparityPct, float Convergence, float BaselineMm, float HfovDeg)
{
    public static readonly StereoSettings Default = new(2.5f, 0.35f, 64f, Spatial3dGaussianSplattingEngine.DefaultHfovDeg);

    public StereoSettings Clamp() => new(Math.Clamp(MaxDisparityPct, 0.2f, 6f), Math.Clamp(Convergence, 0f, 1f), Math.Clamp(BaselineMm, 10f, 90f), Math.Clamp(HfovDeg, 30f, 120f));
}

public sealed record SpatialTranscodeRequest(string InputPath, string OutputFolder, SpatialTarget Target, StereoSettings Stereo,
    string? BaseName = null, int EyeWidth = 960, double Fps = 30, bool Binaural = true);

public sealed record SpatialTranscodeResult(bool Ok, string? VideoPath, string Format, int Frames, string? Encoder, bool Binaural, bool PartialRescue, string Message);

public sealed record SpatialPackageResult(bool Ok, string? Folder, SpatialTranscodeResult? Video, int Splats, int Hotspots, string Message);

public sealed record SpatialFramePreview(int Width, int Height, double Seconds, float MedianDepthMeters, string DepthDataUrl, string SbsDataUrl);

/// <summary>
/// [PromptFlow Studio v16.31.0] MV-HEVC 공간 비디오 &amp; 바이노럴 입체 음향 트랜스코더(순수 BCL + ffmpeg 프로세스).
///   ▸ 영상 패스: ffmpeg rawvideo 디코드 → 프레임별 단안 깊이 → DIBR(깊이 기반 시점 합성 · z-테스트 포워드 워프 · 배경측 홀 채움) → SBS(좌/우) HEVC 인코드.
///   ▸ 시차: half = maxDisparity·(수렴 − 깊이)/2 — 수렴면보다 가까우면 교차 시차(화면 앞 돌출), 멀면 비교차 시차.
///   ▸ 음향 패스: 모노 48kHz 디코드 → 주피사체(근경 무게중심) 방위 트랙 → Woodworth ITD(분수 지연) + ILD + 머리 그림자 1차 LPF 바이노럴 → float WAV.
///   ▸ Vision Pro: SBS → MV-HEVC(.mov, Apple 'spatial' CLI · PFS_SPATIAL_CLI) 결선 · 부재/실패 시 SBS-HEVC [부분 구제].
///   ▸ Meta Quest: SBS_LR HEVC .mp4(stereo_mode=left_right 메타데이터).
///   ▸ 패키지: 비디오 + .usda(공간 핫스팟 씬) + 3DGS .ply + manifest.json(SHA-256 지문).
///   ▸ 프레임 루프는 디코드·깊이·합성 버퍼를 1회 대여/재사용(프레임당 할당 0) · 예외/취소 시 프로세스 Kill · 임시 파일 정리.
/// </summary>
public sealed class MvHevcSpatialVideoTranscoder
{
    public const int PreviewWidth = 320;
    public const int MaxFrames = 18_000;
    public const int AudioRate = 48_000;
    public const int MaxAudioSeconds = 600;
    public static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string?> _ffmpeg;
    private readonly Func<string?> _spatialCli;
    private readonly Spatial3dGaussianSplattingEngine? _splat;
    private readonly object _gate = new();
    private string? _encoder;
    private int _busy, _transcoded;
    private double _progress;
    private byte[]? _pvRgb;
    private float[]? _pvDepth;
    private int _pvW, _pvH;

    public MvHevcSpatialVideoTranscoder(IVideoMergeService merge, Spatial3dGaussianSplattingEngine splat)
        : this(() => merge.ResolvedFfmpegPath, splat, () => s_cli.Value) { }

    private static readonly Lazy<string?> s_cli = new(ResolveSpatialCli);   // PATH 스캔 1회(HUD 렌더 경로 보호)

    public MvHevcSpatialVideoTranscoder(Func<string?> ffmpeg, Spatial3dGaussianSplattingEngine? splat, Func<string?>? spatialCli)
    {
        _ffmpeg = ffmpeg;
        _splat = splat;
        _spatialCli = spatialCli ?? (() => null);
    }

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public bool MvHevcAvailable => _spatialCli() is not null;
    public double Progress => Volatile.Read(ref _progress);
    public int Transcoded => Volatile.Read(ref _transcoded);
    public StereoSettings Settings { get; set; } = StereoSettings.Default;
    public string? LastAction { get; private set; }
    public SpatialTranscodeResult? LastResult { get; private set; }
    public SpatialFramePreview? LastPreview { get; private set; }

    /// <summary>마지막 분석 프레임의 정규화 깊이(핫스팟 깊이 샘플링용 · 없으면 빈 값).</summary>
    public (float[]? Depth, int W, int H) PreviewDepth { get { lock (_gate) return (_pvDepth, _pvW, _pvH); } }

    // ══════════════════════ DIBR 입체 합성 ══════════════════════

    /// <summary>
    /// 깊이 기반 좌/우 시점 합성 → SBS(2w·h·3). zRow ≥ 2w(행 z-버퍼). 무할당.
    /// 근(깊이 &lt; 수렴)일수록 좌안 우측/우안 좌측으로 이동(교차 시차), 홀은 인접 배경측 색으로 채움.
    /// </summary>
    public static void SynthesizeSbs(ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, int w, int h, in StereoSettings s, Span<byte> sbs, Span<float> zRow)
    {
        if (w < 2 || h < 1 || rgb.Length < w * h * 3 || depth.Length < w * h || sbs.Length < w * h * 6 || zRow.Length < 2 * w)
            throw new ArgumentException("버퍼 크기 불일치");
        var st = s.Clamp();
        var maxD = st.MaxDisparityPct * 0.01f * w;
        var conv = st.Convergence;
        var zl = zRow.Slice(0, w);
        var zr = zRow.Slice(w, w);
        var eye = w * 3;
        for (var y = 0; y < h; y++)
        {
            var src = y * w;
            var dst = y * eye * 2;
            zl.Fill(float.PositiveInfinity);
            zr.Fill(float.PositiveInfinity);
            for (var x = 0; x < w; x++)
            {
                var d = depth[src + x];
                var half = maxD * (conv - d) * 0.5f;
                var xl = (int)MathF.Round(x + half);
                var xr = (int)MathF.Round(x - half);
                var si = (src + x) * 3;
                if ((uint)xl < (uint)w && d < zl[xl])
                {
                    zl[xl] = d;
                    var o = dst + xl * 3;
                    sbs[o] = rgb[si]; sbs[o + 1] = rgb[si + 1]; sbs[o + 2] = rgb[si + 2];
                }
                if ((uint)xr < (uint)w && d < zr[xr])
                {
                    zr[xr] = d;
                    var o = dst + eye + xr * 3;
                    sbs[o] = rgb[si]; sbs[o + 1] = rgb[si + 1]; sbs[o + 2] = rgb[si + 2];
                }
            }
            FillHoles(sbs.Slice(dst, eye), zl);
            FillHoles(sbs.Slice(dst + eye, eye), zr);
        }
    }

    /// <summary>행 홀 채움 — 연속 홀 구간의 양쪽 이웃 중 더 먼(배경) 쪽 색으로 채움(디스오클루전 = 배경 노출).</summary>
    private static void FillHoles(Span<byte> row, Span<float> z)
    {
        var w = z.Length;
        var x = 0;
        while (x < w)
        {
            if (!float.IsPositiveInfinity(z[x])) { x++; continue; }
            var a = x;
            while (x < w && float.IsPositiveInfinity(z[x])) x++;
            var left = a - 1;
            var right = x < w ? x : -1;
            int pick;
            if (left < 0 && right < 0) pick = -1;
            else if (left < 0) pick = right;
            else if (right < 0) pick = left;
            else pick = z[left] >= z[right] ? left : right;
            byte r = 0, g = 0, b = 0;
            var pz = 1f;
            if (pick >= 0) { r = row[pick * 3]; g = row[pick * 3 + 1]; b = row[pick * 3 + 2]; pz = z[pick]; }
            for (var k = a; k < x; k++) { row[k * 3] = r; row[k * 3 + 1] = g; row[k * 3 + 2] = b; z[k] = pz; }
        }
    }

    /// <summary>주피사체 방위(도 · 우 +) — 중앙값보다 0.1 이상 가까운 근경 픽셀의 가로 무게중심. 무할당.</summary>
    public static float SubjectAzimuthDeg(ReadOnlySpan<float> depth, int w, int h, float hfovDeg)
    {
        if (w < 2 || h < 1 || depth.Length < w * h) return 0f;
        var cut = Spatial3dGaussianSplattingEngine.MedianDepth(depth.Slice(0, w * h)) - 0.1f;
        double sx = 0, sw = 0;
        for (var y = 0; y < h; y += 2)
        {
            var row = y * w;
            for (var x = 0; x < w; x += 2)
            {
                var d = depth[row + x];
                if (d >= cut) continue;
                var wt = cut - d;
                sx += x * wt; sw += wt;
            }
        }
        if (sw < 1e-6) return 0f;
        var f = Spatial3dGaussianSplattingEngine.FocalPx(w, hfovDeg);
        return MathF.Atan(((float)(sx / sw) - (w - 1) * 0.5f) / f) * (180f / MathF.PI);
    }

    // ══════════════════════ 바이노럴 ══════════════════════

    /// <summary>Woodworth ITD(초) — 머리 반경 8.75cm · 음속 343m/s · 방위 ±90° 포화.</summary>
    public static float ItdSeconds(float azimuthDeg)
    {
        var t = MathF.Abs(Math.Clamp(azimuthDeg, -90f, 90f)) * (MathF.PI / 180f);
        return 0.0875f / 343f * (t + MathF.Sin(t));
    }

    /// <summary>
    /// 모노 → 바이노럴 스테레오(인터리브 L,R · stereo ≥ 2·mono). azimuthTrack 은 trackRate(Hz) 샘플 방위(도 · 우 +) — 64샘플 블록 선형 보간.
    /// 원측 귀: ITD 분수 지연 + 레벨 감쇠 + 머리 그림자 LPF. 무할당.
    /// </summary>
    public static void Binauralize(ReadOnlySpan<float> mono, Span<float> stereo, int sampleRate, ReadOnlySpan<float> azimuthTrack, double trackRate)
    {
        if (stereo.Length < mono.Length * 2 || sampleRate <= 0) throw new ArgumentException("버퍼 크기 불일치");
        const int Block = 64;
        var lp = 0f;
        for (var i0 = 0; i0 < mono.Length; i0 += Block)
        {
            var az = 0f;
            if (!azimuthTrack.IsEmpty && trackRate > 0)
            {
                var t = i0 / (double)sampleRate * trackRate;
                var k = (int)t;
                if (k >= azimuthTrack.Length - 1) az = azimuthTrack[^1];
                else { var fr = (float)(t - k); az = azimuthTrack[k] + (azimuthTrack[k + 1] - azimuthTrack[k]) * fr; }
            }
            var sinA = MathF.Abs(MathF.Sin(Math.Clamp(az, -90f, 90f) * (MathF.PI / 180f)));
            var itd = ItdSeconds(az) * sampleRate;
            var farGain = 1f - 0.45f * sinA;
            var nearGain = 1f + 0.15f * sinA;
            var fc = 20000f * (1f - sinA) + 1200f * sinA;
            var alpha = 1f - MathF.Exp(-2f * MathF.PI * fc / sampleRate);
            var rightNear = az >= 0;
            var end = Math.Min(mono.Length, i0 + Block);
            for (var i = i0; i < end; i++)
            {
                var pos = i - itd;
                var k0 = (int)MathF.Floor(pos);
                var fr = pos - k0;
                float delayed = 0;
                if (k0 >= 0) delayed = k0 + 1 < mono.Length ? mono[k0] + (mono[k0 + 1] - mono[k0]) * fr : mono[k0];
                lp += alpha * (delayed * farGain - lp);
                var near = mono[i] * nearGain;
                stereo[2 * i] = rightNear ? lp : near;
                stereo[2 * i + 1] = rightNear ? near : lp;
            }
        }
    }

    /// <summary>IEEE float 스테레오 WAV 기록.</summary>
    public static void WriteFloatWav(string path, ReadOnlySpan<float> interleaved, int sampleRate)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var bw = new BinaryWriter(fs);
        var dataBytes = interleaved.Length * 4;
        bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)3); bw.Write((short)2); bw.Write(sampleRate); bw.Write(sampleRate * 8); bw.Write((short)8); bw.Write((short)32);
        bw.Write("data"u8); bw.Write(dataBytes);
        bw.Write(MemoryMarshal.AsBytes(interleaved));
    }

    // ══════════════════════ 인자 빌더(테스트 가능) ══════════════════════

    /// <summary>ffmpeg -encoders 출력 → HEVC 우선 인코더(libx265 → 하드웨어 → libx264).</summary>
    public static string PickEncoder(string? encodersText)
    {
        var t = encodersText ?? "";
        foreach (var e in new[] { "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf" })
            if (t.Contains(" " + e + " ", StringComparison.Ordinal)) return e;
        return "libx264";
    }

    public static bool IsHevc(string encoder) => encoder.StartsWith("hevc", StringComparison.Ordinal) || encoder == "libx265";

    public static List<string> BuildEncodeArgs(string encoder, int eyeW, int eyeH, double fps, string output)
    {
        var ci = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
            "-s", (eyeW * 2).ToString(ci) + "x" + eyeH.ToString(ci), "-r", fps.ToString("0.###", ci), "-i", "pipe:0", "-c:v", encoder };
        if (encoder == "libx265") a.AddRange(new[] { "-crf", "20", "-preset", "medium", "-x265-params", "log-level=error" });
        else if (encoder == "libx264") a.AddRange(new[] { "-crf", "18", "-preset", "medium" });
        else a.AddRange(new[] { "-b:v", "20M" });
        a.AddRange(new[] { "-pix_fmt", "yuv420p" });
        if (IsHevc(encoder)) a.AddRange(new[] { "-tag:v", "hvc1" });
        a.AddRange(new[] { "-an", output });
        return a;
    }

    public static List<string> BuildMuxArgs(string video, string? wav, string output)
    {
        var a = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", video };
        if (wav is not null) a.AddRange(new[] { "-i", wav });
        a.AddRange(new[] { "-map", "0:v:0" });
        if (wav is not null) a.AddRange(new[] { "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k" });
        a.AddRange(new[] { "-c:v", "copy", "-metadata:s:v:0", "stereo_mode=left_right", "-movflags", "+faststart", output });
        return a;
    }

    /// <summary>Apple 'spatial' CLI — SBS → MV-HEVC(좌안 주 시점 · 직선 투영 · 기준선/FOV 메타).</summary>
    public static List<string> BuildMvHevcArgs(string sbsPath, string output, in StereoSettings s)
    {
        var ci = CultureInfo.InvariantCulture;
        var st = s.Clamp();
        return new List<string> { "make", "--input", sbsPath, "--format", "sbs", "--output", output,
            "--cdist", st.BaselineMm.ToString("0.##", ci), "--hfov", st.HfovDeg.ToString("0.##", ci), "--hadjust", "0", "--primary", "left", "--projection", "rect" };
    }

    /// <summary>PFS_SPATIAL_CLI → PATH 의 spatial(.exe) 순 탐색(부재 null).</summary>
    public static string? ResolveSpatialCli()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("PFS_SPATIAL_CLI");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var name in new[] { "spatial", "spatial.exe" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch (Exception ex) { Program.Log("Spatial.ResolveCli", ex); }
        return null;
    }

    public static string SafeName(string? name)
    {
        var src = string.IsNullOrWhiteSpace(name) ? "spatial" : name.Trim();
        var sb = new StringBuilder(Math.Min(src.Length, 64));
        foreach (var ch in src)
        {
            if (sb.Length >= 64) break;
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }
        return sb.Length == 0 ? "spatial" : sb.ToString();
    }

    // ══════════════════════ 프레임 분석(깊이 인스펙터) ══════════════════════

    /// <summary>t초 프레임 1장 → 깊이 맵 + SBS 프리뷰(이후 슬라이더 변경은 <see cref="RebuildPreview"/>).</summary>
    public async Task<SpatialFramePreview?> AnalyzeFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) { LastAction = "⚠ 분석 불가(ffmpeg/파일 확인)"; Raise(); return null; }
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, mediaPath, ct).ConfigureAwait(false);
            if (probe is null) return null;
            var w = PreviewWidth;
            var h = Math.Max(16, (int)Math.Round(PreviewWidth * (double)probe.Value.H / probe.Value.W) & ~1);
            var t = probe.Value.Duration > 0 ? Math.Clamp(seconds, 0, Math.Max(0, probe.Value.Duration - 0.05)) : 0;
            var ci = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-ss", t.ToString("0.###", ci), "-i", mediaPath, "-frames:v", "1",
                         "-vf", "scale=" + w.ToString(ci) + ":" + h.ToString(ci), "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
                psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(30));
            _ = proc.StandardError.ReadToEndAsync(d.Token);
            var rgb = new byte[w * h * 3];
            var ok = await Spatial3dGaussianSplattingEngine.ReadExactAsync(proc.StandardOutput.BaseStream, rgb, rgb.Length, d.Token).ConfigureAwait(false);
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            if (!ok) { LastAction = "⚠ 프레임 디코드 실패"; Raise(); return null; }
            return SetPreview(rgb, w, h, t);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Spatial.Analyze", ex); LastAction = "⚠ 분석 실패: " + ex.Message; Raise(); return null; }
    }

    /// <summary>원본 rgb24 프레임으로 프리뷰 구성(깊이 추정 포함 · 테스트/외부 프레임 주입용).</summary>
    public SpatialFramePreview SetPreview(byte[] rgb, int w, int h, double seconds)
    {
        var depth = new float[w * h];
        var scratch = ArrayPool<float>.Shared.Rent(w * h);
        try
        {
            if (_splat is not null) _splat.EstimateDepthWithHook(rgb, w, h, depth, scratch.AsSpan(0, w * h));
            else Spatial3dGaussianSplattingEngine.EstimateDepth(rgb, w, h, depth, scratch.AsSpan(0, w * h));
        }
        finally { ArrayPool<float>.Shared.Return(scratch); }
        lock (_gate) { _pvRgb = rgb; _pvDepth = depth; _pvW = w; _pvH = h; }
        var p = BuildPreview(rgb, depth, w, h, seconds, Settings);
        LastPreview = p;
        LastAction = "🔎 깊이 분석 · 중앙 깊이 " + p.MedianDepthMeters.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        Raise();
        return p;
    }

    /// <summary>시차 슬라이더 변경 → 캐시된 프레임으로 SBS 재합성.</summary>
    public SpatialFramePreview? RebuildPreview()
    {
        byte[]? rgb; float[]? depth; int w, h;
        lock (_gate) { rgb = _pvRgb; depth = _pvDepth; w = _pvW; h = _pvH; }
        if (rgb is null || depth is null) return null;
        var p = BuildPreview(rgb, depth, w, h, LastPreview?.Seconds ?? 0, Settings);
        LastPreview = p;
        Raise();
        return p;
    }

    private static SpatialFramePreview BuildPreview(byte[] rgb, float[] depth, int w, int h, double seconds, StereoSettings s)
    {
        var drgb = new byte[w * h * 3];
        SpatialImageCodec.DepthToRgb(depth, drgb);
        var sbs = new byte[w * h * 6];
        SynthesizeSbs(rgb, depth, w, h, s, sbs, new float[2 * w]);
        var med = Spatial3dGaussianSplattingEngine.DepthToMeters(Spatial3dGaussianSplattingEngine.MedianDepth(depth));
        return new(w, h, seconds, med, SpatialImageCodec.BmpDataUrl(drgb, w, h), SpatialImageCodec.BmpDataUrl(sbs, w * 2, h));
    }

    // ══════════════════════ 트랜스코드 ══════════════════════

    public async Task<SpatialTranscodeResult> TranscodeAsync(SpatialTranscodeRequest req, CancellationToken ct = default)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.InputPath) || !File.Exists(req.InputPath)) return Finish(Fail("입력 영상이 없습니다."));
        if (string.IsNullOrWhiteSpace(req.OutputFolder)) return Finish(Fail("출력 폴더를 지정하세요."));
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return Finish(Fail("ffmpeg 실행 파일을 찾을 수 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Fail("다른 트랜스코드가 진행 중입니다.");
        Volatile.Write(ref _progress, 0);
        LastAction = "🥽 공간 비디오 변환 중 · " + Path.GetFileName(req.InputPath);
        Raise();
        string? tmpVideo = null, wav = null;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TranscodeTimeout);
            Directory.CreateDirectory(req.OutputFolder);
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, req.InputPath, d.Token).ConfigureAwait(false);
            if (probe is null) return Finish(Fail("영상 해상도를 읽지 못했습니다."));
            var eyeW = Math.Clamp(req.EyeWidth, 128, 1920) & ~1;
            var eyeH = Math.Max(16, (int)Math.Round(eyeW * (double)probe.Value.H / probe.Value.W) & ~1);
            var fps = Math.Clamp(req.Fps, 1, 60);
            var encoder = await PickEncoderAsync(ffmpeg, d.Token).ConfigureAwait(false);
            var baseName = SafeName(req.BaseName ?? Path.GetFileNameWithoutExtension(req.InputPath));
            var stereo = req.Stereo.Clamp();
            tmpVideo = Path.Combine(req.OutputFolder, baseName + ".sbs_v.tmp.mov");
            var expected = probe.Value.Duration > 0 ? Math.Max(1, (int)(probe.Value.Duration * fps)) : 0;

            var (frames, azimuth) = await RenderSbsAsync(ffmpeg, encoder, req.InputPath, tmpVideo, eyeW, eyeH, fps, stereo, expected, d.Token).ConfigureAwait(false);
            if (frames == 0 || !File.Exists(tmpVideo)) return Finish(Fail("SBS 인코드 실패(인코더 " + encoder + ")"));

            var binaural = false;
            if (req.Binaural)
            {
                wav = Path.Combine(req.OutputFolder, baseName + ".binaural.tmp.wav");
                binaural = await BuildBinauralWavAsync(ffmpeg, req.InputPath, wav, azimuth, fps, d.Token).ConfigureAwait(false);
                if (!binaural) wav = DeleteQuiet(wav);
            }

            var sbsPath = Path.Combine(req.OutputFolder, baseName + (req.Target == SpatialTarget.MetaQuest ? "_SBS_LR.mp4" : "_SBS_LR.mov"));
            var partial = false;
            var notes = new StringBuilder();
            if (await RunAsync(ffmpeg, BuildMuxArgs(tmpVideo, wav, sbsPath), TimeSpan.FromMinutes(5), d.Token).ConfigureAwait(false) != 0 || !File.Exists(sbsPath))
            {
                File.Move(tmpVideo, sbsPath, true);   // [부분 구제] 무음 SBS 보존
                tmpVideo = null;
                partial = true;
                binaural = false;
                notes.Append("오디오 먹싱 실패 → 무음 SBS 보존. ");
            }

            var outPath = sbsPath;
            var format = IsHevc(encoder) ? "SBS-HEVC" : "SBS-H.264";
            if (req.Target == SpatialTarget.VisionPro)
            {
                var cli = _spatialCli();
                var mv = Path.Combine(req.OutputFolder, baseName + "_MVHEVC.mov");
                if (cli is not null && IsHevc(encoder) && await RunAsync(cli, BuildMvHevcArgs(sbsPath, mv, stereo), CliTimeout, d.Token).ConfigureAwait(false) == 0 && File.Exists(mv))
                {
                    outPath = mv;
                    format = "MV-HEVC";
                }
                else
                {
                    partial = true;
                    notes.Append(cli is null ? "MV-HEVC CLI(PFS_SPATIAL_CLI) 부재 → SBS 폴백. " : "MV-HEVC 변환 실패 → SBS 폴백. ");
                }
            }

            Interlocked.Increment(ref _transcoded);
            Volatile.Write(ref _progress, 1);
            var msg = format + " · " + frames.ToString(CultureInfo.InvariantCulture) + "프레임 · " + encoder + (binaural ? " · 바이노럴" : "") + (notes.Length > 0 ? " · [부분 구제] " + notes.ToString().Trim() : "");
            LastAction = "🥽 " + msg;
            return Finish(new(true, outPath, format, frames, encoder, binaural, partial, msg));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Finish(Fail("취소됨")); }
        catch (OperationCanceledException) { return Finish(Fail("시간 초과")); }
        catch (Exception ex) { Program.Log("Spatial.Transcode", ex); return Finish(Fail("변환 실패: " + ex.Message)); }
        finally
        {
            if (tmpVideo is not null) DeleteQuiet(tmpVideo);
            if (wav is not null) DeleteQuiet(wav);
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private async Task<(int Frames, float[] Azimuth)> RenderSbsAsync(string ffmpeg, string encoder, string input, string output, int w, int h, double fps,
        StereoSettings stereo, int expected, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var dpsi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", input, "-vf", "fps=" + fps.ToString("0.###", ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci),
                     "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
            dpsi.ArgumentList.Add(a);
        var epsi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in BuildEncodeArgs(encoder, w, h, fps, output)) epsi.ArgumentList.Add(a);

        using var dec = new Process { StartInfo = dpsi };
        using var enc = new Process { StartInfo = epsi };
        var n = w * h;
        var rgb = new byte[n * 3];   // 결선점 계약(byte[]) — 트랜스코드 1회 1개
        var sbs = ArrayPool<byte>.Shared.Rent(n * 6);
        var depth = ArrayPool<float>.Shared.Rent(n);
        var scratch = ArrayPool<float>.Shared.Rent(n);
        var zRow = ArrayPool<float>.Shared.Rent(2 * w);
        var az = new List<float>(expected > 0 ? Math.Min(expected, MaxFrames) : 256);
        var frames = 0;
        try
        {
            if (!dec.Start() || !enc.Start()) return (0, Array.Empty<float>());
            _ = dec.StandardError.ReadToEndAsync(ct);
            _ = enc.StandardError.ReadToEndAsync(ct);
            var input0 = dec.StandardOutput.BaseStream;
            var sink = enc.StandardInput.BaseStream;
            while (frames < MaxFrames && await Spatial3dGaussianSplattingEngine.ReadExactAsync(input0, rgb, n * 3, ct).ConfigureAwait(false))
            {
                if (_splat is not null) _splat.EstimateDepthWithHook(rgb, w, h, depth.AsSpan(0, n), scratch.AsSpan(0, n));
                else Spatial3dGaussianSplattingEngine.EstimateDepth(rgb, w, h, depth.AsSpan(0, n), scratch.AsSpan(0, n));
                SynthesizeSbs(rgb, depth.AsSpan(0, n), w, h, stereo, sbs.AsSpan(0, n * 6), zRow.AsSpan(0, 2 * w));
                az.Add(SubjectAzimuthDeg(depth.AsSpan(0, n), w, h, stereo.HfovDeg));
                await sink.WriteAsync(sbs.AsMemory(0, n * 6), ct).ConfigureAwait(false);
                frames++;
                if (expected > 0 && (frames & 15) == 0)
                {
                    Volatile.Write(ref _progress, Math.Min(0.95, 0.9 * frames / expected));
                    Raise();
                }
            }
            await sink.FlushAsync(ct).ConfigureAwait(false);
            sink.Close();
            await enc.WaitForExitAsync(ct).ConfigureAwait(false);
            return (enc.ExitCode == 0 ? frames : 0, az.ToArray());
        }
        finally
        {
            try { if (!dec.HasExited) dec.Kill(true); } catch { }
            try { if (!enc.HasExited) enc.Kill(true); } catch { }
            ArrayPool<byte>.Shared.Return(sbs);
            ArrayPool<float>.Shared.Return(depth);
            ArrayPool<float>.Shared.Return(scratch);
            ArrayPool<float>.Shared.Return(zRow);
        }
    }

    private static async Task<bool> BuildBinauralWavAsync(string ffmpeg, string input, string wavPath, float[] azimuth, double fps, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", AudioRate.ToString(ci), "-t", MaxAudioSeconds.ToString(ci), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        if (!p.Start()) return false;
        _ = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        try { await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false); }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
        var count = (int)(ms.Length / 4);
        if (count < AudioRate / 10) return false;   // 오디오 트랙 없음/100ms 미만
        WriteBinauralWav(ms.GetBuffer(), count, wavPath, azimuth, fps);
        return true;
    }

    private static void WriteBinauralWav(byte[] pcm, int count, string wavPath, float[] azimuth, double fps)
    {
        var mono = MemoryMarshal.Cast<byte, float>(pcm.AsSpan(0, count * 4));
        var stereo = new float[count * 2];
        Binauralize(mono, stereo, AudioRate, azimuth, fps);
        WriteFloatWav(wavPath, stereo, AudioRate);
    }

    private async Task<string> PickEncoderAsync(string ffmpeg, CancellationToken ct)
    {
        if (_encoder is { } cached) return cached;
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-encoders");
        using var p = new Process { StartInfo = psi };
        if (!p.Start()) return "libx264";
        _ = p.StandardError.ReadToEndAsync(ct);
        var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return _encoder = PickEncoder(text);
    }

    private static async Task<int> RunAsync(string exe, List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start()) return -1;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(timeout);
            _ = p.StandardOutput.ReadToEndAsync(d.Token);
            _ = p.StandardError.ReadToEndAsync(d.Token);
            await p.WaitForExitAsync(d.Token).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return -2; }   // 개별 단계 타임아웃
        catch (System.ComponentModel.Win32Exception ex) { Program.Log("Spatial.Run", ex); return -3; }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
    }

    // ══════════════════════ 패키지 ══════════════════════

    /// <summary>
    /// 1-Click 공간 패키지 — {base}_spatial/ 에 비디오(MV-HEVC 또는 SBS) + {base}.usda + scene_splats.ply + manifest.json(SHA-256).
    /// usdaBuilder(비디오 파일명) 는 공간 핫스팟 씬 생성기(보통 <see cref="ImmersiveVisionProInteractionBridge.BuildUsda"/>).
    /// </summary>
    public async Task<SpatialPackageResult> ExportPackageAsync(SpatialTranscodeRequest req, Func<string, string>? usdaBuilder, int hotspots, CancellationToken ct = default)
    {
        var baseName = SafeName(req.BaseName ?? Path.GetFileNameWithoutExtension(req.InputPath));
        var folder = Path.Combine(req.OutputFolder, baseName + "_spatial");
        var v = await TranscodeAsync(req with { OutputFolder = folder, BaseName = baseName }, ct).ConfigureAwait(false);
        if (!v.Ok || v.VideoPath is null) return new(false, null, v, 0, 0, v.Message);
        try
        {
            var files = new List<string> { v.VideoPath };
            var splats = 0;
            if (_splat is not null && _splat.SplatCount > 0)
            {
                var ply = Path.Combine(folder, "scene_splats.ply");
                splats = _splat.ExportPly(ply);
                if (splats > 0) files.Add(ply);
            }
            if (usdaBuilder is not null)
            {
                var usda = Path.Combine(folder, baseName + ".usda");
                await File.WriteAllTextAsync(usda, usdaBuilder(Path.GetFileName(v.VideoPath)), new UTF8Encoding(false), ct).ConfigureAwait(false);
                files.Add(usda);
            }
            var entries = new List<Dictionary<string, object>>(files.Count);
            foreach (var f in files)
            {
                await using var fs = File.OpenRead(f);
                var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                entries.Add(new() { ["name"] = Path.GetFileName(f), ["bytes"] = fs.Length, ["sha256"] = Convert.ToHexString(hash).ToLowerInvariant() });
            }
            var st = req.Stereo.Clamp();
            var manifest = new Dictionary<string, object>
            {
                ["generator"] = "PromptFlow Studio",
                ["version"] = "16.31.0",
                ["target"] = req.Target == SpatialTarget.VisionPro ? "visionOS" : "MetaQuest",
                ["format"] = v.Format,
                ["encoder"] = v.Encoder ?? "",
                ["frames"] = v.Frames,
                ["binaural"] = v.Binaural,
                ["partialRescue"] = v.PartialRescue,
                ["stereo"] = new Dictionary<string, object> { ["maxDisparityPct"] = st.MaxDisparityPct, ["convergence"] = st.Convergence, ["baselineMm"] = st.BaselineMm, ["hfovDeg"] = st.HfovDeg },
                ["splats"] = splats,
                ["hotspots"] = hotspots,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["files"] = entries,
            };
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct).ConfigureAwait(false);
            LastAction = "📦 공간 패키지 · " + v.Format + " · 파일 " + (files.Count + 1).ToString(CultureInfo.InvariantCulture);
            Raise();
            return new(true, folder, v, splats, hotspots, v.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, folder, v, 0, hotspots, "[부분 구제] 취소 — 비디오는 보존됨"); }
        catch (Exception ex)
        {
            Program.Log("Spatial.Package", ex);
            return new(false, folder, v, 0, hotspots, "[부분 구제] 부가 파일 실패(비디오 보존): " + ex.Message);
        }
    }

    private static SpatialTranscodeResult Fail(string msg) => new(false, null, "", 0, null, false, false, msg);

    private SpatialTranscodeResult Finish(SpatialTranscodeResult r)
    {
        LastResult = r;
        if (!r.Ok) LastAction = "⚠ 공간 변환 " + r.Message;
        Raise();
        return r;
    }

    private static string? DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Program.Log("Spatial.Cleanup", ex); }
        return null;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Spatial.OnChange", ex); }
        }
    }
}
