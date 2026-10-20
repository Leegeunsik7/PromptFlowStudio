using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.22.0 아이덴티티 어댑터(ComfyUI 커스텀 노드 계열).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityAdapter>))]
public enum IdentityAdapter { None, InstantId, PuLid }

/// <summary>v16.22.0 정규화 얼굴 영역(0~1) — 기본: 중앙 상단 정사각(아기 인물 사진 구도).</summary>
public readonly record struct FaceRegion(float X, float Y, float W, float H)
{
    public static readonly FaceRegion Default = new(0.2f, 0.08f, 0.6f, 0.6f);

    public FaceRegion Clamp()
    {
        var x = Math.Clamp(float.IsFinite(X) ? X : 0, 0, 0.95f);
        var y = Math.Clamp(float.IsFinite(Y) ? Y : 0, 0, 0.95f);
        return new FaceRegion(x, y, Math.Clamp(float.IsFinite(W) ? W : 1, 0.05f, 1 - x), Math.Clamp(float.IsFinite(H) ? H : 1, 0.05f, 1 - y));
    }
}

/// <summary>v16.22.0 정체성 조건부 생성 설정 — 어댑터 · 강도 · 적용 스텝 구간 · 참조 얼굴 이미지.</summary>
public sealed record IdentityConditioning(IdentityAdapter Adapter, double Weight, double StartAt, double EndAt, string ReferenceImage);

/// <summary>v16.22.0 컷 정합성 결과 — 신뢰도(0~100) · 특징 코사인 · dHash 유사도 · 판정.</summary>
public readonly record struct IdentityScore(double Confidence, double Cosine, double HashSimilarity, IdentityVerdict Verdict)
{
    public static readonly IdentityScore None = new(0, 0, 0, IdentityVerdict.Unknown);
}

/// <summary>v16.22.0 정합성 판정.</summary>
public enum IdentityVerdict { Unknown, Locked, Drifting, Lost }

/// <summary>
/// [PromptFlowStudio] v16.22.0 👶 캐릭터 아이덴티티 고정 서비스 — 등록 사진 → 외형 특징 벡터 · 컷별 정합성 신뢰도 · 생성 조건부(Wan I2V / ComfyUI InstantID·PuLID).
///   ▸ 특징 벡터(<see cref="Dimensions"/>=160): 얼굴 영역 32x32 그레이 4x4 셀 × 8방향 기울기 히스토그램(128 · 셀 L2) + 색상 16 hue × 채도 2단(32) — 블록별 L2 정규화.
///     ⚠ 로컬 폴백은 <b>외형 서명</b>(조명·각도 변화에 민감)이지 얼굴 인식 딥러닝 임베딩이 아니다. 실제 InstantID/PuLID 임베딩은 ComfyUI 측 노드가 참조 이미지에서 추출.
///   ▸ 신뢰도 = 0.7×코사인 매핑(0.6→0 · 1.0→100) + 0.3×밝기 정규화 dHash 유사도 → Locked ≥80 · Drifting ≥60 · Lost.
///   ▸ 생성 고정: Wan 클라우드 = 얼굴 참조 I2V(앵커) + 정체성 구문 + 왜곡 네거티브 + 시드 고정 / ComfyUI = <see cref="InjectComfyWorkflow"/> 로 LoadImage → ApplyInstantID|ApplyPulid 노드를
///     모델 로더 ↔ KSampler 사이에 결선(노드 이름은 설치한 커스텀 노드 기준 · 결선점). 100% 보존은 보장할 수 없어 신뢰도로 측정·재생성 판단.
///   ▸ 이미지 디코드는 ffmpeg(크로스 플랫폼 · 없으면 Graceful) · 특징 계산은 Span 순수 함수(무할당).
/// </summary>
public sealed class CharacterIdentityLockService
{
    public const int Dimensions = 160, HogDims = 128, ColorDims = 32;
    public const int Patch = 32, SampleSize = 96;
    public const double LockedThreshold = 80, DriftThreshold = 60;

