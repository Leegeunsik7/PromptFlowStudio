using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.BroadcastMcr;

public enum McrTransition : byte { Cut, Mix, Wipe }

/// <summary>v17.68 PGM 그래픽 오버레이(CTA 배너 · 카운트다운) — 송출 RGB24 버스에 NV12 변환 직전 합성(무할당).</summary>
public interface IMcrProgramOverlay
{
    void Apply(Span<byte> rgb, int width, int height, long frame);
}

/// <summary>
/// [PromptFlow Studio v17.66.0] 방송 주조정실(MCR) PGM/PVW 이중 버스 &amp; 트랜지션 합성기(60 fps · 무할당 핫패스).
///   ▸ 소스 5: v17.65/v17.39 4 캠(마스터 투샷 · OTS A · OTS B · 액션 바스트/리액션) + PPL 제품 인서트 캠 — 통합 3DGS 컴포지터가 버스별 RGB24 렌더.
///   ▸ 버스: PGM 매 프레임 렌더(60 fps) · PVW 모니터는 대기 시 3 프레임마다(20 fps · 트랜지션 중 매 프레임) · Take = Cut(즉시) / Mix(정수 디졸브 (a·(256−w)+b·w)≫8) / Wipe(좌 → 우 8 px 소프트 에지) → 완료 시 PGM ⇄ PVW 플립플롭.
///   ▸ 송출: 프레임마다 정확히 800 샘플(48 kHz / 60) 오디오를 소비(부족 = 무음 패딩 · 초과 = 오래된 샘플 폐기) · 마스터 페이더 · VU(피크 감쇠 · RMS dBFS) →
///     PGM BT.601 NV12 변환 → v17.49/v17.62 인코더(탭 = 게이트웨이 압축 링) 동일 호출로 영상+음성 제출 → A/V 누적 오차 0 프레임 계약.
/// </summary>
public sealed class BroadcastMcrMasterControlSynthesizer : IDisposable
{
    public const int Sources = 5, W = UnifiedGaussianSceneCompositorEngine.W, H = UnifiedGaussianSceneCompositorEngine.H, FrameBytes = UnifiedGaussianSceneCompositorEngine.FrameBytes;
    public const int Fps = 60, SampleRate = 48_000, TickSamples = SampleRate / Fps, AudioCap = TickSamples * 8, PvwDivider = 3, WipeSoftPx = 8;

    private readonly SplatCamera[] _cam = new SplatCamera[Sources];
    private byte[] _pgmBuf = new byte[FrameBytes], _pvwBuf = new byte[FrameBytes];
    private readonly byte[] _out = new byte[FrameBytes];
    private readonly byte[] _y = new byte[W * H], _uv = new byte[W * H / 2];
    private readonly float[] _fifo = new float[AudioCap], _l = new float[TickSamples], _r = new float[TickSamples];
    private int _fifoN, _pgm, _pvw = 1, _transFrames, _transPos;
    private McrTransition _trans;
    private bool _transitioning;
    private long _frames;
    private RealtimeHardwareVerticalTranscoderEngine? _enc;

    public BroadcastMcrMasterControlSynthesizer() { }

    public int Program => _pgm;
    public int Preview => _pvw;
    public bool Transitioning => _transitioning;
    public McrTransition ActiveTransition => _trans;
    public float Progress => _transitioning ? _transPos / (float)Math.Max(1, _transFrames) : 0f;
    public ReadOnlySpan<byte> ProgramOut => _out;
    public ReadOnlySpan<byte> PreviewOut => _pvwBuf;
    public ReadOnlySpan<byte> NV12Y => _y;
    public ReadOnlySpan<byte> NV12UV => _uv;
    public ref readonly SplatCamera Camera(int s) => ref _cam[s];
    public float MasterGain { get; set; } = 1f;
    public IMcrProgramOverlay? Overlay { get; set; }
    public bool Mute { get; set; }
    public float PeakL { get; private set; }
    public float RmsL { get; private set; }
    public float PeakDb => Db(PeakL);
    public float RmsDb => Db(RmsL);
    public long VideoFrames => _frames;
    public long AudioSamples { get; private set; }
    public long Underruns { get; private set; }
    public long Overruns { get; private set; }
    public long Takes { get; private set; }
    public long Mixes { get; private set; }
    public long Wipes { get; private set; }
    public long CutsTaken { get; private set; }
    public long PgmRenders { get; private set; }
    public long PvwRenders { get; private set; }
    public long EncodedFrames => _enc?.Frames ?? 0;
    public bool EncoderOpen => _enc?.IsOpen == true;
    public string EncoderName => _enc is { IsOpen: true } e ? RealtimeHardwareVerticalTranscoderEngine.BackendName(e.Backend) : "—";
    /// <summary>A/V 누적 오차(프레임) = 송출 오디오 샘플 / 800 − 송출 영상 프레임.</summary>
    public long AvSyncFrames => AudioSamples / TickSamples - _frames;
    public long MaxAbsAvSync { get; private set; }
    public double LastFrameMs { get; private set; }
    public double MaxFrameMs { get; private set; }

