using System.Numerics;
namespace PromptFlowStudio.Services.VerticalTranscode;

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ 스튜디오 엔진 직결 9:16 프레임 &amp; 오디오 합성기(<see cref="Render"/> 무할당 · 사전 렌더 파일 불필요).
/// <b>영상</b>: v17.46 세트 확장 합성(224×126 · 16:9) 과 v17.43 렌즈 광학 출력(240×100) 을 중앙 9:16 크롭 매트릭스로, v17.47 리믹스(72×128 · 9:16)는 전면으로
/// 1/4 해상도 중간 버퍼에 쌍선형 표본화 → 가중 합 · 렌즈 스크린 블렌드 → BT.709 제한 범위 Y/UV → 출력 해상도(기본 1080×1920)로 ×4 쌍선형 업스케일(수직 보간 SIMD) → NV12.
/// <b>음성</b>: v17.47 합성기의 v17.32 더빙 보이스 스템(중앙) + v17.38 모달 폴리 스템(0.25 Hz 오토 팬) 을 48 kHz 스테레오로 믹스 · 소프트 클립.
/// 소스 러너가 정지 중이면 합성기가 미디어 시간에 맞춰 직접 Step(60 Hz 기준 fps 당 60/fps 회) 해 결정적 출력을 만든다. 실행 중이면 최신 스냅샷을 쓴다.
/// </summary>
public sealed class StudioDirectPipelineFrameSynthesizer
{
    public const int SampleRate = 48000;
    private readonly ViralReels.ViralReelsStudioRunner? _reels;
    private readonly SetExtension.VirtualSetExtensionStudioRunner? _set;
    private readonly LensOptics.CinematicLensOpticsStudioRunner? _lens;
    private readonly float[] _ir, _ig, _ib, _iy, _iu, _iv, _row;
    private readonly int[] _sx0, _sy0, _ux0, _uy0;
    private readonly float[] _swx, _swy, _uwx, _uwy;
    private readonly Tab _tSet, _tLens, _tReel;
    private readonly byte[] _y, _uv;
    private readonly float[] _l, _r;
    private long _sample;

    private sealed class Tab
    {
        public readonly int[] X0, Y0; public readonly float[] Wx, Wy; public readonly int SrcW;
        public Tab(int iw, int ih, int srcW, int srcH, float cropX, float cropW)
        {
            SrcW = srcW; X0 = new int[iw]; Wx = new float[iw]; Y0 = new int[ih]; Wy = new float[ih];
            for (var x = 0; x < iw; x++) { var s = cropX + (x + 0.5f) * cropW / iw - 0.5f; s = Math.Clamp(s, 0f, srcW - 1.001f); X0[x] = (int)s; Wx[x] = s - X0[x]; }
            for (var y = 0; y < ih; y++) { var s = (y + 0.5f) * srcH / ih - 0.5f; s = Math.Clamp(s, 0f, srcH - 1.001f); Y0[y] = (int)s; Wy[y] = s - Y0[y]; }
        }
    }

