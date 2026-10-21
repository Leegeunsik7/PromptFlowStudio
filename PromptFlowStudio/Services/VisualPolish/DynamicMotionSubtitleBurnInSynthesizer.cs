using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.VisualPolish;

/// <summary>
/// v17.74.0 디코드 클립 프레임 소스(<see cref="IBurnInFrameSource"/>) — 출력 프레임 f → 원본 시각 매핑(프레임레이트 변환 = 반복/건너뜀) · 클립 종료 후 마지막 프레임 유지.
/// 원본 = 출력 크기면 디코더가 출력 버퍼에 직접 기록(복사 0) · 다르면 NV12 쌍선형 리샘플(사전 할당 · 무할당).
/// </summary>
public sealed class DecodedClipFrameSource : IBurnInFrameSource, IDisposable
{
    private readonly HardwareVideoDemuxFrameDecoder _dec;
    private byte[] _y = Array.Empty<byte>(), _uv = Array.Empty<byte>();
    private int _have = -1;
    private bool _ended;

    public DecodedClipFrameSource(HardwareVideoDemuxFrameDecoder decoder) { _dec = decoder; }

    public HardwareVideoDemuxFrameDecoder Decoder => _dec;
    public long Decoded { get; private set; }
    public long Repeated { get; private set; }
    public long Skipped { get; private set; }
    public long Resampled { get; private set; }
    public long Direct { get; private set; }
    public double LastRenderUs { get; private set; }

    /// <summary>클립 열기 시도(디먹스 + 디코더) — 실패 = null(호출자 = 정지 화면 폴백).</summary>
    public static DecodedClipFrameSource? TryOpen(SegmentedChunkStream media, bool preferHardware = true)
    {
        var d = new HardwareVideoDemuxFrameDecoder { PreferHardware = preferHardware };
        if (!d.Open(media)) { d.Dispose(); return null; }
        return new DecodedClipFrameSource(d);
    }

    public void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int sw = _dec.Width, sh = _dec.Height;
        var srcFps = _dec.Fps > 0 ? _dec.Fps : fps;
        var want = (int)Math.Min(_dec.FrameCount - 1, Math.Floor(frame * srcFps / Math.Max(1, fps) + 1e-6));
        // 제로카피 경로: 동일 크기 · 원본 fps ≥ 출력(반복 없음) · 순차 1 프레임 전진 · 마지막 프레임 제외(종료 후 유지분은 중간 버퍼)
        //   → 디코더가 출력 백버퍼에 직접 복원(중간 버퍼 · 복사 0)
        if (sw == width && sh == height && want == _have + 1 && want < _dec.FrameCount - 1 && srcFps >= fps - 0.01 && !_ended)
        {
            if (_dec.NextFrame(y, uv)) { _have++; Decoded++; Direct++; LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency; return; }
            _ended = true;
        }
        if (_y.Length != sw * sh) { _y = new byte[sw * sh]; _uv = new byte[sw * sh / 2]; }   // 클립 크기 1 회
        if (want <= _have) Repeated++;
        while (_have < want && !_ended)
        {
            if (!_dec.NextFrame(_y, _uv)) { _ended = true; break; }
            _have++; Decoded++;
            if (_have < want) Skipped++;
        }
        if (_have < 0) { y[..(width * height)].Fill(16); uv[..(width * height / 2)].Fill(128); return; }
        if (sw == width && sh == height) { _y.AsSpan(0, sw * sh).CopyTo(y); _uv.AsSpan(0, sw * sh / 2).CopyTo(uv); }
        else { Resample(_y, _uv, sw, sh, y, uv, width, height); Resampled++; }
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>NV12 비율 유지 커버 리샘플(중앙 크롭 · 쌍선형 루마 · 최근접 크로마 · 무할당).</summary>
    public static void Resample(ReadOnlySpan<byte> sy, ReadOnlySpan<byte> suv, int sw, int sh, Span<byte> dy, Span<byte> duv, int dw, int dh)
    {
        var scale = MathF.Max((float)dw / sw, (float)dh / sh);
        float ox = (sw - dw / scale) * 0.5f, oy = (sh - dh / scale) * 0.5f, inv = 1f / scale;
        for (var j = 0; j < dh; j++)
        {
            var fy = Math.Clamp(oy + (j + 0.5f) * inv - 0.5f, 0f, sh - 1.001f); var y0 = (int)fy; var ay = fy - y0;
            var r0 = y0 * sw; var r1 = Math.Min(y0 + 1, sh - 1) * sw; var o = j * dw;
            for (var i = 0; i < dw; i++)
            {
                var fx = Math.Clamp(ox + (i + 0.5f) * inv - 0.5f, 0f, sw - 1.001f); var x0 = (int)fx; var ax = fx - x0; var x1 = Math.Min(x0 + 1, sw - 1);
                float t = sy[r0 + x0] + (sy[r0 + x1] - sy[r0 + x0]) * ax, b = sy[r1 + x0] + (sy[r1 + x1] - sy[r1 + x0]) * ax;
                dy[o + i] = (byte)(t + (b - t) * ay + 0.5f);
            }
        }
        for (var j = 0; j < dh / 2; j++)
        {
            var sj = Math.Clamp((int)(oy * 0.5f + (j + 0.5f) * inv), 0, sh / 2 - 1);
            for (var i = 0; i < dw / 2; i++)
            {
                var si = Math.Clamp((int)(ox * 0.5f + (i + 0.5f) * inv), 0, sw / 2 - 1);
                duv[j * dw + 2 * i] = suv[sj * sw + 2 * si]; duv[j * dw + 2 * i + 1] = suv[sj * sw + 2 * si + 1];
            }
        }
    }

