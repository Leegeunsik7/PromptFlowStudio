using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.VisualQa;

public enum VisualDefectKind : byte { None, LayoutShift, PixelDrift, Popping }

/// <summary>시각 결함 로그 항목(값 형식 · 썸네일은 링 슬롯 인덱스).</summary>
public readonly record struct VisualDefect(long Seq, long Frame, VisualDefectKind Kind, float Score, int X, int Y, int W, int H, int Thumb);

/// <summary>
/// [PromptFlow Studio v17.34.0] 🔬 픽셀 단위 시각 회귀 &amp; SSIM 비교 합성기(<see cref="Compare"/> 무할당 · 480×270 분석 격자).
///   ▸ 픽셀 정확도: 1920×1080 RGBA ↔ 기준 프레임 Vector&lt;uint&gt; 동등 비교 → 불일치 픽셀 수(UI 영역).
///   ▸ 휘도: 4×4 박스 다운샘플(BT.601 정수 가중) → 480×270.
///   ▸ SSIM(Wang 2004 · C1 = (0.01·255)² · C2 = (0.03·255)²): 8×8 창 · 보폭 4 · 적분 영상 5종(Σx · Σy · Σx² · Σy² · Σxy · double) → 창별 SSIM 맵 · UI 영역 평균.
///   ▸ 판독: UI SSIM &lt; 0.98 → 저 SSIM 창(&lt; 0.7) 경계 상자 · 면적 ≥ 2 % = 레이아웃 붕괴 · 그 외 = 픽셀 드리프트.
///     3D = 분석 프로브(오버레이 제외 AOV 320×270) 8×8 블록 · 변화 블록 중 ±4 px 움직임 보상 후에도 잔차 &gt; 12 인 미정합 블록 수 ≥ 24 · &gt; 4·EMA = 팝핑(연속 운동 ≠ 순간이동 · 복귀 프레임도 별도 기록).
///   ▸ 오차 히트맵: UI = (1 − SSIM) · 3D = 시간 차분 → 흑→청→황→적 색상표(480×270 RGBA).
///   ▸ 기준 프레임: 마일스톤 이름 · 메모리 + 파일(zlib 원시 RGBA · <c>.pfsb</c>) 저장/복원.
/// </summary>
public sealed class PixelPerfectVisualRegressionSynthesizer
{
    public const int FW = RealtimeHeadlessFrameBufferEngine.W, FH = RealtimeHeadlessFrameBufferEngine.H, S = 4, LW = FW / S, LH = FH / S, Win = 8, Step = 4;
    public const int Search = 4, GW = (LW - Win) / Step + 1, GH = (LH - Win) / Step + 1, Log = 16, ThumbW = 160, ThumbH = 90;
    public const double C1 = 6.5025, C2 = 58.5225;
    public const float HotBlock = 12f, PopBlocks = 24f, PassSsim = 0.98f, LowWindow = 0.7f, LayoutArea = 0.02f;
    private static readonly int UiCols = RealtimeHeadlessFrameBufferEngine.UiWidth / S;
    private static readonly int SceneRows = RealtimeHeadlessFrameBufferEngine.SceneRegion.Bottom / S;

    private readonly uint[] _base = new uint[FW * FH];
    private readonly byte[] _lb = new byte[LW * LH], _lc = new byte[LW * LH], _lp = new byte[LW * LH];
    private readonly double[] _ix = new double[(LW + 1) * (LH + 1)], _iy = new double[(LW + 1) * (LH + 1)], _ixx = new double[(LW + 1) * (LH + 1)], _iyy = new double[(LW + 1) * (LH + 1)], _ixy = new double[(LW + 1) * (LH + 1)];
    private readonly float[] _ssim = new float[GW * GH];
    private readonly int[] _rowAcc = new int[LW];
    private const int PW = RealtimeHeadlessFrameBufferEngine.ProbeW, PH = RealtimeHeadlessFrameBufferEngine.ProbeH;
    private readonly byte[] _pc = new byte[PW * PH], _pp = new byte[PW * PH];
    private bool _hasProbePrev;
    private readonly uint[] _heat = new uint[LW * LH];
    private readonly VisualDefect[] _log = new VisualDefect[Log];
    private readonly byte[] _thumbs = new byte[Log * ThumbW * ThumbH];
    private readonly object _gate = new();
    private int _logHead, _logCount;
    private long _seq, _frames;
    private bool _hasBase, _hasPrev, _inDefect;
    private float _tempEma = 1f;

