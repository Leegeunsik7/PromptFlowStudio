using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.21.0 정규화 좌표 점(0~1).</summary>
public readonly record struct MaskPoint(float X, float Y);

/// <summary>v16.21.0 브러시 스트로크 — 점열 · 반지름(정규화 · 캔버스 폭 기준) · 지우개.</summary>
public sealed record MaskStroke(IReadOnlyList<MaskPoint> Points, float Radius, bool Erase);

/// <summary>v16.21.0 마스크 바운딩 박스(px) · 점유율.</summary>
public readonly record struct MaskBox(int X, int Y, int W, int H, double Coverage)
{
    public bool IsEmpty => W <= 0 || H <= 0;
    public double CenterX => X + W / 2.0;
    public double CenterY => Y + H / 2.0;
}

/// <summary>v16.21.0 키프레임 보간.</summary>
public enum KeyEase : byte { Linear, EaseInOut, Hold }

/// <summary>v16.21.0 마스크 변환 키프레임 — 시각(초) · 이동(정규화) · 배율 · 회전(도) · 불투명도.</summary>
public readonly record struct MaskKeyframe(double Time, float Dx, float Dy, float Scale, float Rotation, float Opacity, KeyEase Ease = KeyEase.Linear)
{
    public static MaskKeyframe Identity(double t) => new(t, 0, 0, 1, 0, 1);
}

/// <summary>v16.21.0 시각 t 의 평가된 변환.</summary>
public readonly record struct MaskTransform(float Dx, float Dy, float Scale, float Rotation, float Opacity)
{
    public static readonly MaskTransform Identity = new(0, 0, 1, 0, 1);
}

/// <summary>
/// v16.21.0 키프레임 트랙 — 시각 정렬 유지(같은 시각 ±1ms 는 대체) · 이진 탐색 평가(무할당) · 상한 256.
/// </summary>
public sealed class KeyframeTrack
{
    public const int MaxKeys = 256;
    private readonly List<MaskKeyframe> _keys = new(16);

    public int Count => _keys.Count;
    public IReadOnlyList<MaskKeyframe> Keys => _keys;

    public bool Set(MaskKeyframe k)
    {
        if (!double.IsFinite(k.Time) || k.Time < 0) return false;
        var key = k with { Scale = Math.Clamp(k.Scale, 0.05f, 8f), Opacity = Math.Clamp(k.Opacity, 0f, 1f) };
        var i = Lower(key.Time);
        if (i < _keys.Count && Math.Abs(_keys[i].Time - key.Time) < 0.001) { _keys[i] = key; return true; }
        if (i > 0 && Math.Abs(_keys[i - 1].Time - key.Time) < 0.001) { _keys[i - 1] = key; return true; }
        if (_keys.Count >= MaxKeys) return false;
        _keys.Insert(i, key);
        return true;
    }

    public bool RemoveAt(double time)
    {
        var i = Lower(time - 0.001);
        if (i < _keys.Count && Math.Abs(_keys[i].Time - time) < 0.001) { _keys.RemoveAt(i); return true; }
        return false;
    }

    public void Clear() => _keys.Clear();

    /// <summary>시각 t 의 변환(키 없음 → Identity · 범위 밖 → 끝 키 유지).</summary>
    public MaskTransform Evaluate(double t)
    {
        var n = _keys.Count;
        if (n == 0) return MaskTransform.Identity;
        if (t <= _keys[0].Time) return Of(_keys[0]);
        if (t >= _keys[n - 1].Time) return Of(_keys[n - 1]);
        var hi = Lower(t);
        if (hi < n && Math.Abs(_keys[hi].Time - t) < 1e-9) return Of(_keys[hi]);
        var a = _keys[hi - 1];
        var b = _keys[hi];
        var u = (t - a.Time) / Math.Max(1e-9, b.Time - a.Time);
        u = a.Ease switch
        {
            KeyEase.Hold => 0,
            KeyEase.EaseInOut => u * u * (3 - 2 * u),
            _ => u,
        };
        var f = (float)u;
        return new MaskTransform(L(a.Dx, b.Dx, f), L(a.Dy, b.Dy, f), L(a.Scale, b.Scale, f), L(a.Rotation, b.Rotation, f), L(a.Opacity, b.Opacity, f));
    }

