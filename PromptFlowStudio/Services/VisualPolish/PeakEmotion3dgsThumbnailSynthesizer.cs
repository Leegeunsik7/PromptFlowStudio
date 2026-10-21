using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.VisualPolish;

/// <summary>v17.74.0 골든 프레임 장면 스냅숏(카메라 · 아바타 포즈 · 52 블렌드쉐이프 · 소품) — 버킷 슬롯(사전 할당).</summary>
public sealed class GoldenFrameSnapshot
{
    public const int MaxAvatars = 3, K = 52;
    public readonly float[] Face = new float[MaxAvatars * K];
    public readonly Vector3[] Seat = new Vector3[MaxAvatars];
    public readonly float[] BodyYaw = new float[MaxAvatars], HeadYaw = new float[MaxAvatars], HeadPitch = new float[MaxAvatars];
    public SplatCamera Camera;
    public Vector3 Prop;
    public bool PropVisible;
    public int Avatars;
    public long AtMs;
    public float Score, FaceScore, Boost;
    public int PeakAvatar;

    public void CopyFrom(GoldenFrameSnapshot o)
    {
        o.Face.AsSpan().CopyTo(Face); o.Seat.AsSpan().CopyTo(Seat); o.BodyYaw.AsSpan().CopyTo(BodyYaw); o.HeadYaw.AsSpan().CopyTo(HeadYaw); o.HeadPitch.AsSpan().CopyTo(HeadPitch);
        Camera = o.Camera; Prop = o.Prop; PropVisible = o.PropVisible; Avatars = o.Avatars; AtMs = o.AtMs; Score = o.Score; FaceScore = o.FaceScore; Boost = o.Boost; PeakAvatar = o.PeakAvatar;
    }
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 🎯 감정 피크 골든 프레임 추적기(v17.66 MCR 장면 레이어 · 60 Hz 무할당).
///   ▸ 점수 = max_아바타[ (MouthSmileL+R)/2 + JawOpen + (EyeWideL+R)/2 ](v17.58 안면 52) + 부스트(v17.67 피격 · v17.72 열광 · 외부 주입 · 0.5 s 감쇠).
///   ▸ 1 s 버킷 링(64 = 64 s 이력) — 버킷별 최고점 프레임 장면 스냅숏 보관 → <see cref="TryGolden"/>(릴 15~30 s 창 [start, end] 내 최고 버킷).
/// </summary>
public sealed class PeakEmotionFrameTracker : IMcrSceneLayer
{
    public const int Buckets = 64, BucketMs = 1000;
    private readonly GoldenFrameSnapshot[] _b = new GoldenFrameSnapshot[Buckets];
    private readonly long[] _bucketId = new long[Buckets];
    private readonly GoldenFrameSnapshot _cur = new();
    private readonly BroadcastMcrStudioRunner? _mcr;
    private float _boost;
    private long _lastHits = -1;

    public PeakEmotionFrameTracker(BroadcastMcrStudioRunner? mcr = null)
    {
        _mcr = mcr;
        for (var i = 0; i < Buckets; i++) { _b[i] = new GoldenFrameSnapshot(); _bucketId[i] = -1; }
    }

    public Func<long>? HitCounter { get; set; }
    public Func<float>? HypeProbe { get; set; }
    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    public long Frames { get; private set; }
    public float LastScore { get; private set; }
    public float LastFaceScore { get; private set; }
    public float BestScore { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }

    /// <summary>표정 블렌드쉐이프 피크 점수(무할당).</summary>
    public static float FaceScore(ReadOnlySpan<float> w52) =>
        0.5f * (w52[(int)ArkitBlendshape.MouthSmileLeft] + w52[(int)ArkitBlendshape.MouthSmileRight]) + w52[(int)ArkitBlendshape.JawOpen]
        + 0.5f * (w52[(int)ArkitBlendshape.EyeWideLeft] + w52[(int)ArkitBlendshape.EyeWideRight]);

    /// <summary>환호/피격 부스트 주입(임의 스레드 근사 · 0.5 s 감쇠).</summary>
    public void Boost(float v) => _boost = MathF.Max(_boost, v);