    public string BaselineName { get; private set; } = "";
    public bool HasBaseline => _hasBase;
    public float UiSsim { get; private set; } = 1f;
    public float MinUiSsim { get; private set; } = 1f;
    public float SceneTemporalDiff { get; private set; }
    public float SceneTemporalEma => _tempEma;
    public long MismatchPixels { get; private set; }
    public float PixelExactRatio { get; private set; } = 1f;
    public int LowWindows { get; private set; }
    public VisualDefectKind LastKind { get; private set; }
    public long LayoutDefects { get; private set; }
    public long DriftDefects { get; private set; }
    public long PopDefects { get; private set; }
    public long Comparisons { get; private set; }
    public double LastCompareUs { get; private set; }
    public double MaxCompareUs { get; private set; }
    public int LogCount => _logCount;
    public PixelRect LastBox { get; private set; }

    public VisualDefect Defect(int back) { lock (_gate) return _log[(_logHead - 1 - back + 2 * Log) % Log]; }
    public ReadOnlySpan<byte> Thumb(int slot) => _thumbs.AsSpan(slot * ThumbW * ThumbH, ThumbW * ThumbH);
    public float SsimWindow(int gx, int gy) => _ssim[gy * GW + gx];
    public ReadOnlySpan<uint> Heatmap => _heat;
    public void CopyHeatmap(Span<uint> dst) { lock (_gate) _heat.AsSpan().CopyTo(dst); }

    public void ResetMetrics()
    {
        lock (_gate)
        {
            MinUiSsim = 1f; LayoutDefects = DriftDefects = PopDefects = Comparisons = 0; MaxCompareUs = 0; _logCount = 0; _inDefect = false;
        }
    }

    // ══════════════════════ 기준 프레임 ══════════════════════

    public void CaptureBaseline(ReadOnlySpan<uint> frame, string name)
    {
        lock (_gate)
        {
            frame.CopyTo(_base);
            Luma(_base, _lb);
            _hasBase = true;
            BaselineName = string.IsNullOrWhiteSpace(name) ? "baseline" : name.Trim();
            MinUiSsim = 1f;
        }
    }

    /// <summary>기준 프레임 파일 저장(헤더 "PFSB" · W · H · zlib RGBA).</summary>
    public void SaveBaseline(string path)
    {
        lock (_gate)
        {
            if (!_hasBase) throw new InvalidOperationException("기준 프레임 없음");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            Span<byte> hdr = stackalloc byte[12];
            "PFSB"u8.CopyTo(hdr);
            BitConverter.TryWriteBytes(hdr[4..], FW); BitConverter.TryWriteBytes(hdr[8..], FH);
            fs.Write(hdr);
            using var z = new ZLibStream(fs, CompressionLevel.Fastest);
            z.Write(MemoryMarshal.AsBytes(_base.AsSpan()));
        }
    }

    public bool LoadBaseline(string path, string name)
    {
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        Span<byte> hdr = stackalloc byte[12];
        fs.ReadExactly(hdr);
        if (!hdr[..4].SequenceEqual("PFSB"u8) || BitConverter.ToInt32(hdr[4..]) != FW || BitConverter.ToInt32(hdr[8..]) != FH) return false;
        lock (_gate)
        {
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            z.ReadExactly(MemoryMarshal.AsBytes(_base.AsSpan()));
            Luma(_base, _lb);
            _hasBase = true; BaselineName = name; MinUiSsim = 1f;
        }
        return true;
    }

    // ══════════════════════ 비교(무할당 핫패스) ══════════════════════

    /// <summary>현재 프레임 판독 → SSIM · 픽셀 불일치 · 시간 차분 · 히트맵 · 결함 로그.</summary>
    public VisualDefectKind Compare(ReadOnlySpan<uint> frame, long frameIndex) => Compare(frame, ReadOnlySpan<uint>.Empty, frameIndex);

