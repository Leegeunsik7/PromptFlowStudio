using System;
using System.Numerics;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v5.7.0] 지각적 해시(Difference Hash · dHash 64bit) 정체성 수치화 — 100% C# BCL(외부 SDK/OpenCV 0).
///   ▸ 순수 알고리즘부(<see cref="ComputeDHash"/>/<see cref="SimilarityPercent"/>/<see cref="Downsample"/>)는 모든 타깃에서 컴파일(모바일 안전).
///   ▸ 이미지 파일 디코드는 <b>데스크톱(WINDOWS) 전용</b>(System.Drawing) — <c>#if WINDOWS</c> 로 격리해 안드로이드/iOS 소스링크 빌드를 깨지 않는다.
///   ▸ 앵커(원본 시드) vs 실측(결과 첫 프레임) 두 이미지의 dHash 해밍 거리 → 정체성 유지율(%).
/// </summary>
public static class IdentityHashAnalyzer
{
    /// <summary>dHash 그리드 — 9열 × 8행(가로 인접 비교로 8×8 = 64비트 산출).</summary>
    public const int Cols = 9;
    public const int Rows = 8;

    /// <summary>9×8 그레이스케일(0~255 · row-major · 길이 72) → 64bit dHash(각 행에서 가로 인접 밝기 비교).</summary>
    public static ulong ComputeDHash(ReadOnlySpan<byte> gray)
    {
        if (gray.Length < Cols * Rows) return 0;
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < Rows; y++)
        {
            for (var x = 0; x < Cols - 1; x++)
            {
                if (gray[y * Cols + x] > gray[y * Cols + x + 1]) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>
    /// [v5.7.1] 평균 휘도 정규화(128 기준 평탄화) 후 dHash — 조명/노출 변동에 의한 pHash 오탐을 보정.
    ///   ▸ 전체 평균 밝기를 128로 이동(shift)해 밝기 차이를 상쇄한 뒤 인접 비교(밝기 무관 구조 해시).
    /// </summary>
    public static ulong ComputeNormalizedDHash(ReadOnlySpan<byte> gray)
    {
        if (gray.Length < Cols * Rows) return 0;
        long sum = 0;
        for (var i = 0; i < gray.Length; i++) sum += gray[i];
        var shift = 128 - (int)(sum / gray.Length);   // 평균을 128 기준점으로 평탄화
        Span<byte> norm = stackalloc byte[Cols * Rows];
        for (var i = 0; i < norm.Length; i++) norm[i] = (byte)Math.Clamp(gray[i] + shift, 0, 255);
        return ComputeDHash(norm);
    }

    /// <summary>두 해시의 해밍 거리(다른 비트 수 · 0~64).</summary>
    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>두 dHash 정체성 유지율(%) — (64 − 해밍) / 64 × 100.</summary>
    public static double SimilarityPercent(ulong a, ulong b) => (64 - Hamming(a, b)) / 64.0 * 100.0;

    /// <summary>ARGB(int · 0xAARRGGBB) 픽셀 배열 → 9×8 그레이스케일 박스 다운샘플(BT.601 휘도 · 순수 BCL).</summary>
    public static byte[] Downsample(ReadOnlySpan<int> argb, int srcW, int srcH)
    {
        var gray = new byte[Cols * Rows];
        if (srcW <= 0 || srcH <= 0 || argb.Length < srcW * srcH) return gray;
        for (var gy = 0; gy < Rows; gy++)
        {
            for (var gx = 0; gx < Cols; gx++)
            {
                var x0 = gx * srcW / Cols;
                var x1 = Math.Max(x0 + 1, (gx + 1) * srcW / Cols);
                var y0 = gy * srcH / Rows;
                var y1 = Math.Max(y0 + 1, (gy + 1) * srcH / Rows);
                long sum = 0;
                var cnt = 0;
                for (var y = y0; y < y1 && y < srcH; y++)
                {
                    for (var x = x0; x < x1 && x < srcW; x++)
                    {
                        var p = argb[y * srcW + x];
                        int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                        sum += (r * 299 + g * 587 + b * 114) / 1000;   // BT.601 휘도
                        cnt++;
                    }
                }
                gray[gy * Cols + gx] = (byte)(cnt == 0 ? 0 : sum / cnt);
            }
        }
        return gray;
    }

#if WINDOWS
    /// <summary>[데스크톱 전용] 이미지 파일(PNG/JPEG 등) → dHash(System.Drawing 디코드 · 실패 시 false).</summary>
    public static bool TryHashFile(string? path, out ulong hash)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return false;
        try
        {
            using var bmp = new System.Drawing.Bitmap(path!);
            hash = HashBitmap(bmp);
            return true;
        }
        catch { return false; }   // 손상/미지원 포맷 — Graceful
    }

    private static ulong HashBitmap(System.Drawing.Bitmap bmp)
    {
        int w = Math.Max(1, bmp.Width), h = Math.Max(1, bmp.Height);
        // 원본이 커도 최대 64×64 샘플만 순회(GetPixel 비용 상한 · 해시엔 충분).
        int sw = Math.Min(w, 64), sh = Math.Min(h, 64);
        var argb = new int[sw * sh];
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < sw; x++)
            {
                var px = bmp.GetPixel(x * w / sw, y * h / sh);
                argb[y * sw + x] = (px.R << 16) | (px.G << 8) | px.B;
            }
        }
        return ComputeNormalizedDHash(Downsample(argb, sw, sh));   // v5.7.1 밝기 정규화 dHash(조명 오탐 보정)
    }

    /// <summary>[데스크톱 전용] 앵커·실측 이미지 파일 정체성 유지율(%). 어느 하나라도 디코드 실패 시 null.</summary>
    public static double? ScoreFiles(string? anchorPath, string? actualPath)
        => TryHashFile(anchorPath, out var a) && TryHashFile(actualPath, out var b)
            ? SimilarityPercent(a, b) : (double?)null;
#endif
}