    public StudioDirectPipelineFrameSynthesizer(int width, int height, int fps, ViralReels.ViralReelsStudioRunner? reels, SetExtension.VirtualSetExtensionStudioRunner? set, LensOptics.CinematicLensOpticsStudioRunner? lens)
    {
        if (width % 16 != 8 && width % 16 != 0 || height % 16 != 0 || width % 8 != 0) throw new ArgumentException("width % 8 == 0 · height % 16 == 0");
        if (fps != 30 && fps != 60) throw new ArgumentException("fps 30/60");
        Width = width; Height = height; Fps = fps; IW = width / 4; IH = height / 4;
        _reels = reels; _set = set; _lens = lens;
        int ip = IW * IH, cw = IW / 2, ch = IH / 2;
        _ir = new float[ip]; _ig = new float[ip]; _ib = new float[ip]; _iy = new float[ip]; _iu = new float[cw * ch]; _iv = new float[cw * ch];
        _row = new float[Math.Max(IW, cw) + Vector<float>.Count];
        Up(width, IW, out _sx0, out _swx); Up(height, IH, out _sy0, out _swy); Up(width / 2, cw, out _ux0, out _uwx); Up(height / 2, ch, out _uy0, out _uwy);
        const int sw = SetExtension.RealtimeVirtualSetExtensionEngine.W, sh = SetExtension.RealtimeVirtualSetExtensionEngine.H;
        const int lw = LensOptics.RealtimeAnamorphicLensOpticsEngine.W, lh = LensOptics.RealtimeAnamorphicLensOpticsEngine.H;
        _tSet = new Tab(IW, IH, sw, sh, (sw - sh * 9f / 16f) * 0.5f, sh * 9f / 16f);
        _tLens = new Tab(IW, IH, lw, lh, (lw - lh * 9f / 16f) * 0.5f, lh * 9f / 16f);
        _tReel = new Tab(IW, IH, ViralReels.ViralReelsStudioRunner.W, ViralReels.ViralReelsStudioRunner.H, 0f, ViralReels.ViralReelsStudioRunner.W);
        _y = new byte[width * height]; _uv = new byte[width * height / 2];
        SamplesPerFrame = SampleRate / fps;
        _l = new float[SamplesPerFrame]; _r = new float[SamplesPerFrame];
    }

    private static void Up(int dst, int src, out int[] x0, out float[] w)
    {
        x0 = new int[dst]; w = new float[dst];
        for (var x = 0; x < dst; x++) { var s = Math.Clamp((x + 0.5f) * src / dst - 0.5f, 0f, src - 1.001f); x0[x] = (int)s; w[x] = s - x0[x]; }
    }

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int IW { get; }
    public int IH { get; }
    public int SamplesPerFrame { get; }
    public float SetWeight { get; set; } = 0.45f;
    public float ReelWeight { get; set; } = 0.55f;
    public float LensWeight { get; set; } = 0.35f;
    public float VoiceGain { get; set; } = 1.4f;
    public float FoleyGain { get; set; } = 1.2f;
    public bool SetLinked => _set is not null;
    public bool LensLinked => _lens is not null;
    public bool ReelsLinked => _reels is not null;
    public ReadOnlySpan<byte> Y => _y;
    public ReadOnlySpan<byte> UV => _uv;

    /// <summary>v17.52 — 인코딩 직전 NV12 프레임버퍼 오버레이 합성(무할당).</summary>
    public void ApplyOverlay(IVideoFrameOverlay overlay, long frameIndex) => overlay.Apply(_y, _uv, Width, Height, frameIndex, Fps);
    public ReadOnlySpan<float> Left => _l;
    public ReadOnlySpan<float> Right => _r;
    public ReadOnlySpan<float> PreviewR => _ir;
    public ReadOnlySpan<float> PreviewG => _ig;
    public ReadOnlySpan<float> PreviewB => _ib;
    public double LastRenderMs { get; private set; }
    /// <summary>v17.53 비트 동기 오디오 믹스 스테이지(BGM · 사이드체인 더킹 · −14 LUFS · −1 dBTP) — 설정 시 스템 믹스 대신 사용.</summary>
    public IAudioMixStage? MixStage { get; set; }
    public float AudioPeak { get; private set; }

