using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.Autopilot;

/// <summary>v17.73.0 하드서브 자막 큐(ms · 본문 = 엔진 char 풀 슬롯).</summary>
public struct BurnCue
{
    public float StartMs, EndMs;
    public int TextStart, TextLen;
}

/// <summary>v17.73.0 번-인 원본 프레임 공급자 — NV12(Y w×h · UV w×h/2) 버퍼에 프레임 i 를 기록(무할당).</summary>
public interface IBurnInFrameSource
{
    void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height);
}

/// <summary>
/// v17.73.0 MCR PGM(320×192 RGB24) → 9:16 NV12 업스케일 소스 — 중앙 9:16 크롭(108×192) · 쌍선형 표본화 · BT.709 제한 범위.
/// 스냅숏 버퍼(호출자 · 재사용)를 원본으로 사용 — 라이브 PGM 은 <see cref="Capture"/> 로 1 회 복사(경합 없는 정지 화면 · 무할당).
/// </summary>
public sealed class McrProgramFrameSource : IBurnInFrameSource
{
    public const int SrcW = BroadcastMcrMasterControlSynthesizer.W, SrcH = BroadcastMcrMasterControlSynthesizer.H, CropW = SrcH * 9 / 16;
    private readonly byte[] _rgb = new byte[SrcW * SrcH * 3];
    private int[]? _xTab; private float[]? _xW; private int _tabW, _tabH;
    private int[]? _yTab; private float[]? _yW;

    public long Captures { get; private set; }
    public ReadOnlySpan<byte> Snapshot => _rgb;

    /// <summary>PGM 프레임 스냅숏(320×192×3).</summary>
    public void Capture(ReadOnlySpan<byte> pgm) { pgm[..Math.Min(pgm.Length, _rgb.Length)].CopyTo(_rgb); Captures++; }

    private void EnsureTabs(int w, int h)
    {
        if (_tabW == w && _tabH == h) return;
        _xTab = new int[w]; _xW = new float[w]; _yTab = new int[h]; _yW = new float[h];
        var x0 = (SrcW - CropW) / 2;
        for (var x = 0; x < w; x++) { var fx = Math.Clamp((x + 0.5f) * CropW / w - 0.5f, 0f, CropW - 1.001f); _xTab[x] = x0 + (int)fx; _xW[x] = fx - (int)fx; }
        for (var y = 0; y < h; y++) { var fy = Math.Clamp((y + 0.5f) * SrcH / h - 0.5f, 0f, SrcH - 1.001f); _yTab[y] = (int)fy; _yW[y] = fy - (int)fy; }
        _tabW = w; _tabH = h;
    }

    public void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height)
    {
        EnsureTabs(width, height);
        var xt = _xTab!; var xw = _xW!; var yt = _yTab!; var yw = _yW!;
        for (var py = 0; py < height; py++)
        {
            var r0 = yt[py] * SrcW * 3; var r1 = r0 + SrcW * 3; var fy = yw[py];
            var orow = py * width;
            var chroma = (py & 1) == 0;
            for (var px = 0; px < width; px++)
            {
                var c = xt[px] * 3; var fx = xw[px];
                float R = Bi(_rgb, r0 + c, r1 + c, fx, fy), G = Bi(_rgb, r0 + c + 1, r1 + c + 1, fx, fy), B = Bi(_rgb, r0 + c + 2, r1 + c + 2, fx, fy);
                y[orow + px] = (byte)(16f + 0.1826f * R + 0.6142f * G + 0.062f * B);
                if (chroma && (px & 1) == 0)
                {
                    var o = (py >> 1) * width + px;
                    uv[o] = (byte)Math.Clamp(128f - 0.1006f * R - 0.3386f * G + 0.4392f * B, 16f, 240f);
                    uv[o + 1] = (byte)Math.Clamp(128f + 0.4392f * R - 0.3989f * G - 0.0403f * B, 16f, 240f);
                }
            }
        }
    }

    private static float Bi(byte[] s, int a, int b, float fx, float fy)
    {
        float t = s[a] + (s[a + 3] - s[a]) * fx, u = s[b] + (s[b + 3] - s[b]) * fx;
        return t + (u - t) * fy;
    }
}