    public static string SourceName(int s) => s switch { 0 => "CAM 1 · 마스터", 1 => "CAM 2 · OTS-A", 2 => "CAM 3 · OTS-B", 3 => "CAM 4 · 바스트", _ => "CAM 5 · PPL 인서트" };
    public static string TransitionName(McrTransition t) => t switch { McrTransition.Mix => "MIX", McrTransition.Wipe => "WIPE", _ => "CUT" };
    private static float Db(float a) => a > 1e-6f ? 20f * MathF.Log10(a) : -120f;

    public void SetSource(int s, in SplatCamera cam) { if ((uint)s < Sources) _cam[s] = cam; }

    public void ResetMetrics() { Takes = Mixes = Wipes = CutsTaken = Underruns = Overruns = 0; MaxAbsAvSync = 0; MaxFrameMs = 0; }

    // ══════════════════════ 버스 제어(무할당) ══════════════════════
    public void SetPreview(int s) { if ((uint)s < Sources && !_transitioning && s != _pvw) { _pvw = s; _pvwStale = true; } }

    /// <summary>PVW → PGM 전환(Cut 즉시 · Mix/Wipe = <paramref name="frames"/> 프레임 보간).</summary>
    public bool Take(McrTransition t, int frames = 12)
    {
        if (_transitioning) return false;
        Takes++;
        if (t == McrTransition.Cut || frames <= 1) { CutsTaken++; (_pgm, _pvw) = (_pvw, _pgm); (_pgmBuf, _pvwBuf) = (_pvwBuf, _pgmBuf); _pvwStale = true; return true; }
        if (t == McrTransition.Mix) Mixes++; else Wipes++;
        _trans = t; _transFrames = Math.Clamp(frames, 2, 120); _transPos = 0; _transitioning = true;
        return true;
    }

    private bool _pvwStale = true;

    /// <summary>진행 중 Mix/Wipe 즉시 종료(긴급 컷 선행 · 버스 유지).</summary>
    public void AbortTransition() { if (_transitioning) { _transitioning = false; _pvwStale = true; } }