    /// <summary>프레임 1장 합성(무할당) — NV12 · 스테레오 PCM 갱신.</summary>
    public void Render()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var steps = 60 / Fps;
        var spf = SamplesPerFrame / steps;
        for (var s = 0; s < steps; s++)
        {
            if (_reels is not null && !_reels.IsRunning) _reels.Step();
            MixAudio(s * spf, spf);
        }
        if (_set is not null && !_set.IsRunning) _set.Step();
        if (_lens is not null && !_lens.IsRunning) _lens.Step();
        Composite();
        ToYuv();
        Upscale(_iy, IW, IH, _sx0, _swx, _sy0, _swy, _y, Width, Height, 1, 0);
        int cw = IW / 2, ch = IH / 2;
        Upscale(_iu, cw, ch, _ux0, _uwx, _uy0, _uwy, _uv, Width / 2, Height / 2, 2, 0);
        Upscale(_iv, cw, ch, _ux0, _uwx, _uy0, _uwy, _uv, Width / 2, Height / 2, 2, 1);
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void MixAudio(int at, int n)
    {
        var peak = at == 0 ? 0f : AudioPeak;
        var voice = _reels is null ? default : _reels.Synth.VoiceStem;
        var foley = _reels is null ? default : _reels.Synth.FoleyStem;
        if (MixStage is { } stage)
        {
            stage.Mix(voice, foley, _l.AsSpan(at, n), _r.AsSpan(at, n));
            for (var i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(_l[at + i]), MathF.Abs(_r[at + i])));
            _sample += n;
            AudioPeak = peak;
            return;
        }
        for (var i = 0; i < n; i++)
        {
            var v = i < voice.Length ? VoiceGain * voice[i] : 0f;
            var f = i < foley.Length ? FoleyGain * foley[i] : 0f;
            var pan = 0.5f + 0.4f * MathF.Sin(2f * MathF.PI * 0.25f * (_sample + i) / SampleRate);
            float l = v + f * (1f - pan), r = v + f * pan;
            l = l * (27f + l * l) / (27f + 9f * l * l); r = r * (27f + r * r) / (27f + 9f * r * r);
            _l[at + i] = Math.Clamp(l, -1f, 1f); _r[at + i] = Math.Clamp(r, -1f, 1f);
            peak = MathF.Max(peak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));
        }
        _sample += n;
        AudioPeak = peak;
    }

    private void Composite()
    {
        float ws = _set is null ? 0f : SetWeight, wr = _reels is null ? 0f : ReelWeight, wl = _lens is null ? 0f : LensWeight;
        var norm = ws + wr > 1e-6f ? 1f / (ws + wr) : 0f;
        ReadOnlySpan<float> sr = default, sg = default, sb = default, rr = default, rg = default, rb = default, lr = default, lg = default, lb = default;
        if (_set is not null) { sr = _set.Synth.OutR; sg = _set.Synth.OutG; sb = _set.Synth.OutB; }
        if (_reels is not null) { rr = _reels.Synth.OutR; rg = _reels.Synth.OutG; rb = _reels.Synth.OutB; }
        if (_lens is not null) { lr = _lens.OutR; lg = _lens.OutG; lb = _lens.OutB; }
        for (var y = 0; y < IH; y++)
            for (var x = 0; x < IW; x++)
            {
                var i = y * IW + x;
                float r = 0.05f, g = 0.05f, b = 0.07f;
                if (norm > 0f)
                {
                    r = g = b = 0f;
                    if (ws > 0f) { r += ws * Sample(sr, _tSet, x, y); g += ws * Sample(sg, _tSet, x, y); b += ws * Sample(sb, _tSet, x, y); }
                    if (wr > 0f) { r += wr * Sample(rr, _tReel, x, y); g += wr * Sample(rg, _tReel, x, y); b += wr * Sample(rb, _tReel, x, y); }
                    r *= norm; g *= norm; b *= norm;
                }
                if (wl > 0f)
                {
                    float a = wl * Math.Clamp(Sample(lr, _tLens, x, y), 0f, 1f), c = wl * Math.Clamp(Sample(lg, _tLens, x, y), 0f, 1f), d = wl * Math.Clamp(Sample(lb, _tLens, x, y), 0f, 1f);
                    r = 1f - (1f - Math.Clamp(r, 0f, 1f)) * (1f - a); g = 1f - (1f - Math.Clamp(g, 0f, 1f)) * (1f - c); b = 1f - (1f - Math.Clamp(b, 0f, 1f)) * (1f - d);
                }
                _ir[i] = r; _ig[i] = g; _ib[i] = b;
            }
    }

    private static float Sample(ReadOnlySpan<float> p, Tab t, int x, int y)
    {
        if (p.IsEmpty) return 0f;
        int x0 = t.X0[x], y0 = t.Y0[y], w = t.SrcW;
        float fx = t.Wx[x], fy = t.Wy[y];
        var o = y0 * w + x0;
        var top = p[o] + (p[o + 1] - p[o]) * fx;
        var bot = p[o + w] + (p[o + w + 1] - p[o + w]) * fx;
        return top + (bot - top) * fy;
    }

    private void ToYuv()
    {
        var n = _ir.Length;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var k = Vector<float>.Count;
            Vector<float> zero = Vector<float>.Zero, one = Vector<float>.One;
            for (; i <= n - k; i += k)
            {
                var r = Vector.Min(Vector.Max(new Vector<float>(_ir, i), zero), one);
                var g = Vector.Min(Vector.Max(new Vector<float>(_ig, i), zero), one);
                var b = Vector.Min(Vector.Max(new Vector<float>(_ib, i), zero), one);
                (new Vector<float>(16f) + 219f * (0.2126f * r + 0.7152f * g + 0.0722f * b)).CopyTo(_iy, i);
            }
        }
        for (; i < n; i++)
        {
            float r = Math.Clamp(_ir[i], 0f, 1f), g = Math.Clamp(_ig[i], 0f, 1f), b = Math.Clamp(_ib[i], 0f, 1f);
            _iy[i] = 16f + 219f * (0.2126f * r + 0.7152f * g + 0.0722f * b);
        }
        var cw = IW / 2;
        for (var y = 0; y < IH / 2; y++)
            for (var x = 0; x < cw; x++)
            {
                var a = 2 * y * IW + 2 * x;
                float r = 0.25f * (C(_ir[a]) + C(_ir[a + 1]) + C(_ir[a + IW]) + C(_ir[a + IW + 1]));
                float g = 0.25f * (C(_ig[a]) + C(_ig[a + 1]) + C(_ig[a + IW]) + C(_ig[a + IW + 1]));
                float b = 0.25f * (C(_ib[a]) + C(_ib[a + 1]) + C(_ib[a + IW]) + C(_ib[a + IW + 1]));
                _iu[y * cw + x] = 128f + 224f * (-0.1146f * r - 0.3854f * g + 0.5f * b);
                _iv[y * cw + x] = 128f + 224f * (0.5f * r - 0.4542f * g - 0.0458f * b);
            }
    }

    private static float C(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>쌍선형 업스케일 — 행마다 수직 보간(SIMD) 후 수평 표본화 · 바이트 기록(pitch · offset 으로 NV12 UV 인터리브).</summary>
    private void Upscale(float[] src, int sw, int sh, int[] x0, float[] wx, int[] y0, float[] wy, byte[] dst, int dw, int dh, int pitch, int offset)
    {
        var row = _row;
        var k = Vector<float>.Count;
        for (var y = 0; y < dh; y++)
        {
            var a = y0[y] * sw; var b = Math.Min(y0[y] + 1, sh - 1) * sw; var f = wy[y];
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var vf = new Vector<float>(f);
                for (; i <= sw - k; i += k)
                {
                    var va = new Vector<float>(src, a + i); var vb = new Vector<float>(src, b + i);
                    (va + (vb - va) * vf).CopyTo(row, i);
                }
            }
            for (; i < sw; i++) row[i] = src[a + i] + (src[b + i] - src[a + i]) * f;
            var d = y * dw * pitch + offset;
            for (var x = 0; x < dw; x++)
            {
                var s = x0[x];
                var v = row[s] + (row[Math.Min(s + 1, sw - 1)] - row[s]) * wx[x];
                dst[d + x * pitch] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }
        }
    }
}