/// <summary>v17.73.0 합성 그라데이션 소스(회귀 · 데모 · 결정적 움직임).</summary>
public sealed class GradientFrameSource : IBurnInFrameSource
{
    public void Render(long frame, int fps, Span<byte> y, Span<byte> uv, int width, int height)
    {
        var shift = (int)(frame * 3 % 256);
        for (var py = 0; py < height; py++) { var row = py * width; var v = (byte)(48 + (py * 96 / height)); for (var px = 0; px < width; px++) y[row + px] = (byte)(v + ((px + shift) & 31)); }
        uv[..(width * height / 2)].Fill(128);
    }
}

/// <summary>
/// [PromptFlow Studio v17.73.0] 🔥 GPU 가속 9:16 하드서브 자막 프레임 번-인 엔진(<see cref="IVideoFrameOverlay"/> · 무할당 · 외부 프로세스 0).
///   ▸ 결선: v17.49 트랜스코더 인코딩 직전 NV12 백버퍼(<see cref="VerticalTranscoderStudioRunner.Overlay"/> 체인 · 프레임 복사 0 = 제로카피 제자리 블렌딩) —
///     MF 하드웨어 인코더(GPU) 입력 버퍼에 직접 합성 · MF 부재 시 관리 Baseline 인코더 폴백(동일 경로).
///   ▸ 자막: v17.52 SDF 아틀라스 + 단어 단위 모션 타이포(Pop) · 3 플랫폼 데드존 교집합 세이프 존 · 큐 전환/단어 전환/팝 구간(180 ms)에만 RGBA 재래스터 —
///     그 외 프레임은 캐시 레이어의 더티 사각형만 NV12 합성(BT.709 제한 범위 프리멀티 알파).
///   ▸ 출력: <see cref="BurnReel"/> — 프레임 소스(MCR PGM 업스케일 · 그라데이션) → 번-인 → 트랜스코더 → fMP4(SegmentedChunkStream) · 오디오 무음 AAC/MF AAC.
/// </summary>
public sealed class HardwareSubtitleBurnInEngine : IVideoFrameOverlay
{
    public const int MaxCues = 32, TextPool = 4096, PopMs = 180;

    private readonly BurnCue[] _cues = new BurnCue[MaxCues];
    private readonly char[] _text = new char[TextPool];
    private RgbaOverlayLayer? _layer;
    private byte[]? _y, _uv;
    private float[] _l = new float[800], _r = new float[800];
    private int _cueN, _textLen, _active = -1, _lastWord = -2;
    private float _wordAt;

    public HardwareSubtitleBurnInEngine(int width = 1080, int height = 1920)
    {
        Atlas = new SdfGlyphAtlas();
        Typo = new RealtimeWordByWordMotionTypographyEngine(Atlas) { Preset = TypoPreset.Pop, FontPx = 84f };
        Resize(width, height);
    }