    public void Dispose() => _dec.Dispose();
}

/// <summary>v17.74.0 번-인 결과(프레임 · 백엔드 · 프레임 시간 분포).</summary>
public readonly record struct MotionBurnResult(bool Ok, int Frames, int Width, int Height, int Fps, VideoDecoderBackend Decoder, string Encoder, double MedianFrameMs, double P95FrameMs, double MaxFrameMs, long OverBudget, string? Error);

/// <summary>
/// [PromptFlow Studio v17.74.0] 🎬 동적 비디오 프레임 하드서브 융합 합성기.
///   ▸ 원본 클립(v17.62 무손실 fMP4 · v17.69 패키지 · v17.73 번-인 릴) → <see cref="HardwareVideoDemuxFrameDecoder"/> 동영상 프레임 →
///     v17.73 <see cref="HardwareSubtitleBurnInEngine"/>(v17.52 SDF 단어 모션 자막 · 세이프 존) 프레임 단위 제자리 블렌딩 → v17.49 트랜스코더(MF GPU → 관리 폴백) → fMP4.
///   ▸ 프레임 예산: 프레임당(디코드 + 번-인 + 인코드) 벽시계 측정 → 중앙/p95/최대 · 16.7 ms 초과 수 — 핫패스(디코드 · 리샘플 · 블렌딩) 무할당.
/// </summary>
public sealed class DynamicMotionSubtitleBurnInSynthesizer : IBurnInFrameSource
{
    public const double BudgetMs = 1000.0 / 60;

    private readonly double[] _frameMs = new double[4096];
    private DecodedClipFrameSource? _src;
    private long _t0;
    private int _fn;

    public DynamicMotionSubtitleBurnInSynthesizer(HardwareSubtitleBurnInEngine? burnIn = null) => BurnIn = burnIn ?? new HardwareSubtitleBurnInEngine();

    public HardwareSubtitleBurnInEngine BurnIn { get; }
    public bool PreferHardwareDecode { get; set; } = true;
    public bool AllowMediaFoundationEncode { get; set; } = true;
    public long Runs { get; private set; }
    public long Fallbacks { get; private set; }
    public MotionBurnResult Last { get; private set; }

    /// <summary>프레임 시간 계측 래퍼 — 직전 프레임(번-인 + 인코드 포함) 경계에서 분할.</summary>
    public void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (frame > 0 && _fn < _frameMs.Length) _frameMs[_fn++] = (now - _t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _t0 = now;
        _src!.Render(frame, fps, y, uv, width, height);
    }

    /// <summary>클립 → 동적 자막 번-인 MP4. 디코드 불가 = 실패 결과(호출자 정지 화면 폴백).</summary>
    public MotionBurnResult Burn(SegmentedChunkStream clip, ReadOnlySpan<char> script, Stream output, int width = 0, int height = 0, double maxSec = 30)
    {
        Runs++;
        using var src = DecodedClipFrameSource.TryOpen(clip, PreferHardwareDecode);
        if (src is null) { Fallbacks++; return Last = new MotionBurnResult(false, 0, 0, 0, 0, VideoDecoderBackend.None, "", 0, 0, 0, 0, "클립 디코드 불가"); }
        var dec = src.Decoder;
        int w = width > 0 ? width : dec.Width & ~1, h = height > 0 ? height : dec.Height & ~1;
        var fps = (int)Math.Clamp(Math.Round(dec.Fps), 1, 60);
        var dur = Math.Min(maxSec, dec.FrameCount / Math.Max(1.0, dec.Fps));
        BurnIn.Resize(w, h);
        BurnIn.SetCaptionTrack(script, (float)(dur * 1000));
        _src = src; _fn = 0; _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var frames = BurnIn.BurnReel(this, dur, fps, output, 6000, AllowMediaFoundationEncode);
        if (_fn < _frameMs.Length && frames > 0) _frameMs[_fn++] = (System.Diagnostics.Stopwatch.GetTimestamp() - _t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _src = null;
        if (frames == 0) return Last = new MotionBurnResult(false, 0, w, h, fps, dec.Backend, "", 0, 0, 0, 0, "트랜스코더 열기 실패");
        var n = Math.Min(_fn, _frameMs.Length);
        Array.Sort(_frameMs, 0, n);
        long over = 0; for (var i = 0; i < n; i++) if (_frameMs[i] > BudgetMs) over++;
        return Last = new MotionBurnResult(true, frames, w, h, fps, dec.Backend, BurnIn.LastBackend ?? "", n > 0 ? _frameMs[n / 2] : 0, n > 0 ? _frameMs[(int)(n * 0.95)] : 0, n > 0 ? _frameMs[n - 1] : 0, over, null);
    }
}