    /// <summary>정체성 유지 프롬프트 구문.</summary>
    public const string IdentityPrompt = "the exact same baby as the reference image, identical facial features, same eye shape, same nose and mouth, same hair, consistent face across frames";

    /// <summary>정체성 붕괴 방지 네거티브.</summary>
    public static readonly IReadOnlyList<string> IdentityNegatives = new[]
    {
        "different person", "different baby", "face swap", "changing face", "inconsistent facial features", "morphing face", "aged up", "adult face on baby",
    };

    private readonly Func<string?> _ffmpeg;

    public CharacterIdentityLockService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    public CharacterIdentityLockService(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public bool IsAvailable => _ffmpeg() is not null;

    // ── 순수 함수: 특징 벡터 ────────────────────────────────────────────────

    /// <summary>
    /// RGB24 이미지(w×h) 얼굴 영역 → 특징 벡터(dst 길이 ≥ 160). 반환: 영역 평균 휘도(0~255).
    /// </summary>
    public static double ComputeFeatures(ReadOnlySpan<byte> rgb, int w, int h, FaceRegion region, Span<float> dst)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) throw new ArgumentException("RGB 버퍼 크기가 w×h×3 보다 작습니다.");
        if (dst.Length < Dimensions) throw new ArgumentException("특징 버퍼가 작습니다.", nameof(dst));
        var r = region.Clamp();
        var rx = (int)(r.X * w);
        var ry = (int)(r.Y * h);
        var rw = Math.Max(1, (int)(r.W * w));
        var rh = Math.Max(1, (int)(r.H * h));
        Span<float> gray = stackalloc float[Patch * Patch];
        dst[..Dimensions].Clear();
        double lum = 0;
        // 1) 영역 → 32x32 그레이(박스 평균) + 색상 히스토그램(전 픽셀)
        for (var py = 0; py < Patch; py++)
        {
            var y0 = ry + py * rh / Patch;
            var y1 = Math.Max(y0 + 1, ry + (py + 1) * rh / Patch);
            for (var px = 0; px < Patch; px++)
            {
                var x0 = rx + px * rw / Patch;
                var x1 = Math.Max(x0 + 1, rx + (px + 1) * rw / Patch);
                double acc = 0;
                var n = 0;
                for (var y = y0; y < y1 && y < h; y++)
                    for (var x = x0; x < x1 && x < w; x++)
                    {
                        var o = (y * w + x) * 3;
                        double R = rgb[o], G = rgb[o + 1], B = rgb[o + 2];
                        acc += 0.299 * R + 0.587 * G + 0.114 * B;
                        n++;
                        AccumulateColor(R, G, B, dst.Slice(HogDims, ColorDims));
                    }
                var v = n > 0 ? (float)(acc / n) : 0f;
                gray[py * Patch + px] = v;
                lum += v;
            }
        }
        lum /= Patch * Patch;
        // 2) 기울기 방향 히스토그램(4x4 셀 · 8방향 · 크기 가중)
        const int cell = Patch / 4;
        for (var y = 1; y < Patch - 1; y++)
            for (var x = 1; x < Patch - 1; x++)
            {
                var gx = gray[y * Patch + x + 1] - gray[y * Patch + x - 1];
                var gy = gray[(y + 1) * Patch + x] - gray[(y - 1) * Patch + x];
                var mag = MathF.Sqrt(gx * gx + gy * gy);
                if (mag < 1e-3f) continue;
                var ang = MathF.Atan2(gy, gx);              // −π..π
                if (ang < 0) ang += MathF.PI;               // 무방향(0..π)
                var bin = Math.Min(7, (int)(ang / MathF.PI * 8));
                var c = (y / cell) * 4 + x / cell;
                dst[c * 8 + bin] += mag;
            }
        for (var c = 0; c < 16; c++) L2(dst.Slice(c * 8, 8));
        L2(dst.Slice(HogDims, ColorDims));
        return lum;
    }

    private static void AccumulateColor(double r, double g, double b, Span<float> hist)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (max < 24) return;                     // 너무 어두운 픽셀은 색상 신뢰 불가
        var s = d / max;
        double hue;
        if (d < 1e-6) hue = 0;
        else if (max == r) hue = ((g - b) / d + 6) % 6;
        else if (max == g) hue = (b - r) / d + 2;
        else hue = (r - g) / d + 4;
        var hb = Math.Min(15, (int)(hue / 6 * 16));
        hist[hb * 2 + (s >= 0.35 ? 1 : 0)] += 1f;
    }

    private static void L2(Span<float> v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        if (s <= 1e-12) return;
        var k = (float)(1 / Math.Sqrt(s));
        for (var i = 0; i < v.Length; i++) v[i] *= k;
    }

    /// <summary>코사인 유사도(−1~1 · 길이 불일치/영벡터 0).</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 1e-12 || nb <= 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>신뢰도 계산(순수) — 코사인 0.6→0, 1.0→100 선형 · dHash 유사도(0~100) 가중.</summary>
    public static IdentityScore Score(double cosine, double hashSimilarity)
    {
        var cosPart = Math.Clamp((cosine - 0.6) / 0.4, 0, 1) * 100;
        var conf = Math.Round(0.7 * cosPart + 0.3 * Math.Clamp(hashSimilarity, 0, 100), 1);
        var verdict = conf >= LockedThreshold ? IdentityVerdict.Locked : conf >= DriftThreshold ? IdentityVerdict.Drifting : IdentityVerdict.Lost;
        return new IdentityScore(conf, Math.Round(cosine, 4), Math.Round(hashSimilarity, 1), verdict);
    }

    /// <summary>얼굴 영역 dHash(밝기 정규화 · 기존 v5.7 분석기 재사용).</summary>
    public static ulong RegionHash(ReadOnlySpan<byte> rgb, int w, int h, FaceRegion region)
    {
        var r = region.Clamp();
        var rx = (int)(r.X * w);
        var ry = (int)(r.Y * h);
        var rw = Math.Max(1, (int)(r.W * w));
        var rh = Math.Max(1, (int)(r.H * h));
        Span<byte> g = stackalloc byte[IdentityHashAnalyzer.Cols * IdentityHashAnalyzer.Rows];
        for (var y = 0; y < IdentityHashAnalyzer.Rows; y++)
            for (var x = 0; x < IdentityHashAnalyzer.Cols; x++)
            {
                var sx = Math.Min(w - 1, rx + (x * 2 + 1) * rw / (IdentityHashAnalyzer.Cols * 2));
                var sy = Math.Min(h - 1, ry + (y * 2 + 1) * rh / (IdentityHashAnalyzer.Rows * 2));
                var o = (sy * w + sx) * 3;
                g[y * IdentityHashAnalyzer.Cols + x] = (byte)((rgb[o] * 299 + rgb[o + 1] * 587 + rgb[o + 2] * 114) / 1000);
            }
        return IdentityHashAnalyzer.ComputeNormalizedDHash(g);
    }

    /// <summary>두 RGB 이미지 직접 비교(순수 · 테스트/라이브 프리뷰).</summary>
    public static IdentityScore Compare(ReadOnlySpan<byte> refRgb, int rw, int rh, FaceRegion refRegion, ReadOnlySpan<byte> rgb, int w, int h, FaceRegion region)
    {
        Span<float> a = stackalloc float[Dimensions];
        Span<float> b = stackalloc float[Dimensions];
        ComputeFeatures(refRgb, rw, rh, refRegion, a);
        ComputeFeatures(rgb, w, h, region, b);
        var hs = IdentityHashAnalyzer.SimilarityPercent(RegionHash(refRgb, rw, rh, refRegion), RegionHash(rgb, w, h, region));
        return Score(Cosine(a, b), hs);
    }

    // ── 생성 조건부 ──────────────────────────────────────────────────────────

    /// <summary>프롬프트 · 네거티브에 정체성 구문 주입(멱등 · 순수).</summary>
    public static (string Prompt, string Negative) ApplyPrompt(string? prompt, string? negative)
    {
        var p = (prompt ?? "").Trim();
        if (!p.Contains(IdentityPrompt, StringComparison.OrdinalIgnoreCase)) p = p.Length == 0 ? IdentityPrompt : p.TrimEnd(',', ' ') + ", " + IdentityPrompt;
        return (p, MergeTokens(negative, IdentityNegatives));
    }

    /// <summary>중복 없이 토큰 결합(순서 유지 · 순수).</summary>
    public static string MergeTokens(string? baseText, IReadOnlyList<string> tokens)
    {
        var b = (baseText ?? "").Trim().TrimEnd(',');
        var sb = new StringBuilder(b.Length + tokens.Count * 20).Append(b);
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t) || b.Contains(t, StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// ComfyUI API 워크플로에 정체성 어댑터 결선(제자리 · 순수). KSampler 의 model 입력을 어댑터 출력으로 교체. 반환: 추가 노드 수(0 = 미적용).
    ///   InstantID: InstantIDModelLoader + InstantIDFaceAnalysis + ApplyInstantID(positive/negative 도 경유) · PuLID: PulidModelLoader + PulidEvaClipLoader + PulidInsightFaceLoader + ApplyPulid.
    /// </summary>
    public static int InjectComfyWorkflow(JsonObject workflow, IdentityConditioning cond, string referenceImageName)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (cond.Adapter == IdentityAdapter.None || string.IsNullOrWhiteSpace(referenceImageName)) return 0;
        string? samplerId = null;
        foreach (var kv in workflow)
            if (kv.Value is JsonObject n && (string?)n["class_type"] == "KSampler") { samplerId = kv.Key; break; }
        if (samplerId is null || workflow[samplerId]!["inputs"] is not JsonObject sIn) return 0;
        if (sIn["model"] is not JsonArray modelRef) return 0;
        var next = NextId(workflow);
        string Id() => (next++).ToString(CultureInfo.InvariantCulture);
        JsonArray Ref(string id, int slot) => new(id, slot);
        var weight = Math.Clamp(cond.Weight, 0, 1.5);
        var start = Math.Clamp(cond.StartAt, 0, 1);
        var end = Math.Clamp(cond.EndAt, start, 1);
        var load = Id();
        workflow[load] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = referenceImageName } };
        var model = modelRef.DeepClone();
        if (cond.Adapter == IdentityAdapter.InstantId)
        {
            var loader = Id(); var face = Id(); var apply = Id();
            workflow[loader] = new JsonObject { ["class_type"] = "InstantIDModelLoader", ["inputs"] = new JsonObject { ["instantid_file"] = "ip-adapter.bin" } };
            workflow[face] = new JsonObject { ["class_type"] = "InstantIDFaceAnalysis", ["inputs"] = new JsonObject { ["provider"] = "CUDA" } };
            workflow[apply] = new JsonObject
            {
                ["class_type"] = "ApplyInstantID",
                ["inputs"] = new JsonObject
                {
                    ["instantid"] = Ref(loader, 0), ["insightface"] = Ref(face, 0), ["image"] = Ref(load, 0), ["model"] = model,
                    ["positive"] = sIn["positive"]?.DeepClone(), ["negative"] = sIn["negative"]?.DeepClone(),
                    ["weight"] = weight, ["start_at"] = start, ["end_at"] = end,
                },
            };
            sIn["model"] = Ref(apply, 0);
            sIn["positive"] = Ref(apply, 1);
            sIn["negative"] = Ref(apply, 2);
            return 4;
        }
        var pl = Id(); var eva = Id(); var ins = Id(); var ap = Id();
        workflow[pl] = new JsonObject { ["class_type"] = "PulidModelLoader", ["inputs"] = new JsonObject { ["pulid_file"] = "ip-adapter_pulid_sdxl_fp16.safetensors" } };
        workflow[eva] = new JsonObject { ["class_type"] = "PulidEvaClipLoader", ["inputs"] = new JsonObject() };
        workflow[ins] = new JsonObject { ["class_type"] = "PulidInsightFaceLoader", ["inputs"] = new JsonObject { ["provider"] = "CUDA" } };
        workflow[ap] = new JsonObject
        {
            ["class_type"] = "ApplyPulid",
            ["inputs"] = new JsonObject
            {
                ["model"] = model, ["pulid"] = Ref(pl, 0), ["eva_clip"] = Ref(eva, 0), ["face_analysis"] = Ref(ins, 0), ["image"] = Ref(load, 0),
                ["method"] = "fidelity", ["weight"] = weight, ["start_at"] = start, ["end_at"] = end,
            },
        };
        sIn["model"] = Ref(ap, 0);
        return 5;
    }

    private static int NextId(JsonObject wf)
    {
        var max = 0;
        foreach (var kv in wf) if (int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
        return max + 1;
    }

    // ── 실행(ffmpeg 디코드) ──────────────────────────────────────────────────

    /// <summary>이미지/영상(1초 지점) → 96x96 RGB24(비율 무시 박스 · 예외 0 · 실패 null).</summary>
    public async Task<byte[]?> DecodeAsync(string path, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_id_" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var args = new List<string>(20) { "-y", "-hide_banner", "-nostdin" };
            if (ext is ".mp4" or ".mov" or ".webm" or ".mkv") { args.Add("-ss"); args.Add("1"); }
            args.AddRange(new[] { "-i", path, "-vf", "scale=" + SampleSize.ToString(CultureInfo.InvariantCulture) + ":" + SampleSize.ToString(CultureInfo.InvariantCulture) + ":flags=area,format=rgb24",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", tmp });
            var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
            if (!r.Ok || !File.Exists(tmp)) return null;
            var b = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
            return b.Length >= SampleSize * SampleSize * 3 ? b : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Program.Log("IdentityLock.Decode", ex); return null; }
        finally { try { File.Delete(tmp); } catch { /* 무시 */ } }
    }

    /// <summary>등록 사진 → 특징 벡터 + dHash(실패 null).</summary>
    public async Task<(float[] Features, ulong Hash)?> ExtractAsync(string imagePath, FaceRegion region, CancellationToken ct = default)
    {
        var rgb = await DecodeAsync(imagePath, ct).ConfigureAwait(false);
        if (rgb is null) return null;
        var f = new float[Dimensions];
        ComputeFeatures(rgb, SampleSize, SampleSize, region, f);
        return (f, RegionHash(rgb, SampleSize, SampleSize, region));
    }

    /// <summary>프로필 ↔ 컷 결과(포스터/영상) 정합성(실패 None). 컷은 동일 영역 가정(I2V 구도 유지).</summary>
    public async Task<IdentityScore> ScoreAsync(CharacterProfile profile, string framePath, CancellationToken ct = default)
    {
        if (profile.Features is not { Length: Dimensions }) return IdentityScore.None;
        var rgb = await DecodeAsync(framePath, ct).ConfigureAwait(false);
        return rgb is null ? IdentityScore.None : ScoreAgainst(profile, rgb, SampleSize, SampleSize);
    }

    /// <summary>프로필 ↔ RGB 프레임 정합성(순수 · 동일 영역 가정).</summary>
    public static IdentityScore ScoreAgainst(CharacterProfile profile, ReadOnlySpan<byte> rgb, int w, int h)
    {
        if (profile.Features is not { Length: Dimensions }) return IdentityScore.None;
        Span<float> f = stackalloc float[Dimensions];
        ComputeFeatures(rgb, w, h, profile.Region, f);
        var hs = IdentityHashAnalyzer.SimilarityPercent(profile.FaceHash, RegionHash(rgb, w, h, profile.Region));
        return Score(Cosine(profile.Features, f), hs);
    }

    public static string VerdictLabel(IdentityVerdict v) => v switch
    {
        IdentityVerdict.Locked => "🔒 고정",
        IdentityVerdict.Drifting => "⚠ 드리프트",
        IdentityVerdict.Lost => "❌ 이탈",
        _ => "—",
    };
}