    // ══════════════════════ IMcrSceneLayer(MCR 틱 · 무할당) ══════════════════════
    public void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = _cur;
        var talk = _mcr?.Talkshow;
        var n = Math.Min(comp.Avatars, GoldenFrameSnapshot.MaxAvatars);
        s.Avatars = n;
        for (var a = 0; a < n; a++)
        {
            comp.Face(a).Weights.CopyTo(s.Face.AsSpan(a * GoldenFrameSnapshot.K, GoldenFrameSnapshot.K));
            if (talk is not null && a < talk.Active)
            {
                ref readonly var p = ref talk.Gaze.Pose(a);
                s.Seat[a] = talk.Seat(a); s.BodyYaw[a] = talk.Gaze.BodyYaw(a); s.HeadYaw[a] = p.HeadYaw; s.HeadPitch[a] = p.HeadPitch + p.Nod;
            }
            else { s.Seat[a] = comp.Root(a); s.BodyYaw[a] = s.HeadYaw[a] = s.HeadPitch[a] = 0f; }
        }
        if (_mcr is not null) { s.Prop = _mcr.Ppl.Position; s.PropVisible = true; }
        if (HitCounter is { } hc)
        {
            var h = hc();
            if (_lastHits >= 0 && h > _lastHits) Boost(MathF.Min(1.5f, 0.5f * (h - _lastHits)));
            _lastHits = h;
        }
        var hype = HypeProbe?.Invoke() ?? 0f;
        Observe(Clock(), in pgm, hype * 0.6f);
        _boost = MathF.Max(0f, _boost - dt * 2f);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastStepUs = us; if (us > MaxStepUs && Frames > 3) MaxStepUs = us;
    }

    /// <summary>현재 스냅숏(<see cref="Current"/> 에 채워진 장면) 점수화 → 버킷 최고점 갱신(무할당).</summary>
    public void Observe(long nowMs, in SplatCamera cam, float extraBoost = 0f)
    {
        Frames++;
        var s = _cur;
        s.Camera = cam; s.AtMs = nowMs;
        float best = 0f; var who = 0;
        for (var a = 0; a < s.Avatars; a++) { var f = FaceScore(s.Face.AsSpan(a * GoldenFrameSnapshot.K, GoldenFrameSnapshot.K)); if (f > best) { best = f; who = a; } }
        s.FaceScore = best; s.Boost = _boost + extraBoost; s.PeakAvatar = who;
        s.Score = best + s.Boost;
        LastScore = s.Score; LastFaceScore = best;
        var id = nowMs / BucketMs; var k = (int)(id % Buckets);
        if (_bucketId[k] != id) { _bucketId[k] = id; _b[k].Score = float.MinValue; }
        if (s.Score > _b[k].Score) { _b[k].CopyFrom(s); if (s.Score > BestScore) BestScore = s.Score; }
    }

    /// <summary>테스트/외부 주입용 현재 스냅숏(Observe 직전에 채움).</summary>
    public GoldenFrameSnapshot Current => _cur;