    /// <summary>현재 프레임 + 3D 분석 프로브(오버레이 없는 액터/지형 AOV · <see cref="RealtimeHeadlessFrameBufferEngine.ProbeW"/>×<see cref="RealtimeHeadlessFrameBufferEngine.ProbeH"/>) 판독.</summary>
    public VisualDefectKind Compare(ReadOnlySpan<uint> frame, ReadOnlySpan<uint> probe, long frameIndex)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _frames++;
            Luma(frame, _lc);
            var kind = VisualDefectKind.None;
            var score = 0f;
            int bx0 = 0, by0 = 0, bx1 = 0, by1 = 0;

            // 3D 팝핑: 분석 프로브(오버레이 제외 · 디버그 도형의 의도된 불연속 배제) 시간 차분
            var hasProbe = probe.Length == PW * PH;
            if (hasProbe) for (var i = 0; i < _pc.Length; i++) { var q = probe[i]; _pc[i] = (byte)(((q >> 16 & 255) * 77 + (q >> 8 & 255) * 150 + (q & 255) * 29) >> 8); }
            if (_hasProbePrev && hasProbe)
            {
                // 8×8 블록 변화 → ±4 px 움직임 보상 최소 잔차: 연속 운동은 정합 · 순간이동(팝)은 미정합 → 미정합 블록 수
                var hot = 0;
                for (var by = 0; by + 8 <= PH; by += 8)
                    for (var bx = 0; bx + 8 <= PW; bx += 8)
                    {
                        var sum = 0;
                        for (var y = by; y < by + 8; y++)
                        {
                            var o = y * PW + bx;
                            for (var x = 0; x < 8; x++) sum += Math.Abs(_pc[o + x] - _pp[o + x]);
                        }
                        if (sum / 64f > HotBlock && Residual(bx, by) > HotBlock) hot++;
                    }
                float diff = hot;
                SceneTemporalDiff = diff;
                if (_frames > 30 && diff >= PopBlocks && diff > 4f * _tempEma)
                {
                    kind = VisualDefectKind.Popping; score = diff / MathF.Max(_tempEma, 0.1f);
                    bx0 = UiCols; by0 = 0; bx1 = LW; by1 = LH / 2;
                }
                else _tempEma += (_frames < 30 ? 0.2f : 0.05f) * (diff - _tempEma);
            }

            if (_hasBase)
            {
                // 픽셀 정확 비교(UI 영역 행 단위 SIMD)
                long mismatch = 0;
                var uiW = RealtimeHeadlessFrameBufferEngine.UiWidth;
                for (var y = 0; y < FH; y++)
                {
                    var a = frame.Slice(y * FW, uiW); var b = _base.AsSpan(y * FW, uiW);
                    if (a.SequenceEqual(b)) continue;
                    mismatch += CountMismatch(a, b);
                }
                MismatchPixels = mismatch;
                PixelExactRatio = 1f - mismatch / (float)(uiW * FH);
                Ssim();
                // UI 영역 평균 SSIM · 저 SSIM 창 경계 상자
                double acc = 0; var n = 0; var low = 0;
                int lx0 = int.MaxValue, ly0 = int.MaxValue, lx1 = -1, ly1 = -1;
                var gxUi = (UiCols - Win) / Step + 1;
                for (var gy = 0; gy < GH; gy++)
                    for (var gx = 0; gx < gxUi; gx++)
                    {
                        var s = _ssim[gy * GW + gx];
                        acc += s; n++;
                        if (s < LowWindow)
                        {
                            low++;
                            lx0 = Math.Min(lx0, gx); ly0 = Math.Min(ly0, gy); lx1 = Math.Max(lx1, gx); ly1 = Math.Max(ly1, gy);
                        }
                    }
                UiSsim = (float)(acc / n);
                LowWindows = low;
                if (UiSsim < MinUiSsim) MinUiSsim = UiSsim;
                if (kind == VisualDefectKind.None && UiSsim < PassSsim)
                {
                    kind = low / (float)n >= LayoutArea ? VisualDefectKind.LayoutShift : VisualDefectKind.PixelDrift;
                    score = 1f - UiSsim;
                    if (low > 0) { bx0 = lx0 * Step; by0 = ly0 * Step; bx1 = lx1 * Step + Win; by1 = ly1 * Step + Win; }
                    else { bx0 = 0; by0 = 0; bx1 = UiCols; by1 = LH; }
                }
            }
            else { UiSsim = 1f; MismatchPixels = 0; PixelExactRatio = 1f; LowWindows = 0; }