    public SdfGlyphAtlas Atlas { get; }
    public RealtimeWordByWordMotionTypographyEngine Typo { get; private set; }
    public CrossPlatformUiSafeZoneGuard Guard { get; private set; } = null!;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IVideoFrameOverlay? Next { get; set; }
    public bool Enabled { get; set; } = true;
    public float TimeOffsetMs { get; set; }
    public int CueCount => _cueN;
    public ref readonly BurnCue Cue(int i) => ref _cues[i];
    public ReadOnlySpan<char> CueText(int i) => _text.AsSpan(_cues[i].TextStart, _cues[i].TextLen);
    public int ActiveCue => _active;
    public long Frames { get; private set; }
    public long BurnedFrames { get; private set; }
    public long Rasters { get; private set; }
    public long Mismatched { get; private set; }
    public long Reels { get; private set; }
    public double LastApplyUs { get; private set; }
    public double MaxApplyUs { get; private set; }
    public double LastReelMs { get; private set; }
    public PxRect LastBox { get; private set; }
    public bool LastBoxSafe { get; private set; } = true;
    public string? LastBackend { get; private set; }

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width; Height = height;
        _layer = new RgbaOverlayLayer(width, height);
        Guard = new CrossPlatformUiSafeZoneGuard(width, height, CrossPlatformUiSafeZoneGuard.All);
        Typo.FontPx = Math.Clamp(84f * width / 1080f, 18f, 140f);
        _active = -1;
    }

    // ══════════════════════ 큐(비핫패스) ══════════════════════
    public void ClearCues() { _cueN = 0; _textLen = 0; _active = -1; }

    /// <summary>큐 추가 — 글리프는 즉시 아틀라스 준비(핫패스 미스 0).</summary>
    public bool AddCue(float startMs, float endMs, ReadOnlySpan<char> text)
    {
        if (_cueN >= MaxCues || text.IsEmpty || endMs <= startMs) return false;
        var n = Math.Min(text.Length, Math.Min(RealtimeWordByWordMotionTypographyEngine.TextCap, TextPool - _textLen));
        if (n <= 0) return false;
        text[..n].CopyTo(_text.AsSpan(_textLen));
        _cues[_cueN++] = new BurnCue { StartMs = startMs, EndMs = endMs, TextStart = _textLen, TextLen = n };
        Atlas.Ensure(_text.AsSpan(_textLen, n));
        _textLen += n;
        return true;
    }

    /// <summary>제목/설명 문장 → 길이 비례 큐 자동 분할(문장 부호 · 최대 18 자 단위 줄) — 전체 길이에 맞춰 배치.</summary>
    public int SetCaptionTrack(ReadOnlySpan<char> script, float durationMs, float startMs = 300f)
    {
        ClearCues();
        Span<int> cut = stackalloc int[MaxCues + 1];
        var parts = 0; var s = 0;
        cut[0] = 0;
        for (var i = 0; i < script.Length && parts < MaxCues; i++)
        {
            var ch = script[i];
            var brk = ch is '.' or '!' or '?' or '\n' or '·' || (i - s >= 18 && ch == ' ');
            if (!brk && i != script.Length - 1) continue;
            var e = i + 1;
            if (script[s..e].Trim().Length > 0) cut[++parts] = e; else cut[parts] = e;
            s = e;
        }
        if (parts == 0) return 0;
        var span = MathF.Max(500f, durationMs - startMs - 200f);
        var total = 0; for (var k = 1; k <= parts; k++) total += cut[k] - cut[k - 1];
        var t = startMs;
        for (var k = 1; k <= parts; k++)
        {
            var seg = script[cut[k - 1]..cut[k]].Trim();
            var d = span * (cut[k] - cut[k - 1]) / Math.Max(1, total);
            AddCue(t, t + MathF.Max(400f, d), seg);
            t += d;
        }
        return _cueN;
    }

    private int Find(float tMs)
    {
        if (_active >= 0 && _active < _cueN && tMs >= _cues[_active].StartMs && tMs < _cues[_active].EndMs) return _active;
        for (var i = 0; i < _cueN; i++) if (tMs >= _cues[i].StartMs && tMs < _cues[i].EndMs) return i;
        return -1;
    }

    // ══════════════════════ IVideoFrameOverlay(인코딩 스레드 · 무할당) ══════════════════════
    public void Apply(Span<byte> y, Span<byte> uv, int width, int height, long frameIndex, int fps)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        if (Enabled && _cueN > 0)
        {
            if (width != Width || height != Height) Mismatched++;
            else
            {
                var tMs = frameIndex * 1000f / Math.Max(1, fps) + TimeOffsetMs;
                var cue = Find(tMs);
                var layer = _layer!;
                if (cue != _active)
                {
                    _active = cue; _lastWord = -2;
                    layer.Begin();
                    if (cue >= 0)
                    {
                        ref readonly var c = ref _cues[cue];
                        Typo.SetCaption(_text.AsSpan(c.TextStart, c.TextLen), TypoLang.Ko, 0f, 40f);
                        Typo.Layout((Guard.Safe.W - 2 * RealtimeWordByWordMotionTypographyEngine.OutlineEm * Typo.FontPx) / Typo.FontPx);
                        // 단어 타임라인을 큐 길이에 맞춰 압축(전 단어가 큐 안에서 등장)
                        var scale = Typo.TotalMs > 0 ? MathF.Min(1f, (c.EndMs - c.StartMs) * 0.85f / Typo.TotalMs) : 1f;
                        for (var i = 0; i < Typo.WordCount; i++) { ref readonly var w = ref Typo.Word(i); Typo.SetTiming(i, w.StartMs * scale, w.EndMs * scale); }
                        // 마지막 단어는 큐 끝까지 유지
                        if (Typo.WordCount > 0) Typo.SetTiming(Typo.WordCount - 1, Typo.Word(Typo.WordCount - 1).StartMs, c.EndMs - c.StartMs);
                    }
                }
                if (cue >= 0)
                {
                    var local = tMs - _cues[cue].StartMs;
                    var before = Typo.CurrentWord;
                    // 단어 전환 · 팝 구간에만 재래스터
                    var word = WordAt(local);
                    if (word != _lastWord) { _lastWord = word; _wordAt = local; }
                    if (local - _wordAt <= PopMs || Rasters == 0 || before == -1)
                    {
                        layer.Begin();
                        Typo.Render(local, layer, Guard);
                        Rasters++;
                        var box = Typo.LastDrawnBounds;
                        if (!box.IsEmpty) { LastBox = box; LastBoxSafe = Guard.Audit(in box); }
                    }
                    layer.CompositeNv12(y, uv, width);
                    BurnedFrames++;
                }
            }
        }
        Next?.Apply(y, uv, width, height, frameIndex, fps);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastApplyUs = us; if (us > MaxApplyUs && Frames > 3) MaxApplyUs = us;
    }

    private int WordAt(float local)
    {
        var n = Typo.WordCount;
        for (var i = n - 1; i >= 0; i--) if (local >= Typo.Word(i).StartMs) return i;
        return -1;
    }

    // ══════════════════════ 번-인 릴 사출(백그라운드) ══════════════════════
    /// <summary>프레임 소스 → 번-인 → v17.49 트랜스코더(MF GPU → 관리 폴백) → fMP4 · 프레임 수 반환(실패 0).</summary>
    public int BurnReel(IBurnInFrameSource source, double durationSec, int fps, Stream output, int kbps = 6000, bool allowMf = true)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var frames = (int)Math.Round(durationSec * fps);
        var ySize = Width * Height;
        if (_y is null || _y.Length != ySize) { _y = new byte[ySize]; _uv = new byte[ySize / 2]; }
        var spf = 48000 / Math.Max(1, fps);
        if (_l.Length != spf) { _l = new float[spf]; _r = new float[spf]; }
        using var enc = new RealtimeHardwareVerticalTranscoderEngine();
        if (!enc.Open(output, Width, Height, fps, kbps, true, allowMf)) return 0;
        LastBackend = RealtimeHardwareVerticalTranscoderEngine.BackendName(enc.Backend);
        _active = -1; Rasters = 0;
        for (var f = 0; f < frames; f++)
        {
            source.Render(f, fps, _y, _uv!, Width, Height);
            Apply(_y, _uv!, Width, Height, f, fps);
            enc.EncodeFrame(_y, _uv!, _l, _r);
        }
        enc.Finish();
        Reels++;
        LastReelMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return frames;
    }
}