    // ══════════════════════ 오디오(무할당) ══════════════════════
    /// <summary>송출 오디오 적재(모노 48 kHz) — 프레임당 800 샘플 소비 · 포화 시 오래된 샘플 폐기.</summary>
    public void PushAudio(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length >= AudioCap) { Overruns++; pcm = pcm[^AudioCap..]; _fifoN = 0; }
        if (_fifoN + pcm.Length > AudioCap)
        {
            var drop = _fifoN + pcm.Length - AudioCap;
            _fifo.AsSpan(drop, _fifoN - drop).CopyTo(_fifo);
            _fifoN -= drop; Overruns++;
        }
        pcm.CopyTo(_fifo.AsSpan(_fifoN));
        _fifoN += pcm.Length;
    }

    private void PullAudio()
    {
        var n = Math.Min(_fifoN, TickSamples);
        if (n < TickSamples) Underruns++;
        var g = Mute ? 0f : MasterGain;
        float pk = 0f, e = 0f;
        for (var i = 0; i < TickSamples; i++)
        {
            var x = i < n ? _fifo[i] * g : 0f;
            x = x > 1f ? 1f : x < -1f ? -1f : x;
            _l[i] = x; _r[i] = x;
            var a = MathF.Abs(x); if (a > pk) pk = a; e += x * x;
        }
        if (n > 0) { _fifo.AsSpan(n, _fifoN - n).CopyTo(_fifo); _fifoN -= n; }
        PeakL = MathF.Max(pk, PeakL * 0.9f);
        RmsL = MathF.Sqrt(e / TickSamples);
        AudioSamples += TickSamples;
    }

    // ══════════════════════ 송출 인코더(비핫패스 개폐) ══════════════════════
    public bool OpenEncoder(Stream output, IEncodedStreamTap? tap, bool allowMf, int kbps = 4000)
    {
        CloseEncoder();
        var e = new RealtimeHardwareVerticalTranscoderEngine { Tap = tap };
        if (!e.Open(output, W, H, Fps, kbps, true, allowMf)) { e.Dispose(); return false; }
        _enc = e;
        return true;
    }

    public void CloseEncoder()
    {
        var e = _enc; _enc = null;
        if (e is null) return;
        e.Tap = null; e.Close(); e.Dispose();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <summary>버스 렌더 · 트랜지션 합성 · 오디오 800 샘플 · NV12 · 인코더 제출(동일 프레임).</summary>
    public void Frame(UnifiedGaussianSceneCompositorEngine comp)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        comp.Render(in _cam[_pgm], _pgmBuf); PgmRenders++;
        if (_transitioning || _pvwStale || _frames % PvwDivider == 0) { comp.Render(in _cam[_pvw], _pvwBuf); PvwRenders++; _pvwStale = false; }
        if (_transitioning)
        {
            _transPos++;
            var p = _transPos / (float)_transFrames;
            if (_trans == McrTransition.Mix) Mix(_pgmBuf, _pvwBuf, _out, (int)(p * 256f));
            else Wipe(_pgmBuf, _pvwBuf, _out, p);
            if (_transPos >= _transFrames)
            {
                _transitioning = false;
                (_pgm, _pvw) = (_pvw, _pgm); (_pgmBuf, _pvwBuf) = (_pvwBuf, _pgmBuf);
                _pvwStale = true;
            }
        }
        else _pgmBuf.AsSpan().CopyTo(_out);
        Overlay?.Apply(_out, W, H, _frames);
        PullAudio();
        ToNv12(_out, _y, _uv);
        if (_enc is { IsOpen: true } enc) enc.EncodeFrame(_y, _uv, _l, _r);
        _frames++;
        var s = Math.Abs(AvSyncFrames); if (s > MaxAbsAvSync) MaxAbsAvSync = s;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastFrameMs = ms; if (ms > MaxFrameMs && _frames > 3) MaxFrameMs = ms;
    }

    public static void Mix(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int w256)
    {
        w256 = Math.Clamp(w256, 0, 256);
        var iw = 256 - w256;
        for (var i = 0; i < dst.Length; i++) dst[i] = (byte)((a[i] * iw + b[i] * w256) >> 8);
    }

    public static void Wipe(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, float p)
    {
        var edge = p * (W + 2 * WipeSoftPx) - WipeSoftPx;
        for (var x = 0; x < W; x++)
        {
            var w = (int)(Math.Clamp((edge - x) / WipeSoftPx * 0.5f + 0.5f, 0f, 1f) * 256f);
            var iw = 256 - w;
            for (var y = 0; y < H; y++)
            {
                var o = (y * W + x) * 3;
                dst[o] = (byte)((a[o] * iw + b[o] * w) >> 8);
                dst[o + 1] = (byte)((a[o + 1] * iw + b[o + 1] * w) >> 8);
                dst[o + 2] = (byte)((a[o + 2] * iw + b[o + 2] * w) >> 8);
            }
        }
    }

    /// <summary>RGB24 → NV12(BT.601 제한 범위 · 2×2 크로마 평균).</summary>
    public static void ToNv12(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> uv)
    {
        for (var p = 0; p < W * H; p++)
        {
            int r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            y[p] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }
        for (var cy = 0; cy < H / 2; cy++)
            for (var cx = 0; cx < W / 2; cx++)
            {
                int r = 0, g = 0, b = 0;
                for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++) { var o = ((cy * 2 + dy) * W + cx * 2 + dx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; }
                r >>= 2; g >>= 2; b >>= 2;
                var o2 = cy * W + cx * 2;
                uv[o2] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uv[o2 + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
    }

    /// <summary>버스 프레임 → 24 비트 BMP(하향 · UI 미리보기 · 비핫패스).</summary>
    public static void WriteBmp(ReadOnlySpan<byte> rgb, byte[] dst)
    {
        const int Row = W * 3;
        if (dst.Length < 54 + Row * H) return;
        dst.AsSpan(0, 54).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(2), 54 + Row * H);
        dst[10] = 54; dst[14] = 40;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(18), W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(22), H);
        dst[26] = 1; dst[28] = 24;
        for (var y = 0; y < H; y++)
        {
            var o = 54 + (H - 1 - y) * Row; var s = y * Row;
            for (var x = 0; x < W; x++) { dst[o + x * 3] = rgb[s + x * 3 + 2]; dst[o + x * 3 + 1] = rgb[s + x * 3 + 1]; dst[o + x * 3 + 2] = rgb[s + x * 3]; }
        }
    }

    public void Dispose() => CloseEncoder();
}