            BuildHeatmap();
            // 결함 로그: 상태 전이(진입) 시 1건 + 썸네일
            if (kind != VisualDefectKind.None)
            {
                if (!_inDefect || kind == VisualDefectKind.Popping)
                {
                    var slot = _logHead;
                    _log[slot] = new VisualDefect(++_seq, frameIndex, kind, score, bx0 * S, by0 * S, (bx1 - bx0) * S, (by1 - by0) * S, slot);
                    Thumbnail(_lc, slot);
                    _logHead = (_logHead + 1) % Log;
                    if (_logCount < Log) _logCount++;
                    LastBox = new PixelRect(bx0 * S, by0 * S, (bx1 - bx0) * S, (by1 - by0) * S);
                    switch (kind)
                    {
                        case VisualDefectKind.LayoutShift: LayoutDefects++; break;
                        case VisualDefectKind.PixelDrift: DriftDefects++; break;
                        default: PopDefects++; break;
                    }
                }
                if (kind != VisualDefectKind.Popping) _inDefect = true;
            }
            else _inDefect = false;
            LastKind = kind;
            _lc.AsSpan().CopyTo(_lp);
            _hasPrev = true;
            if (hasProbe) { _pc.AsSpan().CopyTo(_pp); _hasProbePrev = true; }
            Comparisons++;
            var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            LastCompareUs = us;
            if (us > MaxCompareUs && Comparisons > 3) MaxCompareUs = us;
            return kind;
        }
    }

    /// <summary>프로브 블록(8×8) 현재 ↔ 직전 프레임 ±4 px 이동 최소 평균 절대 잔차.</summary>
    private float Residual(int bx, int by)
    {
        var best = int.MaxValue;
        for (var dy = -Search; dy <= Search; dy++)
            for (var dx = -Search; dx <= Search; dx++)
            {
                int sx = bx + dx, sy = by + dy;
                if (sx < 0 || sy < 0 || sx + 8 > PW || sy + 8 > PH) continue;
                var sum = 0;
                for (var y = 0; y < 8 && sum < best; y++)
                {
                    int oc = (by + y) * PW + bx, op = (sy + y) * PW + sx;
                    for (var x = 0; x < 8; x++) sum += Math.Abs(_pc[oc + x] - _pp[op + x]);
                }
                if (sum < best) best = sum;
            }
        return best / 64f;
    }

    private static long CountMismatch(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        long n = 0; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var va = MemoryMarshal.Cast<uint, Vector<uint>>(a); var vb = MemoryMarshal.Cast<uint, Vector<uint>>(b);
            for (var k = 0; k < va.Length; k++)
            {
                var eq = Vector.Equals(va[k], vb[k]);
                if (eq == Vector<uint>.AllBitsSet) continue;
                for (var l = 0; l < Vector<uint>.Count; l++) if (eq[l] == 0) n++;
            }
            i = va.Length * Vector<uint>.Count;
        }
        for (; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    /// <summary>4×4 박스 휘도(0.299 R + 0.587 G + 0.114 B · 정수 77/150/29 · 행 누산 1 패스).</summary>
    public void Luma(ReadOnlySpan<uint> src, Span<byte> dst)
    {
        var acc = _rowAcc;
        for (var ly = 0; ly < LH; ly++)
        {
            Array.Clear(acc);
            for (var dy = 0; dy < S; dy++)
            {
                var row = src.Slice((ly * S + dy) * FW, FW);
                for (var x = 0; x < FW; x++)
                {
                    var p = row[x];
                    acc[x >> 2] += (int)((p >> 16 & 255) * 77 + (p >> 8 & 255) * 150 + (p & 255) * 29);
                }
            }
            var d = dst.Slice(ly * LW, LW);
            for (var lx = 0; lx < LW; lx++) d[lx] = (byte)(acc[lx] >> 12);
        }
    }

    /// <summary>적분 영상 기반 창별 SSIM(8×8 · 보폭 4).</summary>
    private void Ssim()
    {
        const int iw = LW + 1;
        Array.Clear(_ix, 0, iw); Array.Clear(_iy, 0, iw); Array.Clear(_ixx, 0, iw); Array.Clear(_iyy, 0, iw); Array.Clear(_ixy, 0, iw);
        for (var y = 1; y <= LH; y++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            _ix[y * iw] = _iy[y * iw] = _ixx[y * iw] = _iyy[y * iw] = _ixy[y * iw] = 0;
            for (var x = 1; x <= LW; x++)
            {
                double a = _lb[(y - 1) * LW + x - 1], b = _lc[(y - 1) * LW + x - 1];
                sx += a; sy += b; sxx += a * a; syy += b * b; sxy += a * b;
                var k = y * iw + x; var u = k - iw;
                _ix[k] = _ix[u] + sx; _iy[k] = _iy[u] + sy; _ixx[k] = _ixx[u] + sxx; _iyy[k] = _iyy[u] + syy; _ixy[k] = _ixy[u] + sxy;
            }
        }
        const double n = Win * Win;
        for (var gy = 0; gy < GH; gy++)
            for (var gx = 0; gx < GW; gx++)
            {
                int x0 = gx * Step, y0 = gy * Step, x1 = x0 + Win, y1 = y0 + Win;
                int a = y0 * iw + x0, b = y0 * iw + x1, c = y1 * iw + x0, d = y1 * iw + x1;
                var mx = (_ix[d] - _ix[b] - _ix[c] + _ix[a]) / n;
                var my = (_iy[d] - _iy[b] - _iy[c] + _iy[a]) / n;
                var vx = (_ixx[d] - _ixx[b] - _ixx[c] + _ixx[a]) / n - mx * mx;
                var vy = (_iyy[d] - _iyy[b] - _iyy[c] + _iyy[a]) / n - my * my;
                var cxy = (_ixy[d] - _ixy[b] - _ixy[c] + _ixy[a]) / n - mx * my;
                _ssim[gy * GW + gx] = (float)((2 * mx * my + C1) * (2 * cxy + C2) / ((mx * mx + my * my + C1) * (vx + vy + C2)));
            }
    }

    private void BuildHeatmap()
    {
        for (var y = 0; y < LH; y++)
            for (var x = 0; x < LW; x++)
            {
                float v;
                if (x < UiCols)
                {
                    if (!_hasBase) v = 0f;
                    else
                    {
                        var gx = Math.Min(GW - 1, Math.Max(0, (x - Win / 2) / Step)); var gy = Math.Min(GH - 1, Math.Max(0, (y - Win / 2) / Step));
                        v = Math.Clamp((1f - _ssim[gy * GW + gx]) * 2f, 0f, 1f);
                    }
                }
                else v = _hasPrev ? Math.Clamp(Math.Abs(_lc[y * LW + x] - _lp[y * LW + x]) / 64f, 0f, 1f) : 0f;
                _heat[y * LW + x] = ColorMap(v, _lc[y * LW + x]);
            }
    }

    /// <summary>흑→청→황→적(배경 휘도 20 % 혼합).</summary>
    public static uint ColorMap(float v, byte luma)
    {
        float r, g, b;
        if (v < 0.33f) { var t = v / 0.33f; r = 0f; g = 0.3f * t; b = t; }
        else if (v < 0.66f) { var t = (v - 0.33f) / 0.33f; r = t; g = 0.3f + 0.6f * t; b = 1f - t; }
        else { var t = (v - 0.66f) / 0.34f; r = 1f; g = 0.9f * (1f - t); b = 0f; }
        var l = luma * 0.2f;
        uint R = (uint)Math.Clamp(r * 255f * 0.8f + l, 0f, 255f), G = (uint)Math.Clamp(g * 255f * 0.8f + l, 0f, 255f), B = (uint)Math.Clamp(b * 255f * 0.8f + l, 0f, 255f);
        return 0xFF000000u | R << 16 | G << 8 | B;
    }

    private void Thumbnail(byte[] luma, int slot)
    {
        var dst = _thumbs.AsSpan(slot * ThumbW * ThumbH, ThumbW * ThumbH);
        for (var y = 0; y < ThumbH; y++)
            for (var x = 0; x < ThumbW; x++)
                dst[y * ThumbW + x] = luma[(y * LH / ThumbH) * LW + x * LW / ThumbW];
    }
}
