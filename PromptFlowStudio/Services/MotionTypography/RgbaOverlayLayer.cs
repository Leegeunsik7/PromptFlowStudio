namespace PromptFlowStudio.Services.MotionTypography;

/// <summary>
/// 32-bit 사전 곱 RGBA 오버레이 프레임버퍼(<see cref="Over"/> · <see cref="FillRoundRect"/> · <see cref="CompositeNv12"/> 무할당).
/// 더티 사각형만 지우고 합성한다. NV12 합성: Y' = Y(1−a) + (16a + 219·luma(premul)) · UV 는 2×2 평균 알파로 BT.709 제한 범위 블렌드.
/// </summary>
public sealed class RgbaOverlayLayer
{
    private readonly byte[] _px;
    private int _dx0, _dy0, _dx1, _dy1;

    public RgbaOverlayLayer(int width, int height)
    {
        Width = width; Height = height;
        _px = new byte[width * height * 4];
        _dx0 = _dy0 = int.MaxValue; _dx1 = _dy1 = 0;
    }

    public int Width { get; }
    public int Height { get; }
    public Span<byte> Pixels => _px;
    public PxRect Dirty => _dx1 > _dx0 ? new PxRect(_dx0, _dy0, _dx1 - _dx0, _dy1 - _dy0) : default;
    public long BlendedPixels { get; private set; }

    /// <summary>프레임 시작 — 직전 더티 영역 소거.</summary>
    public void Begin()
    {
        if (_dx1 > _dx0) for (var y = _dy0; y < _dy1; y++) _px.AsSpan((y * Width + _dx0) * 4, (_dx1 - _dx0) * 4).Clear();
        _dx0 = _dy0 = int.MaxValue; _dx1 = _dy1 = 0;
        BlendedPixels = 0;
    }

    private void Mark(int x0, int y0, int x1, int y1)
    {
        if (x0 < _dx0) _dx0 = x0; if (y0 < _dy0) _dy0 = y0;
        if (x1 > _dx1) _dx1 = x1; if (y1 > _dy1) _dy1 = y1;
    }

    /// <summary>스트레이트 색(0~1) · 커버리지 a 를 over 합성.</summary>
    public void Over(int x, int y, float r, float g, float b, float a)
    {
        if (a <= 0.002f || (uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        a = MathF.Min(a, 1f);
        var o = (y * Width + x) * 4;
        var inv = 1f - a;
        _px[o] = (byte)(r * a * 255f + _px[o] * inv + 0.5f);
        _px[o + 1] = (byte)(g * a * 255f + _px[o + 1] * inv + 0.5f);
        _px[o + 2] = (byte)(b * a * 255f + _px[o + 2] * inv + 0.5f);
        _px[o + 3] = (byte)(a * 255f + _px[o + 3] * inv + 0.5f);
        BlendedPixels++;
    }

    public void MarkRect(in PxRect r) { if (!r.IsEmpty) Mark(Math.Max(r.X, 0), Math.Max(r.Y, 0), Math.Min(r.Right, Width), Math.Min(r.Bottom, Height)); }

    /// <summary>둥근 사각형 채움(SDF AA) — 색 · 불투명도.</summary>
    public void FillRoundRect(in PxRect rc, float radius, float r, float g, float b, float alpha)
    {
        if (rc.IsEmpty) return;
        int x0 = Math.Max(rc.X, 0), y0 = Math.Max(rc.Y, 0), x1 = Math.Min(rc.Right, Width), y1 = Math.Min(rc.Bottom, Height);
        float cx = rc.X + rc.W * 0.5f, cy = rc.Y + rc.H * 0.5f, hx = rc.W * 0.5f - radius, hy = rc.H * 0.5f - radius;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                float qx = MathF.Abs(x + 0.5f - cx) - hx, qy = MathF.Abs(y + 0.5f - cy) - hy;
                float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
                var d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
                Over(x, y, r, g, b, alpha * Math.Clamp(0.5f - d, 0f, 1f));
            }
        Mark(x0, y0, x1, y1);
    }

    /// <summary>NV12 프레임에 더티 영역 합성(무할당).</summary>
    public void CompositeNv12(Span<byte> yPlane, Span<byte> uv, int stride)
    {
        if (_dx1 <= _dx0) return;
        int x0 = _dx0 & ~1, y0 = _dy0 & ~1, x1 = Math.Min((_dx1 + 1) & ~1, Width), y1 = Math.Min((_dy1 + 1) & ~1, Height);
        for (var y = y0; y < y1; y++)
        {
            var row = y * Width * 4; var yr = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var o = row + x * 4;
                int a = _px[o + 3];
                if (a == 0) continue;
                var lum = 0.2126f * _px[o] + 0.7152f * _px[o + 1] + 0.0722f * _px[o + 2];
                var fa = a / 255f;
                yPlane[yr + x] = (byte)Math.Clamp(yPlane[yr + x] * (1f - fa) + 16f * fa + 219f / 255f * lum + 0.5f, 0f, 255f);
            }
        }
        for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                int a = 0, r = 0, g = 0, b = 0;
                for (var k = 0; k < 4; k++)
                {
                    var o = ((y + (k >> 1)) * Width + x + (k & 1)) * 4;
                    r += _px[o]; g += _px[o + 1]; b += _px[o + 2]; a += _px[o + 3];
                }
                if (a == 0) continue;
                float fa = a / 1020f, pr = r / 1020f, pg = g / 1020f, pb = b / 1020f;
                var u = (y >> 1) * stride + x;
                uv[u] = (byte)Math.Clamp(uv[u] * (1f - fa) + 128f * fa + 224f * (-0.1146f * pr - 0.3854f * pg + 0.5f * pb) + 0.5f, 0f, 255f);
                uv[u + 1] = (byte)Math.Clamp(uv[u + 1] * (1f - fa) + 128f * fa + 224f * (0.5f * pr - 0.4542f * pg - 0.0458f * pb) + 0.5f, 0f, 255f);
            }
    }
}