    /// <summary>[startMs, endMs] 창 최고 버킷 스냅숏 → dst 복사(false = 창 내 기록 없음).</summary>
    public bool TryGolden(long startMs, long endMs, GoldenFrameSnapshot dst)
    {
        var best = -1; var bs = float.MinValue;
        for (var i = 0; i < Buckets; i++)
        {
            if (_bucketId[i] < 0) continue;
            var t = _b[i].AtMs;
            if (t < startMs || t > endMs || _b[i].Score <= bs) continue;
            bs = _b[i].Score; best = i;
        }
        if (best < 0) return false;
        dst.CopyFrom(_b[best]);
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 🖼️ 3DGS 감정 피크 9:16 바이럴 썸네일 합성기.
///   ▸ 골든 프레임 스냅숏 → 전용 통합 3DGS 컴포지터(v17.66 동일 밀도 · 백그라운드 · 방송 스레드 비간섭)에 포즈/표정/소품 재현 →
///     1080×1920 = 320×192 오프축 타일 4×10(주점 이동 · 동일 초점거리 = 이음매 무단차) 고해상도 렌더.
///   ▸ 피사체 경계 글로우: 루마 소벨 엣지 × 중앙 가중(피사체 영역) → 분리형 박스 블러 2 회 → 네온(시안 → 마젠타 수직 그라데이션) 가산.
///   ▸ 볼드 후킹 헤드라인: v17.52 SDF 아틀라스 · 8 방향 외곽선 + 하이라이트 노랑 · 상단 세이프 존 · 하단 그라데이션 비네트.
///   ▸ 출력: RGB24 버퍼 · PNG(zlib · CRC32 · BCL) · 미리보기 BMP(축소).
/// </summary>
public sealed class PeakEmotion3dgsThumbnailSynthesizer : IDisposable
{
    public const int OutW = 1080, OutH = 1920, TileW = RealtimeGaussianSplatRasterizerEngine.W, TileH = RealtimeGaussianSplatRasterizerEngine.H;
    public const int Cols = (OutW + TileW - 1) / TileW, Rows = (OutH + TileH - 1) / TileH;

    private readonly byte[] _rgb = new byte[OutW * OutH * 3], _tile = new byte[TileW * TileH * 3];
    private readonly float[] _edge = new float[OutW * OutH], _tmp = new float[OutW * OutH];
    private UnifiedGaussianSceneCompositorEngine? _comp;
    private readonly int _env, _body, _face;
    private RgbaOverlayLayer? _text;
    private SdfGlyphAtlas? _atlas;

    public PeakEmotion3dgsThumbnailSynthesizer(int envSplats = UnifiedGaussianSceneCompositorEngine.DefaultEnv, int bodySplats = UnifiedGaussianSceneCompositorEngine.DefaultBody, int faceSplats = UnifiedGaussianSceneCompositorEngine.DefaultFace)
    { _env = envSplats; _body = bodySplats; _face = faceSplats; }

    public ReadOnlySpan<byte> Rgb => _rgb;
    public float FovScale { get; set; } = 1.1f;
    public bool Glow { get; set; } = true;
    public long Renders { get; private set; }
    public double LastRenderMs { get; private set; }
    public double LastTilesMs { get; private set; }
    public int LastTiles { get; private set; }
    public float LastEdgeMean { get; private set; }

    private UnifiedGaussianSceneCompositorEngine Comp => _comp ??= new UnifiedGaussianSceneCompositorEngine(_env, _body, _face);

    /// <summary>9:16 기준 카메라 → 타일(c, r) 오프축 카메라(동일 초점거리 · 주점 이동).</summary>
    public static SplatCamera TileCamera(in SplatCamera baseCam, int col, int row, float fovScale = 1f)
    {
        var tanV = MathF.Tan(baseCam.VFovDeg * fovScale * MathF.PI / 360f);
        var fy = OutH * 0.5f / tanV;   // 정사각 화소 — fx = fy
        var c = baseCam;
        c.VFovDeg = 2f * MathF.Atan(TileH * 0.5f / fy) * 180f / MathF.PI;
        c.Aspect = (float)TileW / TileH;
        c.ShiftX = OutW * 0.5f - TileW * 0.5f - col * TileW;
        c.ShiftY = OutH * 0.5f - TileH * 0.5f - row * TileH;
        return c;
    }

    /// <summary>골든 프레임 → 1080×1920 RGB(타일 렌더 + 글로우 + 헤드라인).</summary>
    public void Render(GoldenFrameSnapshot g, ReadOnlySpan<char> headline)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var comp = Comp;
        var n = Math.Min(g.Avatars, comp.Avatars);
        for (var a = 0; a < n; a++)
        {
            comp.SetAvatarPose(a, g.Seat[a], g.BodyYaw[a], g.HeadYaw[a], g.HeadPitch[a]);
            comp.SetFaceWeights(a, g.Face.AsSpan(a * GoldenFrameSnapshot.K, GoldenFrameSnapshot.K));
        }
        comp.SetProp(g.Prop, g.PropVisible);
        comp.Update();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastTiles = 0;
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var cam = TileCamera(in g.Camera, c, r, FovScale);
                comp.Render(in cam, _tile);
                LastTiles++;
                int x0 = c * TileW, y0 = r * TileH, w = Math.Min(TileW, OutW - x0), h = Math.Min(TileH, OutH - y0);
                for (var j = 0; j < h; j++) _tile.AsSpan(j * TileW * 3, w * 3).CopyTo(_rgb.AsSpan(((y0 + j) * OutW + x0) * 3));
            }
        LastTilesMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (Glow) ApplyGlow();
        Headline(headline);
        Renders++;
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void ApplyGlow()
    {
        // 루마 소벨 × 중앙 가중
        double sum = 0;
        for (var y = 1; y < OutH - 1; y++)
        {
            var wy = MathF.Exp(-MathF.Pow((y - OutH * 0.55f) / (OutH * 0.35f), 2));
            for (var x = 1; x < OutW - 1; x++)
            {
                float L(int xx, int yy) { var o = (yy * OutW + xx) * 3; return 0.299f * _rgb[o] + 0.587f * _rgb[o + 1] + 0.114f * _rgb[o + 2]; }
                var gx = L(x + 1, y - 1) + 2 * L(x + 1, y) + L(x + 1, y + 1) - L(x - 1, y - 1) - 2 * L(x - 1, y) - L(x - 1, y + 1);
                var gy = L(x - 1, y + 1) + 2 * L(x, y + 1) + L(x + 1, y + 1) - L(x - 1, y - 1) - 2 * L(x, y - 1) - L(x + 1, y - 1);
                var wx = MathF.Exp(-MathF.Pow((x - OutW * 0.5f) / (OutW * 0.38f), 2));
                var e = MathF.Min(1f, MathF.Sqrt(gx * gx + gy * gy) / 220f) * wx * wy;
                _edge[y * OutW + x] = e; sum += e;
            }
        }
        LastEdgeMean = (float)(sum / (OutW * OutH));
        for (var pass = 0; pass < 2; pass++) { Box(_edge, _tmp, OutW, OutH, 6, true); Box(_tmp, _edge, OutW, OutH, 6, false); }
        for (var y = 0; y < OutH; y++)
        {
            var t = y / (float)OutH;
            float cr = 0.1f + 0.9f * t, cg = 0.9f - 0.7f * t, cb = 1f;   // 시안 → 마젠타
            for (var x = 0; x < OutW; x++)
            {
                var e = MathF.Min(1f, _edge[y * OutW + x] * 3.2f) * 200f;
                if (e < 1f) continue;
                var o = (y * OutW + x) * 3;
                _rgb[o] = Sat(_rgb[o] + cr * e); _rgb[o + 1] = Sat(_rgb[o + 1] + cg * e); _rgb[o + 2] = Sat(_rgb[o + 2] + cb * e);
            }
        }
    }

    private static byte Sat(float v) => (byte)(v > 255f ? 255 : v < 0f ? 0 : v);

    private static void Box(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        var inv = 1f / (2 * r + 1);
        if (horizontal)
            for (var y = 0; y < h; y++)
            {
                var row = y * w; float acc = 0;
                for (var i = -r; i <= r; i++) acc += src[row + Math.Clamp(i, 0, w - 1)];
                for (var x = 0; x < w; x++) { dst[row + x] = acc * inv; acc += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)]; }
            }
        else
            for (var x = 0; x < w; x++)
            {
                float acc = 0;
                for (var i = -r; i <= r; i++) acc += src[Math.Clamp(i, 0, h - 1) * w + x];
                for (var y = 0; y < h; y++) { dst[y * w + x] = acc * inv; acc += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x]; }
            }
    }

    private void Headline(ReadOnlySpan<char> text)
    {
        // 하단 비네트(가독성)
        for (var y = OutH * 2 / 3; y < OutH; y++)
        {
            var a = (y - OutH * 2 / 3) / (OutH / 3f) * 0.55f;
            var row = y * OutW * 3;
            for (var i = 0; i < OutW * 3; i++) _rgb[row + i] = (byte)(_rgb[row + i] * (1f - a));
        }
        if (text.IsEmpty) return;
        _atlas ??= new SdfGlyphAtlas();
        _text ??= new RgbaOverlayLayer(OutW, OutH);
        _text.Begin();
        _atlas.Ensure(text);
        // 2 줄 분할(공백 기준 중앙) · 줄당 폭 맞춤 크기
        var split = -1;
        if (text.Length > 9) { var mid = text.Length / 2; for (var d = 0; d < mid; d++) { if (mid + d < text.Length && text[mid + d] == ' ') { split = mid + d; break; } if (text[mid - d] == ' ') { split = mid - d; break; } } }
        var l1 = split > 0 ? text[..split] : text; var l2 = split > 0 ? text[(split + 1)..] : default;
        var probe = _text;
        float px = 132f;
        var w1 = SdfText.Draw(_atlas, probe, l1, 0, 0, px, 0, 0, 0).W;
        probe.Begin();
        var w2 = l2.IsEmpty ? 0 : SdfText.Draw(_atlas, probe, l2, 0, 0, px, 0, 0, 0).W;
        var widest = Math.Max(1, Math.Max(w1, w2));
        if (widest > OutW - 120) px *= (OutW - 120f) / widest;
        _text.Begin();
        var y0 = OutH * 0.12f;
        DrawOutlined(l1, y0, px);
        if (!l2.IsEmpty) DrawOutlined(l2, y0 + px * 1.15f, px);
        // RGBA(프리멀티) over RGB
        var p = _text.Pixels;
        for (var i = 0; i < OutW * OutH; i++)
        {
            var a = p[i * 4 + 3];
            if (a == 0) continue;
            var ia = 255 - a; var o = i * 3;
            _rgb[o] = (byte)(_rgb[o] * ia / 255 + p[i * 4]); _rgb[o + 1] = (byte)(_rgb[o + 1] * ia / 255 + p[i * 4 + 1]); _rgb[o + 2] = (byte)(_rgb[o + 2] * ia / 255 + p[i * 4 + 2]);
        }
    }

    private void DrawOutlined(ReadOnlySpan<char> line, float y, float px)
    {
        // 폭 측정(임시 레이어 사용 불가 → 그린 뒤 같은 위치 재그리기로 덮음): 글리프 전진폭 합
        var em = 0f;
        foreach (var ch in line) em += _atlas!.TryGet(ch, out var gi) ? _atlas.Glyph(gi).Advance : 0.5f;
        var x = (OutW - em * px) * 0.5f;
        var o = px * 0.06f;
        for (var k = 0; k < 8; k++)
        {
            var ang = k * MathF.PI / 4f;
            SdfText.Draw(_atlas!, _text!, line, x + MathF.Cos(ang) * o, y + MathF.Sin(ang) * o, px, 0.02f, 0.02f, 0.05f);
        }
        SdfText.Draw(_atlas!, _text!, line, x, y, px, 1f, 0.9f, 0.1f);
    }

    // ══════════════════════ 인코딩(비핫패스) ══════════════════════
    private static readonly uint[] Crc = BuildCrc();
    private static uint[] BuildCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    public static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) { var c = 0xFFFFFFFFu; foreach (var b in type) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8); foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }

    /// <summary>현재 RGB → PNG(8 비트 RGB · Sub 필터 · zlib) 기록 — 바이트 수.</summary>
    public long WritePng(Stream dst) => EncodePng(_rgb, OutW, OutH, dst);

    public static long EncodePng(ReadOnlySpan<byte> rgb, int w, int h, Stream dst)
    {
        var start = dst.CanSeek ? dst.Position : 0;
        Span<byte> hdr = stackalloc byte[13];
        dst.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        BinaryPrimitives.WriteInt32BigEndian(hdr, w); BinaryPrimitives.WriteInt32BigEndian(hdr[4..], h); hdr[8] = 8; hdr[9] = 2; hdr[10] = 0; hdr[11] = 0; hdr[12] = 0;
        Chunk(dst, "IHDR"u8, hdr);
        using var ms = new MemoryStream(w * h);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
        {
            var row = new byte[w * 3 + 1];
            for (var y = 0; y < h; y++)
            {
                row[0] = 1;   // Sub
                var s = rgb.Slice(y * w * 3, w * 3);
                for (var i = 0; i < w * 3; i++) row[i + 1] = (byte)(s[i] - (i >= 3 ? s[i - 3] : 0));
                z.Write(row);
            }
        }
        Chunk(dst, "IDAT"u8, ms.GetBuffer().AsSpan(0, (int)ms.Length));
        Chunk(dst, "IEND"u8, default);
        return dst.CanSeek ? dst.Position - start : 0;
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, data.Length); s.Write(b); s.Write(type); s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(b, Crc32(type, data)); s.Write(b);
    }

    /// <summary>축소 미리보기 BMP(24 비트 · 하향) — 박스 다운샘플.</summary>
    public void WritePreviewBmp(byte[] dst, int w, int h)
    {
        const int Header = 54; var row = w * 3; var pad = (4 - row % 4) % 4;
        if (dst.Length < Header + (row + pad) * h) return;
        dst.AsSpan(0, Header).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(2), Header + (row + pad) * h);
        dst[10] = Header; dst[14] = 40;
        BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(18), w); BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(22), h);
        dst[26] = 1; dst[28] = 24;
        int sx = OutW / w, sy = OutH / h;
        for (var y = 0; y < h; y++)
        {
            var o = Header + (h - 1 - y) * (row + pad);
            for (var x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0;
                for (var j = 0; j < sy; j++) for (var i = 0; i < sx; i++) { var p = ((y * sy + j) * OutW + x * sx + i) * 3; r += _rgb[p]; g += _rgb[p + 1]; b += _rgb[p + 2]; }
                var k = sx * sy;
                dst[o + x * 3] = (byte)(b / k); dst[o + x * 3 + 1] = (byte)(g / k); dst[o + x * 3 + 2] = (byte)(r / k);
            }
        }
    }

    public static int PreviewBytes(int w, int h) => 54 + (w * 3 + (4 - w * 3 % 4) % 4) * h;

    public void Dispose() { _comp?.Dispose(); _comp = null; }
}