    private int Lower(double t)
    {
        int lo = 0, hi = _keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_keys[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static MaskTransform Of(MaskKeyframe k) => new(k.Dx, k.Dy, k.Scale, k.Rotation, k.Opacity);
    private static float L(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>v16.21.0 마스크 문서(동기화/저장 DTO).</summary>
public sealed record MaskDocumentDto(int Schema, int Width, int Height, int StrokeCount, int KeyCount, IReadOnlyList<MaskStrokeDto> Strokes, IReadOnlyList<MaskKeyframe> Keys, string? MediaPath);

/// <summary>v16.21.0 스트로크 DTO — 좌표는 0~65535 양자화(전송량 절감).</summary>
public sealed record MaskStrokeDto(ushort[] Xy, float Radius, bool Erase);

/// <summary>
/// [PromptFlowStudio] v16.21.0 🖌️ 인터랙티브 마스킹 &amp; 키프레임 캔버스 서비스 — 브러시 → 8-bit 마스크 래스터 · 페더 · 바운딩 박스 · 프레임 간 박스 추적 · 키프레임 변환 · PNG 내보내기(순수 BCL).
///   ▸ 래스터: 선분을 반지름/2 간격 원 스탬프로 채움(지우개는 0) — 호출자 버퍼에 제자리 기록(무할당).
///   ▸ 페더: 분리형 박스 블러 2-패스(스크래치는 ArrayPool) · 박스: 임계 이상 픽셀 외곽 · 추적: 축소 그레이 프레임 SAD 템플릿 매칭(±search · 2px 서브샘플).
///   ▸ 키프레임: <see cref="KeyframeTrack"/> 이진 탐색 보간 → <see cref="TransformMask"/>(역 아핀 최근접 샘플)로 시각 t 마스크 생성.
///   ▸ PNG: 그레이스케일 8-bit · ZLibStream · CRC32 테이블(외부 NuGet 0). 영상 박스 추적은 ffmpeg 그레이 raw 추출(없으면 Graceful).
///   ▸ 문서 상태(스트로크/키프레임/미디어)는 lock 보호 + OnChange(예외 격리) — 노드 에디터(ControlNet 마스크)·라이브 동기화 브릿지가 공유.
/// </summary>
public sealed class CanvasMaskingService
{
    public const int SchemaVersion = 1;
    public const int MaxStrokes = 512, MaxPointsPerStroke = 2048;
    public const int TrackW = 160, TrackH = 90;

    private readonly Func<string?> _ffmpeg;
    private readonly object _gate = new();
    private readonly List<MaskStroke> _strokes = new(32);
    private List<MaskPoint>? _active;
    private float _activeRadius;
    private bool _activeErase;

    public CanvasMaskingService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    public CanvasMaskingService(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public event Action? OnChange;

    public int Width { get; private set; } = 640;
    public int Height { get; private set; } = 360;
    public string? MediaPath { get; private set; }
    public KeyframeTrack Track { get; } = new();
    public MaskBox LastBox { get; private set; }
    public string? LastExportPath { get; private set; }
    public bool FfmpegAvailable => _ffmpeg() is not null;

    public IReadOnlyList<MaskStroke> Strokes { get { lock (_gate) return _strokes.ToArray(); } }
    public int StrokeCount { get { lock (_gate) return _strokes.Count; } }

    // ── 문서 편집 ─────────────────────────────────────────────────────────────

    public void SetMedia(string? path, int width, int height)
    {
        lock (_gate)
        {
            MediaPath = path;
            Width = Math.Clamp(width, 16, 4096);
            Height = Math.Clamp(height, 16, 4096);
        }
        Raise();
    }

    public void BeginStroke(MaskPoint p, float radius, bool erase)
    {
        lock (_gate)
        {
            _active = new List<MaskPoint>(64) { Clamp01(p) };
            _activeRadius = Math.Clamp(radius, 0.002f, 0.25f);
            _activeErase = erase;
        }
    }

    /// <summary>점 추가 — 직전 점과 0.3% 미만 거리면 무시(포인터 이벤트 폭주 완화). 추가 여부 반환.</summary>
    public bool AddPoint(MaskPoint p)
    {
        lock (_gate)
        {
            if (_active is null || _active.Count >= MaxPointsPerStroke) return false;
            var q = Clamp01(p);
            var last = _active[^1];
            var dx = q.X - last.X;
            var dy = q.Y - last.Y;
            if (dx * dx + dy * dy < 0.003f * 0.003f) return false;
            _active.Add(q);
            return true;
        }
    }

    /// <summary>진행 중 스트로크(미확정 미리보기).</summary>
    public IReadOnlyList<MaskPoint>? ActivePoints { get { lock (_gate) return _active?.ToArray(); } }

    public MaskStroke? EndStroke()
    {
        MaskStroke? s = null;
        lock (_gate)
        {
            if (_active is { Count: > 0 } && _strokes.Count < MaxStrokes)
            {
                s = new MaskStroke(_active.ToArray(), _activeRadius, _activeErase);
                _strokes.Add(s);
            }
            _active = null;
        }
        if (s is not null) { RecomputeBox(); Raise(); }
        return s;
    }

    public bool AddStroke(MaskStroke s)
    {
        if (s.Points.Count == 0) return false;
        lock (_gate)
        {
            if (_strokes.Count >= MaxStrokes) return false;
            _strokes.Add(s);
        }
        RecomputeBox();
        Raise();
        return true;
    }

    public bool Undo()
    {
        lock (_gate)
        {
            if (_strokes.Count == 0) return false;
            _strokes.RemoveAt(_strokes.Count - 1);
        }
        RecomputeBox();
        Raise();
        return true;
    }

    public void ClearMask()
    {
        lock (_gate) { _strokes.Clear(); _active = null; }
        LastBox = default;
        Raise();
    }

    public bool SetKeyframe(MaskKeyframe k)
    {
        bool ok;
        lock (_gate) ok = Track.Set(k);
        if (ok) Raise();
        return ok;
    }

    public bool RemoveKeyframe(double time)
    {
        bool ok;
        lock (_gate) ok = Track.RemoveAt(time);
        if (ok) Raise();
        return ok;
    }

    public MaskTransform Evaluate(double t) { lock (_gate) return Track.Evaluate(t); }

    /// <summary>현재 문서를 지정 해상도 마스크로 렌더(호출자 버퍼 · Clear 후 순서대로 래스터).</summary>
    public void Render(Span<byte> mask, int w, int h)
    {
        MaskStroke[] strokes;
        lock (_gate) strokes = _strokes.ToArray();
        mask[..(w * h)].Clear();
        foreach (var s in strokes) RasterizeStroke(mask, w, h, s);
    }

    private void RecomputeBox()
    {
        int w, h;
        lock (_gate) { w = Width; h = Height; }
        var buf = ArrayPool<byte>.Shared.Rent(w * h);
        try
        {
            Render(buf, w, h);
            LastBox = ComputeBounds(buf.AsSpan(0, w * h), w, h);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // ── 순수 함수 ─────────────────────────────────────────────────────────────

    /// <summary>스트로크 래스터(제자리) — 반지름은 폭 기준 정규화.</summary>
    public static void RasterizeStroke(Span<byte> mask, int w, int h, MaskStroke s)
    {
        if (s.Points.Count == 0) return;
        var r = Math.Max(0.5f, s.Radius * w);
        var value = s.Erase ? (byte)0 : (byte)255;
        var prev = s.Points[0];
        Stamp(mask, w, h, prev.X * w, prev.Y * h, r, value);
        var step = Math.Max(0.5f, r * 0.5f);
        for (var i = 1; i < s.Points.Count; i++)
        {
            var p = s.Points[i];
            float x0 = prev.X * w, y0 = prev.Y * h, x1 = p.X * w, y1 = p.Y * h;
            var len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            var n = Math.Max(1, (int)MathF.Ceiling(len / step));
            for (var k = 1; k <= n; k++)
            {
                var t = (float)k / n;
                Stamp(mask, w, h, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, r, value);
            }
            prev = p;
        }
    }

    /// <summary>원 스탬프(제자리).</summary>
    public static void Stamp(Span<byte> mask, int w, int h, float cx, float cy, float r, byte value)
    {
        var x0 = Math.Max(0, (int)MathF.Floor(cx - r));
        var x1 = Math.Min(w - 1, (int)MathF.Ceiling(cx + r));
        var y0 = Math.Max(0, (int)MathF.Floor(cy - r));
        var y1 = Math.Min(h - 1, (int)MathF.Ceiling(cy + r));
        var r2 = r * r;
        for (var y = y0; y <= y1; y++)
        {
            var dy = y + 0.5f - cy;
            var row = y * w;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5f - cx;
                if (dx * dx + dy * dy <= r2) mask[row + x] = value;
            }
        }
    }

    /// <summary>분리형 박스 블러 페더(제자리 · 반지름 px).</summary>
    public static void Feather(Span<byte> mask, int w, int h, int radius)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return;
        var tmp = ArrayPool<byte>.Shared.Rent(w * h);
        try
        {
            var t = tmp.AsSpan(0, w * h);
            var win = radius * 2 + 1;
            for (var y = 0; y < h; y++)
            {
                var row = mask.Slice(y * w, w);
                var o = t.Slice(y * w, w);
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += row[Math.Clamp(k, 0, w - 1)];
                for (var x = 0; x < w; x++)
                {
                    o[x] = (byte)(sum / win);
                    sum += row[Math.Min(w - 1, x + radius + 1)] - row[Math.Max(0, x - radius)];
                }
            }
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += t[Math.Clamp(k, 0, h - 1) * w + x];
                for (var y = 0; y < h; y++)
                {
                    mask[y * w + x] = (byte)(sum / win);
                    sum += t[Math.Min(h - 1, y + radius + 1) * w + x] - t[Math.Max(0, y - radius) * w + x];
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    /// <summary>임계 이상 픽셀 외곽 박스 + 점유율.</summary>
    public static MaskBox ComputeBounds(ReadOnlySpan<byte> mask, int w, int h, byte threshold = 128)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1, on = 0;
        for (var y = 0; y < h; y++)
        {
            var row = mask.Slice(y * w, w);
            for (var x = 0; x < w; x++)
            {
                if (row[x] < threshold) continue;
                on++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? default : new MaskBox(minX, minY, maxX - minX + 1, maxY - minY + 1, (double)on / (w * h));
    }

    /// <summary>
    /// 박스 추적 — prev 프레임의 box 템플릿을 next 프레임 ±search 범위에서 SAD 최소 위치로 이동(2px 서브샘플). 반환: 새 박스 · 정규화 오차(0=완전 일치).
    /// </summary>
    public static (MaskBox Box, double Error) TrackBox(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> next, int w, int h, MaskBox box, int search = 12)
    {
        if (box.IsEmpty) return (box, 1);
        var bx = Math.Clamp(box.X, 0, w - 1);
        var by = Math.Clamp(box.Y, 0, h - 1);
        var bw = Math.Min(box.W, w - bx);
        var bh = Math.Min(box.H, h - by);
        long best = long.MaxValue;
        int bestDx = 0, bestDy = 0, samples = 0;
        for (var dy = -search; dy <= search; dy++)
        {
            var ny = by + dy;
            if (ny < 0 || ny + bh > h) continue;
            for (var dx = -search; dx <= search; dx++)
            {
                var nx = bx + dx;
                if (nx < 0 || nx + bw > w) continue;
                long sad = 0;
                var cnt = 0;
                var pruned = false;
                for (var y = 0; y < bh; y += 2)
                {
                    if (sad > best) { pruned = true; break; }   // 조기 종료(동점은 끝까지 계산해 근접 이동 우선 판정)
                    var pr = (by + y) * w + bx;
                    var nr = (ny + y) * w + nx;
                    for (var x = 0; x < bw; x += 2) { sad += Math.Abs(prev[pr + x] - next[nr + x]); cnt++; }
                }
                if (pruned) continue;
                if (sad < best || (sad == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bestDx) + Math.Abs(bestDy)))
                {
                    best = sad; bestDx = dx; bestDy = dy; samples = cnt;
                }
            }
        }
        if (best == long.MaxValue) return (box, 1);
        return (box with { X = bx + bestDx, Y = by + bestDy }, samples > 0 ? best / (255.0 * samples) : 1);
    }

    /// <summary>키프레임 변환 적용(역 아핀 · 최근접 · 불투명도 곱) — 중심 기준 배율/회전 + 정규화 이동.</summary>
    public static void TransformMask(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, MaskTransform t)
    {
        var cx = w / 2f;
        var cy = h / 2f;
        var rad = -t.Rotation * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var inv = 1f / Math.Max(0.05f, t.Scale);
        var tx = t.Dx * w;
        var ty = t.Dy * h;
        var op = Math.Clamp(t.Opacity, 0f, 1f);
        for (var y = 0; y < h; y++)
        {
            var py = y + 0.5f - cy - ty;
            for (var x = 0; x < w; x++)
            {
                var px = x + 0.5f - cx - tx;
                var sx = (px * cos - py * sin) * inv + cx;
                var sy = (px * sin + py * cos) * inv + cy;
                var ix = (int)MathF.Floor(sx);
                var iy = (int)MathF.Floor(sy);
                dst[y * w + x] = ix < 0 || iy < 0 || ix >= w || iy >= h ? (byte)0 : (byte)(src[iy * w + ix] * op);
            }
        }
    }

    private static readonly uint[] Crc = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = Crc[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = Crc[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>8-bit 그레이스케일 PNG 인코딩.</summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> gray, int w, int h)
    {
        if (w <= 0 || h <= 0 || gray.Length < w * h) throw new ArgumentException("마스크 크기 불일치");
        using var ms = new MemoryStream(w * h / 4 + 128);
        ms.Write(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR"u8, ihdr);
        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
            {
                Span<byte> filter = stackalloc byte[1];
                for (var y = 0; y < h; y++) { zs.Write(filter); zs.Write(gray.Slice(y * w, w)); }
            }
            idat = z.ToArray();
        }
        Chunk(ms, "IDAT"u8, idat);
        Chunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        s.Write(type);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(b4, Crc32(type, data));
        s.Write(b4);
    }

    // ── 직렬화 · 내보내기 · 추적 ───────────────────────────────────────────────

    public MaskDocumentDto ToDto()
    {
        lock (_gate)
        {
            var list = new List<MaskStrokeDto>(_strokes.Count);
            foreach (var s in _strokes)
            {
                var xy = new ushort[s.Points.Count * 2];
                for (var i = 0; i < s.Points.Count; i++) { xy[i * 2] = Q(s.Points[i].X); xy[i * 2 + 1] = Q(s.Points[i].Y); }
                list.Add(new MaskStrokeDto(xy, s.Radius, s.Erase));
            }
            var keys = new MaskKeyframe[Track.Count];
            for (var i = 0; i < keys.Length; i++) keys[i] = Track.Keys[i];
            return new MaskDocumentDto(SchemaVersion, Width, Height, list.Count, keys.Length, list, keys, MediaPath);
        }
    }

    /// <summary>DTO 적용(스키마 · 상한 · 집계 필드 · 좌표 쌍 검증 · 실패 시 무변경).</summary>
    public (bool Ok, string Message) LoadDto(MaskDocumentDto? dto)
    {
        if (dto is null || dto.Schema != SchemaVersion || dto.Strokes is null || dto.Keys is null) return (false, "스키마 불일치");
        if (dto.Strokes.Count > MaxStrokes || dto.Keys.Count > KeyframeTrack.MaxKeys) return (false, "상한 초과");
        if (dto.StrokeCount != dto.Strokes.Count || dto.KeyCount != dto.Keys.Count) return (false, "집계 필드 불일치");
        var strokes = new List<MaskStroke>(dto.Strokes.Count);
        foreach (var s in dto.Strokes)
        {
            if (s.Xy is null || s.Xy.Length == 0 || (s.Xy.Length & 1) != 0 || s.Xy.Length / 2 > MaxPointsPerStroke || !float.IsFinite(s.Radius)) return (false, "스트로크 형식 오류");
            var pts = new MaskPoint[s.Xy.Length / 2];
            for (var i = 0; i < pts.Length; i++) pts[i] = new MaskPoint(s.Xy[i * 2] / 65535f, s.Xy[i * 2 + 1] / 65535f);
            strokes.Add(new MaskStroke(pts, Math.Clamp(s.Radius, 0.002f, 0.25f), s.Erase));
        }
        lock (_gate)
        {
            Width = Math.Clamp(dto.Width, 16, 4096);
            Height = Math.Clamp(dto.Height, 16, 4096);
            MediaPath = dto.MediaPath ?? MediaPath;
            _strokes.Clear();
            _strokes.AddRange(strokes);
            Track.Clear();
            foreach (var k in dto.Keys) Track.Set(k);
        }
        RecomputeBox();
        Raise();
        return (true, "불러옴");
    }

    public string ToJson() => JsonSerializer.Serialize(ToDto());

    public (bool Ok, string Message) LoadJson(string json)
    {
        try { return LoadDto(JsonSerializer.Deserialize<MaskDocumentDto>(json)); }
        catch (JsonException) { return (false, "JSON 파싱 실패"); }
    }

    /// <summary>시각 t 마스크 PNG + 키프레임 JSON 저장(예외 0) → PNG 경로.</summary>
    public async Task<string?> ExportAsync(string directory, double time = 0, int featherPx = 3, CancellationToken ct = default)
    {
        try
        {
            int w, h;
            lock (_gate) { w = Width; h = Height; }
            var baseMask = new byte[w * h];
            var outMask = new byte[w * h];
            Render(baseMask, w, h);
            TransformMask(baseMask, outMask, w, h, Evaluate(time));
            Feather(outMask, w, h, Math.Clamp(featherPx, 0, 32));
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var png = Path.Combine(directory, "mask_" + stamp + ".png");
            await File.WriteAllBytesAsync(png, EncodePng(outMask, w, h), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "mask_" + stamp + ".json"), ToJson(), ct).ConfigureAwait(false);
            LastExportPath = png;
            Raise();
            return png;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Program.Log("CanvasMasking.Export", ex); return null; }
    }

    /// <summary>
    /// 영상 박스 추적 → 키프레임 자동 생성(ffmpeg 그레이 raw 160x90 · steps 구간). 반환: 생성 키 수(실패 0).
    /// </summary>
    public async Task<int> TrackAcrossAsync(double fromSec, double toSec, int steps = 6, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        string? media;
        MaskBox box0;
        int w, h;
        lock (_gate) { media = MediaPath; box0 = LastBox; w = Width; h = Height; }
        if (exe is null || string.IsNullOrWhiteSpace(media) || !File.Exists(media) || box0.IsEmpty || toSec <= fromSec) return 0;
        steps = Math.Clamp(steps, 1, 30);
        var sx = (double)TrackW / w;
        var sy = (double)TrackH / h;
        var box = new MaskBox((int)(box0.X * sx), (int)(box0.Y * sy), Math.Max(2, (int)(box0.W * sx)), Math.Max(2, (int)(box0.H * sy)), box0.Coverage);
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_track_" + Guid.NewGuid().ToString("N") + ".gray");
        try
        {
            var prev = await GrayFrameAsync(exe, media, fromSec, tmp, ct).ConfigureAwait(false);
            if (prev is null) return 0;
            var start = box;
            var made = 0;
            SetKeyframe(MaskKeyframe.Identity(fromSec));
            for (var i = 1; i <= steps; i++)
            {
                var t = fromSec + (toSec - fromSec) * i / steps;
                var next = await GrayFrameAsync(exe, media, t, tmp, ct).ConfigureAwait(false);
                if (next is null) break;
                var (nb, err) = TrackBox(prev, next, TrackW, TrackH, box);
                if (err > 0.35) break;   // 추적 실패(가림/장면 전환) — 이후 키 생성 중단
                box = nb;
                prev = next;
                var dx = (float)((box.CenterX - start.CenterX) / TrackW);
                var dy = (float)((box.CenterY - start.CenterY) / TrackH);
                if (SetKeyframe(new MaskKeyframe(Math.Round(t, 3), dx, dy, 1, 0, 1))) made++;
            }
            return made;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Program.Log("CanvasMasking.Track", ex); return 0; }
        finally { try { File.Delete(tmp); } catch { /* 무시 */ } }
    }

    private static async Task<byte[]?> GrayFrameAsync(string exe, string media, double t, string tmp, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, new List<string>
        {
            "-y", "-hide_banner", "-nostdin", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", media,
            "-vf", "scale=" + TrackW.ToString(CultureInfo.InvariantCulture) + ":" + TrackH.ToString(CultureInfo.InvariantCulture) + ",format=gray",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", tmp,
        }, ct: ct).ConfigureAwait(false);
        if (!r.Ok || !File.Exists(tmp)) return null;
        var b = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
        return b.Length >= TrackW * TrackH ? b : null;
    }

    /// <summary>미리보기 프레임 JPG 추출(예외 0 · ffmpeg 없으면 null).</summary>
    public async Task<string?> ExtractFrameAsync(double t, string directory, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        var media = MediaPath;
        if (exe is null || string.IsNullOrWhiteSpace(media) || !File.Exists(media)) return null;
        try
        {
            Directory.CreateDirectory(directory);
            var outPath = Path.Combine(directory, "frame_" + ((long)(t * 1000)).ToString(CultureInfo.InvariantCulture) + ".jpg");
            var r = await ProcessRunner.RunAsync(exe, new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", media, "-frames:v", "1", "-q:v", "3", outPath,
            }, ct: ct).ConfigureAwait(false);
            return r.Ok && File.Exists(outPath) ? outPath : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Program.Log("CanvasMasking.Frame", ex); return null; }
    }

    private static ushort Q(float v) => (ushort)Math.Clamp((int)MathF.Round(v * 65535f), 0, 65535);
    private static MaskPoint Clamp01(MaskPoint p) => new(Math.Clamp(float.IsFinite(p.X) ? p.X : 0, 0, 1), Math.Clamp(float.IsFinite(p.Y) ? p.Y : 0, 0, 1));

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("CanvasMasking.OnChange", ex); }
    }
}
