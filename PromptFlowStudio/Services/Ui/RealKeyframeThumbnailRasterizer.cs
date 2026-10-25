using System.Buffers.Binary;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.Ui;

public enum ThumbnailSourceKind : byte { None, DecodedIdr, IdrBitstreamMap }

/// <summary>
/// [PromptFlow Studio v18.0.3] 🖼️ 실 키프레임 썸네일 래스터라이저 — 바이트 해시 색조 대신 피크 시점 IDR 접근 단위(Annex-B)로 9:16 1080×1920 PNG 를 만든다.
///   ▸ 1 순위 = 실 디코딩: 관리 Baseline 디코더(v17.74 부분집합) → Windows MF H.264 MFT → NV12 → 9:16 리프레임 박스 크롭 · 쌍선형 업스케일(BT.601).
///   ▸ 2 순위(디코딩 불가 프로파일 · 손상) = IDR 슬라이스 RBSP 비트 할당 맵: 매크로블록 격자(SPS mb 폭×높이)에 RBSP 를 균등 분할 →
///     셀별 바이트 평균(3 성분) · 대비 정규화 · 쌍선형 업스케일(실 비트스트림 함수 · 디코딩 화상 아님 = <see cref="Kind"/> 로 구분).
///   ▸ 공통: 줌 펀치 GOP 비네트 · 자막 프리셋 색 바 · PNG(시그니처 · IHDR · IDAT · IEND) + 108×192 미리보기 PNG. 버퍼(RGB · NV12 · PNG) 1 회 할당 재사용.
/// </summary>
public sealed class RealKeyframeThumbnailRasterizer
{
    public const int OutW = 1080, OutH = 1920, PreviewW = 108, PreviewH = 192, ReframeRefW = 1920;
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    private readonly MemoryStream _png = new(256 * 1024), _preview = new(16 * 1024);
    private readonly byte[] _previewRgb = new byte[PreviewW * PreviewH * 3];
    private readonly float[] _cell = new float[3 * 128 * 128];
    private byte[] _rgb = Array.Empty<byte>(), _au = new byte[64 * 1024], _rbsp = new byte[64 * 1024], _y = Array.Empty<byte>(), _uv = Array.Empty<byte>();
    private int _pngLen, _previewLen;
    private ulong _lastKey;

    /// <summary>MF 하드웨어 디코더 시도 허용(관리 디코더 실패 시) — 오케스트레이터 기본 경로.</summary>
    public bool AllowHardwareDecode { get; set; } = true;
    public ThumbnailSourceKind Kind { get; private set; }
    public VideoDecoderBackend Backend { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public int IdrNalBytes { get; private set; }
    public long Renders { get; private set; }
    /// <summary>입력(IDR · 파라미터 셋 · 박스 · 펀치 · 색) 동일 → 직전 PNG 재사용 횟수.</summary>
    public long CacheHits { get; private set; }
    public double LastMs { get; private set; }
    public string? LastError { get; private set; }
    public ReadOnlyMemory<byte> Png => _png.GetBuffer().AsMemory(0, _pngLen);
    public ReadOnlyMemory<byte> PreviewPng => _preview.GetBuffer().AsMemory(0, _previewLen);
    public ReadOnlySpan<byte> Rgb => _rgb;

    /// <summary>
    /// IDR AU(+ AU 에 SPS/PPS 없으면 <paramref name="paramSets"/> 선행) → 9:16 썸네일. <paramref name="boxX"/> = 1920 기준 리프레임 박스 좌단 ·
    /// <paramref name="accent"/> = 0xRRGGBB 자막 바 색. IDR NAL 없으면 false.
    /// </summary>
    public bool Rasterize(ReadOnlySpan<byte> idrAu, ReadOnlySpan<byte> paramSets, int boxX, bool punched, uint accent, bool allowHardware = true)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var key = InputKey(idrAu, paramSets, boxX, punched, accent, allowHardware);
        if (key == _lastKey && _pngLen > 0) { CacheHits++; return true; }
        _lastKey = 0;
        LastError = null; Kind = ThumbnailSourceKind.None; Backend = VideoDecoderBackend.None;
        ReadOnlySpan<byte> sps = default, idr = default;
        Locate(idrAu, ref sps, ref idr);
        if (sps.IsEmpty) Locate(paramSets, ref sps, ref idr);
        if (idr.IsEmpty) { LastError = "IDR NAL 없음"; return false; }
        IdrNalBytes = idr.Length;
        int w = 1920, h = 1080, mbW = 120, mbH = 68;
        if (LocalVideoFileRingBufferSourceInjector.TryReadSpsSize(sps, out var sw, out var sh, out var smw, out var smh)) { w = sw; h = sh; mbW = smw; mbH = smh; }
        SourceWidth = w; SourceHeight = h;
        if (_rgb.Length == 0) _rgb = new byte[OutW * OutH * 3];

        // 9:16 크롭 박스(원본 좌표) — 세로 원본이면 전체 폭
        var cropW = Math.Min(w, h * 9 / 16);
        var x0 = Math.Clamp((int)((long)boxX * w / ReframeRefW), 0, w - cropW);

        if (TryDecode(idrAu, paramSets, sps.IsEmpty, w, h, allowHardware)) { NvToRgb(w, h, x0, cropW); Kind = ThumbnailSourceKind.DecodedIdr; }
        else { BitstreamMap(idr, mbW, mbH, w, x0, cropW); Kind = ThumbnailSourceKind.IdrBitstreamMap; }
        Overlay(punched, accent);

        _png.SetLength(0);
        PeakEmotion3dgsThumbnailSynthesizer.EncodePng(_rgb, OutW, OutH, _png);
        _pngLen = (int)_png.Length;
        for (var y = 0; y < PreviewH; y++)
            for (var x = 0; x < PreviewW; x++)
                _rgb.AsSpan(((y * OutH / PreviewH) * OutW + x * OutW / PreviewW) * 3, 3).CopyTo(_previewRgb.AsSpan((y * PreviewW + x) * 3));
        _preview.SetLength(0);
        PeakEmotion3dgsThumbnailSynthesizer.EncodePng(_previewRgb, PreviewW, PreviewH, _preview);
        _previewLen = (int)_preview.Length;
        _lastKey = key;
        Renders++;
        LastMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return true;
    }

