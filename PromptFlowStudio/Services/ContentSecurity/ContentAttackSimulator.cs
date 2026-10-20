namespace PromptFlowStudio.Services.ContentSecurity;

public enum AttackKind : byte { None, Recompress, Noise, Brightness, FaceSwap, Splice }

/// <summary>
/// [PromptFlow Studio v16.90.0] 🧨 배포/공격 시뮬레이터(검증 · 데모용 · 무할당): JPEG 유사 재압축(표준 휘도 양자화 표 · 품질 Q · 휘도만) · 가우시안 유사 노이즈 ·
/// 밝기/대비 · 얼굴 교체(영역을 ½ 다운샘플 → 2× 최근접 업샘플 + 색 이동 = 생성 모델 업샘플 흔적 근사) · 스플라이스(다른 프레임 영역 복사).
/// </summary>
public static class ContentAttackSimulator
{
    private static readonly int[] JpegLuma =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    public static string Name(AttackKind k) => k switch
    {
        AttackKind.Recompress => "재압축", AttackKind.Noise => "노이즈", AttackKind.Brightness => "밝기/대비", AttackKind.FaceSwap => "얼굴 교체(딥페이크)", AttackKind.Splice => "영역 스플라이스", _ => "원본",
    };

    /// <summary>JPEG 유사 재압축(휘도 · 품질 1..100).</summary>
    public static void Recompress(Span<float> rgb, int w, int h, int quality)
    {
        var s = quality < 50 ? 5000f / Math.Max(quality, 1) : 200f - 2f * quality;
        Span<float> blk = stackalloc float[64];
        Span<float> d = stackalloc float[64];
        for (var by = 0; by < h / 8; by++)
            for (var bx = 0; bx < w / 8; bx++)
            {
                for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) { var i = ((by * 8 + y) * w + bx * 8 + x) * 3; blk[y * 8 + x] = (0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2]) * 255f; }
                d.Clear();
                for (var v = 0; v < 8; v++)
                    for (var u = 0; u < 8; u++)
                    {
                        var b = NeuralInvisibleWatermarkEncoderEngine.BasisOf(u, v);
                        var c = WmSimd.Dot(blk, b);
                        var q = MathF.Max(1f, MathF.Floor((JpegLuma[v * 8 + u] * s + 50f) / 100f));
                        WmSimd.MulAdd(d, b, MathF.Round(c / q) * q - c);
                    }
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        var i = ((by * 8 + y) * w + bx * 8 + x) * 3; var dd = d[y * 8 + x] / 255f;
                        for (var ch = 0; ch < 3; ch++) rgb[i + ch] = Math.Clamp(rgb[i + ch] + dd, 0f, 1f);
                    }
            }
    }

    public static void Noise(Span<float> rgb, float sigma, uint seed)
    {
        var r = seed | 1;
        for (var i = 0; i < rgb.Length; i++)
        {
            r ^= r << 13; r ^= r >> 17; r ^= r << 5; var a = r * (1f / uint.MaxValue);
            r ^= r << 13; r ^= r >> 17; r ^= r << 5; var b = r * (1f / uint.MaxValue);
            rgb[i] = Math.Clamp(rgb[i] + (a + b - 1f) * sigma * 2.45f, 0f, 1f);
        }
    }

    public static void Brightness(Span<float> rgb, float gain, float offset)
    {
        for (var i = 0; i < rgb.Length; i++) rgb[i] = Math.Clamp(rgb[i] * gain + offset, 0f, 1f);
    }

    /// <summary>얼굴 교체 근사: 영역 ½ 다운샘플 → 2× 최근접 업샘플 · 피부 색 이동 · 소스 패턴 혼합.</summary>
    public static void FaceSwap(Span<float> rgb, int w, int x0, int y0, int rw, int rh)
    {
        for (var y = y0; y < y0 + rh; y += 2)
            for (var x = x0; x < x0 + rw; x += 2)
            {
                float r = 0f, g = 0f, b = 0f;
                for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++) { var i = ((y + dy) * w + x + dx) * 3; r += rgb[i]; g += rgb[i + 1]; b += rgb[i + 2]; }
                r = r * 0.25f * 0.85f + 0.12f; g = g * 0.25f * 0.8f + 0.06f; b = b * 0.25f * 0.8f + 0.03f;
                var pat = 0.06f * MathF.Sin((x - x0) * 0.3f) * MathF.Cos((y - y0) * 0.25f);
                for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++)
                {
                    var i = ((y + dy) * w + x + dx) * 3;
                    rgb[i] = Math.Clamp(r + pat, 0f, 1f); rgb[i + 1] = Math.Clamp(g + pat, 0f, 1f); rgb[i + 2] = Math.Clamp(b + pat, 0f, 1f);
                }
            }
    }

    /// <summary>스플라이스: src 프레임의 (sx, sy) 영역을 dst (x0, y0)에 복사.</summary>
    public static void Splice(Span<float> dst, ReadOnlySpan<float> src, int w, int sx, int sy, int x0, int y0, int rw, int rh)
    {
        for (var y = 0; y < rh; y++) src.Slice(((sy + y) * w + sx) * 3, rw * 3).CopyTo(dst.Slice(((y0 + y) * w + x0) * 3, rw * 3));
    }
}