    /// <summary>Annex-B 시작 코드 스캔 → 첫 SPS · 첫 IDR NAL(후행 0 바이트 제외).</summary>
    private static void Locate(ReadOnlySpan<byte> annexB, ref ReadOnlySpan<byte> sps, ref ReadOnlySpan<byte> idr)
    {
        var i = annexB.IndexOf(StartCode);
        while (i >= 0)
        {
            var h = i + 3;
            var rel = h < annexB.Length ? annexB[h..].IndexOf(StartCode) : -1;
            var end = rel < 0 ? annexB.Length : h + rel;
            var next = rel < 0 ? -1 : end;
            while (end > h && annexB[end - 1] == 0) end--;
            if (end > h)
            {
                var t = annexB[h] & 0x1F;
                if (t == 7 && sps.IsEmpty) sps = annexB[h..end];
                else if (t == 5 && idr.IsEmpty) idr = annexB[h..end];
            }
            i = next;
        }
    }

    // ══════════════════════ 1 순위: 실 디코딩 ══════════════════════

    private bool TryDecode(ReadOnlySpan<byte> au, ReadOnlySpan<byte> paramSets, bool prependParams, int w, int h, bool allowHardware)
    {
        var need = au.Length + (prependParams ? paramSets.Length : 0);
        if (_au.Length < need) _au = new byte[need + 1024];
        var n = 0;
        if (prependParams) { paramSets.CopyTo(_au); n = paramSets.Length; }
        au.CopyTo(_au.AsSpan(n)); n += au.Length;
        if (_y.Length < w * h) { _y = new byte[w * h]; _uv = new byte[w * h / 2 + w]; }
        var bitstream = _au.AsSpan(0, n);
        try
        {
            using (var d = new ManagedBaselineH264Decoder())
            {
                if (d.Decode(bitstream, 0) >= 0 && d.Width == w && d.Height == h)
                {
                    d.Flush();
                    if (d.TryTake(_y, _uv)) { Backend = VideoDecoderBackend.ManagedBaseline; return true; }
                }
            }
            if (!allowHardware) return false;
            using var mf = MediaFoundationVideoDecoder.TryCreate(w, h, 30);
            if (mf is null || mf.Decode(bitstream, 0) < 0) return false;
            mf.Flush();
            if (!mf.TryTake(_y, _uv)) return false;
            Backend = VideoDecoderBackend.MediaFoundation;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = "디코딩 폴백: " + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>NV12(BT.601 제한 범위) 크롭 박스 → 1080×1920 RGB(루마 쌍선형 · 크로마 최근접).</summary>
    private void NvToRgb(int w, int h, int x0, int cropW)
    {
        var y = _y; var uv = _uv; var rgb = _rgb;
        for (var oy = 0; oy < OutH; oy++)
        {
            var sy = (oy + 0.5f) * h / OutH - 0.5f;
            var y0 = Math.Clamp((int)sy, 0, h - 1); var y1 = Math.Min(y0 + 1, h - 1); var fy = Math.Clamp(sy - y0, 0f, 1f);
            var cr = (y0 >> 1) * w;
            for (var ox = 0; ox < OutW; ox++)
            {
                var sx = x0 + (ox + 0.5f) * cropW / OutW - 0.5f;
                var xa = Math.Clamp((int)sx, 0, w - 1); var xb = Math.Min(xa + 1, w - 1); var fx = Math.Clamp(sx - xa, 0f, 1f);
                var l = (y[y0 * w + xa] * (1 - fx) + y[y0 * w + xb] * fx) * (1 - fy) + (y[y1 * w + xa] * (1 - fx) + y[y1 * w + xb] * fx) * fy;
                int c = (int)l - 16, d = uv[cr + (xa & ~1)] - 128, e = uv[cr + (xa & ~1) + 1] - 128;
                var o = (oy * OutW + ox) * 3;
                rgb[o] = Clip((298 * c + 409 * e + 128) >> 8);
                rgb[o + 1] = Clip((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgb[o + 2] = Clip((298 * c + 516 * d + 128) >> 8);
            }
        }
    }

    // ══════════════════════ 2 순위: IDR RBSP 비트 할당 맵 ══════════════════════

    /// <summary>IDR RBSP(헤더 1 바이트 제외 · 에뮬레이션 방지 제거) → MB 격자 셀 3 성분 평균 → 대비 정규화 → 크롭 열 쌍선형 업스케일.</summary>
    private void BitstreamMap(ReadOnlySpan<byte> idr, int mbW, int mbH, int w, int x0, int cropW)
    {
        if (_rbsp.Length < idr.Length) _rbsp = new byte[idr.Length + 1024];
        var n = H264BitReader.Unescape(idr[1..], _rbsp);
        var rb = _rbsp.AsSpan(0, n);
        // 격자: MB 격자를 128×128 이하 · 셀당 ≥ 3 바이트로 축소
        int gw = Math.Clamp(mbW, 1, 128), gh = Math.Clamp(mbH, 1, 128);
        while (gw * gh * 3 > n && (gw > 1 || gh > 1)) { gw = Math.Max(1, gw / 2); gh = Math.Max(1, gh / 2); }
        var cells = gw * gh;
        float lo = float.MaxValue, hi = float.MinValue;
        for (var c = 0; c < cells; c++)
        {
            var a = (int)((long)c * n / cells); var b = (int)((long)(c + 1) * n / cells);
            float r = 0, g = 0, bl = 0; var k = 0;
            for (var i = a; i + 2 < b; i += 3) { r += rb[i]; g += rb[i + 1]; bl += rb[i + 2]; k++; }
            if (k == 0 && a < n) { r = g = bl = rb[a]; k = 1; }
            var inv = k > 0 ? 1f / k : 0f;
            _cell[c * 3] = r * inv; _cell[c * 3 + 1] = g * inv; _cell[c * 3 + 2] = bl * inv;
            for (var q = 0; q < 3; q++) { var v = _cell[c * 3 + q]; if (v < lo) lo = v; if (v > hi) hi = v; }
        }
        var scale = hi - lo > 1e-3f ? 215f / (hi - lo) : 0f;
        var cx0 = (float)x0 / w * gw; var cxw = (float)cropW / w * gw;
        for (var oy = 0; oy < OutH; oy++)
        {
            var sy = (oy + 0.5f) * gh / OutH - 0.5f;
            var ya = Math.Clamp((int)MathF.Floor(sy), 0, gh - 1); var yb = Math.Min(ya + 1, gh - 1); var fy = Math.Clamp(sy - ya, 0f, 1f);
            for (var ox = 0; ox < OutW; ox++)
            {
                var sx = cx0 + (ox + 0.5f) * cxw / OutW - 0.5f;
                var xa = Math.Clamp((int)MathF.Floor(sx), 0, gw - 1); var xb = Math.Min(xa + 1, gw - 1); var fx = Math.Clamp(sx - xa, 0f, 1f);
                var o = (oy * OutW + ox) * 3;
                for (var q = 0; q < 3; q++)
                {
                    var v = (_cell[(ya * gw + xa) * 3 + q] * (1 - fx) + _cell[(ya * gw + xb) * 3 + q] * fx) * (1 - fy)
                          + (_cell[(yb * gw + xa) * 3 + q] * (1 - fx) + _cell[(yb * gw + xb) * 3 + q] * fx) * fy;
                    _rgb[o + q] = (byte)Math.Clamp(20f + (v - lo) * scale, 0f, 255f);
                }
            }
        }
    }

    // ══════════════════════ 공통 오버레이 ══════════════════════

    /// <summary>줌 펀치 GOP 비네트(가장자리 감광 강화) · 자막 프리셋 색 바(62~70 % 높이).</summary>
    private void Overlay(bool punched, uint accent)
    {
        var k = punched ? 1.6f : 0.9f;
        byte ar = (byte)(accent >> 16), ag = (byte)(accent >> 8), ab = (byte)accent;
        int barY0 = OutH * 62 / 100, barY1 = OutH * 70 / 100, barX0 = OutW / 10, barX1 = OutW - OutW / 10;
        for (var y = 0; y < OutH; y++)
        {
            var dy = (y - OutH / 2) / (float)OutH;
            for (var x = 0; x < OutW; x++)
            {
                var o = (y * OutW + x) * 3;
                if (y >= barY0 && y < barY1 && x >= barX0 && x < barX1) { _rgb[o] = ar; _rgb[o + 1] = ag; _rgb[o + 2] = ab; continue; }
                var dx = (x - OutW / 2) / (float)OutW;
                var v = Math.Clamp(1.08f - (dx * dx + dy * dy) * k, 0.35f, 1f);
                _rgb[o] = (byte)(_rgb[o] * v); _rgb[o + 1] = (byte)(_rgb[o + 1] * v); _rgb[o + 2] = (byte)(_rgb[o + 2] * v);
            }
        }
    }

    /// <summary>FNV-1a 64 — 썸네일은 입력의 순수 함수이므로 같은 키면 래스터 · PNG 인코딩 생략.</summary>
    private static ulong InputKey(ReadOnlySpan<byte> au, ReadOnlySpan<byte> ps, int boxX, bool punched, uint accent, bool hw)
    {
        var h = 14695981039346656037UL;
        foreach (var b in au) h = (h ^ b) * 1099511628211UL;
        foreach (var b in ps) h = (h ^ b) * 1099511628211UL;
        h = (h ^ (uint)boxX) * 1099511628211UL;
        h = (h ^ accent) * 1099511628211UL;
        h = (h ^ (punched ? 1UL : 0UL) ^ (hw ? 2UL : 0UL)) * 1099511628211UL;
        return h == 0 ? 1 : h;
    }

    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>PNG 구조 검증 — 시그니처 · 첫 청크 IHDR(13 B) · IDAT ≥ 1 · 마지막 IEND · 청크별 CRC-32.</summary>
    public static bool ValidatePng(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 8 + 25 + 12 || !png[..8].SequenceEqual(Signature)) return false;
        var p = 8; var idat = 0; var first = true; var end = false;
        while (p + 12 <= png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png[p..]);
            if (len < 0 || p + 12 + len > png.Length) return false;
            var type = png.Slice(p + 4, 4); var data = png.Slice(p + 8, len);
            if (BinaryPrimitives.ReadUInt32BigEndian(png[(p + 8 + len)..]) != PeakEmotion3dgsThumbnailSynthesizer.Crc32(type, data)) return false;
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || len != 13) return false;
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                first = false;
            }
            else if (type.SequenceEqual("IDAT"u8)) idat++;
            else if (type.SequenceEqual("IEND"u8)) { end = p + 12 + len == png.Length; break; }
            p += 12 + len;
        }
        return end && idat > 0 && width > 0 && height > 0;
    }
}
