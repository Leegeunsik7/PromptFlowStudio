using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.DigitalTwin;

[JsonConverter(typeof(JsonStringEnumConverter<TwinHairStyle>))]
public enum TwinHairStyle { Bald, Short, Medium, Long }

[JsonConverter(typeof(JsonStringEnumConverter<TwinBodyBuild>))]
public enum TwinBodyBuild { Slim, Average, Broad }

/// <summary>ITA(개인 유형각) 기반 피부 톤 5단계.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TwinSkinTone>))]
public enum TwinSkinTone { Fair, Light, Medium, Tan, Deep }

/// <summary>3DMM 형상 계수 인덱스(0~1 정규화).</summary>
public static class TwinShape
{
    public const int Count = 8;
    public const int Aspect = 0, Forehead = 1, Cheek = 2, Jaw = 3, Chin = 4, NoseDepth = 5, EyeSpacing = 6, Symmetry = 7;
    public static readonly string[] Labels = { "세로비", "이마폭", "광대폭", "턱폭", "턱길이", "코높이", "눈간격", "대칭" };
}

/// <summary>파라메트릭 얼굴 메시(정점 xyz · UV · 삼각형 인덱스).</summary>
public sealed class TwinMesh
{
    public const int Rows = 24, Cols = 24;
    public float[] Vertices { get; init; } = Array.Empty<float>();
    public float[] Uvs { get; init; } = Array.Empty<float>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    public int VertexCount => Vertices.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>디지털 트윈 자산 — 얼굴 영역 · 3DMM 계수 · 메시 · 알베도 텍스처 · 피부/헤어/체형 · 정체성 벡터 · 프롬프트 토큰.</summary>
public sealed class DigitalTwinAsset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "디지털 트윈";
    public string? SourcePath { get; set; }
    public int SourceFrames { get; set; } = 1;
    public FaceRegion Face { get; set; } = FaceRegion.Default;
    public float[] Shape { get; set; } = new float[TwinShape.Count];
    public string SkinHex { get; set; } = "#c8a07f";
    public TwinSkinTone SkinTone { get; set; }
    public double Ita { get; set; }
    public string HairHex { get; set; } = "#2a1f1a";
    public TwinHairStyle Hair { get; set; }
    public double HairCoverage { get; set; }
    public TwinBodyBuild Body { get; set; }
    public double ShoulderRatio { get; set; }
    public double HeadsTall { get; set; }
    public float[]? Features { get; set; }
    public ulong FaceHash { get; set; }
    public double Quality { get; set; }
    public string PromptTokens { get; set; } = "";
    public string? TexturePath { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    [JsonIgnore] public TwinMesh? Mesh { get; set; }
    [JsonIgnore] public byte[]? Texture { get; set; }
}

public sealed record TwinPackage(string Folder, string MetaPath, string ObjPath, string TexturePath);

/// <summary>테마 결합 — 테마 키 · 제목 · 점수 · 합성 프롬프트.</summary>
public sealed record TwinThemeBinding(string ThemeKey, string Title, double Score, string Prompt);

/// <summary>
/// [PromptFlow Studio v16.36.0] 🧍 실사 AI 디지털 트윈 복원 엔진(Pure BCL).
///   ▸ 사진/단편 영상(ffmpeg 프레임 ≤6 · 웹캠 스냅샷 RGB24) → YCbCr 피부 마스크 · 테두리 배경 모델 · 모멘트 기반 얼굴 영역.
///   ▸ 3DMM 근사: 행별 피부 폭(이마/광대/턱) · 턱 길이 · 음영 기반 코 높이 · 눈 간격 · 좌우 대칭 → 24×24 파라메트릭 메시(UV 포함).
///   ▸ 알베도 텍스처: 얼굴 영역 쌍선형 리샘플 + 적분 영상 저주파 휘도 분할(디라이팅) · PNG(BCL zlib) / OBJ+MTL / JSON 패키지.
///   ▸ 피부 톤(Lab ITA) · 헤어(색 · 커버리지 · 길이) · 체형(어깨/얼굴 비 · 두신) · 정체성 벡터(v16.22.0 특징 160D + dHash).
///   ▸ 다중 프레임 융합(품질 가중) · 204+ 테마 결합(TF-IDF 추천) · 컷 프롬프트 멱등 주입 · CharacterProfile 변환.
/// </summary>
public sealed class AiDigitalTwinGeneratorEngine
{
    public const int AnalysisSize = 128, TextureSize = 128, MaxVideoFrames = 6;

    private static readonly string[] Negatives = { "different person", "face swap", "plastic skin", "waxy skin", "deformed face", "uncanny valley" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Func<string?> _ffmpeg;
    private readonly SmartThemeRecommender? _themes;
    private readonly string _folder;
    private readonly object _gate = new();
    private readonly List<DigitalTwinAsset> _assets = new();
    private int _busy;

    public AiDigitalTwinGeneratorEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(IVideoMergeService)) is IVideoMergeService m ? () => m.ResolvedFfmpegPath : () => null, sp.GetService(typeof(SmartThemeRecommender)) as SmartThemeRecommender) { }

    public AiDigitalTwinGeneratorEngine(Func<string?> ffmpeg, SmartThemeRecommender? themes = null, string? folder = null)
    {
        _ffmpeg = ffmpeg;
        _themes = themes;
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "DigitalTwins");
    }

    public event Action? OnChange;

    public string Folder => _folder;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public DigitalTwinAsset? Current { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<DigitalTwinAsset> Assets { get { lock (_gate) return _assets.ToArray(); } }
    public int CatalogSize { get { try { return _themes?.AllThemes.Count ?? 0; } catch (Exception ex) { Program.Log("DigitalTwin.Catalog", ex); return 0; } } }

    // ── 순수 분석 ─────────────────────────────────────────────────────────────

    /// <summary>YCbCr 피부 판정(정수 근사 · Y&gt;40 · Cb 77~127 · Cr 133~173).</summary>
    public static bool IsSkin(byte r, byte g, byte b)
    {
        var y = (77 * r + 150 * g + 29 * b) >> 8;
        var cb = ((-43 * r - 85 * g + 128 * b) >> 8) + 128;
        var cr = ((128 * r - 107 * g - 21 * b) >> 8) + 128;
        return y > 40 && cb is >= 77 and <= 127 && cr is >= 133 and <= 173;
    }

    /// <summary>sRGB → Lab ITA 각도(도).</summary>
    public static double ItaOf(double r, double g, double b)
    {
        static double Lin(double c) { c /= 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
        double lr = Lin(r), lg = Lin(g), lb = Lin(b);
        var y = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
        var z = (0.0193 * lr + 0.1192 * lg + 0.9505 * lb) / 1.089;
        var fy = F(y);
        var l = 116 * fy - 16;
        var bb = 200 * (fy - F(z));
        return Math.Atan2(l - 50, Math.Max(1e-3, bb)) * 180 / Math.PI;
    }

    public static TwinSkinTone ToneOf(double ita) => ita > 55 ? TwinSkinTone.Fair : ita > 41 ? TwinSkinTone.Light : ita > 28 ? TwinSkinTone.Medium : ita > 10 ? TwinSkinTone.Tan : TwinSkinTone.Deep;

    public static string ToneLabel(TwinSkinTone t) => t switch
    {
        TwinSkinTone.Fair => "아주 밝은", TwinSkinTone.Light => "밝은", TwinSkinTone.Medium => "중간", TwinSkinTone.Tan => "황갈색", _ => "깊은",
    };

    private static string ToneEnglish(TwinSkinTone t) => t switch
    {
        TwinSkinTone.Fair => "fair porcelain", TwinSkinTone.Light => "light", TwinSkinTone.Medium => "medium olive", TwinSkinTone.Tan => "warm tan", _ => "deep brown",
    };

    private static string HairEnglish(TwinHairStyle h) => h switch
    {
        TwinHairStyle.Bald => "bald head", TwinHairStyle.Short => "short", TwinHairStyle.Medium => "shoulder-length", _ => "long flowing",
    };

    private static string BodyEnglish(TwinBodyBuild b) => b switch { TwinBodyBuild.Slim => "slim", TwinBodyBuild.Broad => "broad-shouldered", _ => "average" };

    /// <summary>RGB → 대략적 색 이름(영문 프롬프트용).</summary>
    public static string ColorName(int r, int g, int b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max < 55) return "black";
        if (max - min < 25) return max > 190 ? "platinum white" : "gray";
        if (r > 150 && g > 110 && b < 110 && r - b > 70) return "blonde";
        if (r > g + 35 && r > b + 35) return max > 140 ? "auburn red" : "dark auburn";
        if (b > r && b > g) return "blue-black";
        return max > 120 ? "light brown" : "dark brown";
    }

    private static string Hex(int r, int g, int b) => "#" + r.ToString("x2", CultureInfo.InvariantCulture) + g.ToString("x2", CultureInfo.InvariantCulture) + b.ToString("x2", CultureInfo.InvariantCulture);

    /// <summary>RGB24(w×h) → 디지털 트윈(결정적 · 순수 · 메시/텍스처 포함).</summary>
    public static DigitalTwinAsset Analyze(ReadOnlySpan<byte> rgb, int w, int h, string? name = null)
    {
        if (w < 16 || h < 16 || rgb.Length < w * h * 3) throw new ArgumentException("RGB24 버퍼 크기 불일치", nameof(rgb));
        var n = w * h;
        var mask = ArrayPool<byte>.Shared.Rent(n);   // 0 배경 · 1 전경(비피부) · 2 피부
        try
        {
            // 1) 테두리 배경 모델
            long br = 0, bg = 0, bb = 0; var bc = 0;
            for (var x = 0; x < w; x++) { Acc(rgb, x, 0); Acc(rgb, x, 1); }
            for (var y = 2; y < h; y++) { Acc(rgb, 0, y); Acc(rgb, w - 1, y); }
            void Acc(ReadOnlySpan<byte> s, int x, int y) { var i = (y * w + x) * 3; br += s[i]; bg += s[i + 1]; bb += s[i + 2]; bc++; }
            int mr = (int)(br / bc), mg = (int)(bg / bc), mb = (int)(bb / bc);

            // 2) 마스크 + 상단 65% 피부 모멘트
            double sx = 0, sy = 0, sxx = 0, syy = 0; long skinTop = 0;
            var topLimit = (int)(h * 0.65);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 3;
                    byte r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                    if (IsSkin(r, g, b))
                    {
                        mask[y * w + x] = 2;
                        if (y < topLimit) { sx += x; sy += y; sxx += (double)x * x; syy += (double)y * y; skinTop++; }
                    }
                    else mask[y * w + x] = (byte)(Math.Abs(r - mr) + Math.Abs(g - mg) + Math.Abs(b - mb) > 60 ? 1 : 0);
                }

            // 3) 얼굴 영역(균일 직사각 std → 폭 = 3.46σ)
            FaceRegion face;
            var faceFound = skinTop >= n / 100;
            if (faceFound)
            {
                double cx = sx / skinTop, cy = sy / skinTop;
                var stdX = Math.Sqrt(Math.Max(1, sxx / skinTop - cx * cx));
                var stdY = Math.Sqrt(Math.Max(1, syy / skinTop - cy * cy));
                double fw = 3.46 * stdX, fh = 3.46 * stdY;
                face = new FaceRegion((float)((cx - fw / 2) / w), (float)((cy - fh / 2) / h), (float)(fw / w), (float)(fh / h)).Clamp();
            }
            else face = FaceRegion.Default;
            int fx0 = (int)(face.X * w), fy0 = (int)(face.Y * h), fW = Math.Max(4, (int)(face.W * w)), fH = Math.Max(4, (int)(face.H * h));
            int fx1 = Math.Min(w, fx0 + fW), fy1 = Math.Min(h, fy0 + fH);

            // 4) 3DMM 형상 계수
            var shape = new float[TwinShape.Count];
            var maxRow = 1;
            Span<int> rowW = stackalloc int[3];
            Span<double> rowAt = stackalloc double[] { 0.22, 0.5, 0.8 };
            for (var k = 0; k < 3; k++) { rowW[k] = SkinWidth(mask, w, fx0, fx1, fy0 + (int)(fH * rowAt[k])); maxRow = Math.Max(maxRow, rowW[k]); }
            shape[TwinShape.Aspect] = Math.Clamp((float)(fH / (double)fW / 2.0), 0, 1);
            shape[TwinShape.Forehead] = rowW[0] / (float)maxRow;
            shape[TwinShape.Cheek] = rowW[1] / (float)maxRow;
            shape[TwinShape.Jaw] = rowW[2] / (float)maxRow;
            var narrowRows = 0;
            for (var y = fy0 + fH / 2; y < fy1; y++) if (SkinWidth(mask, w, fx0, fx1, y) < maxRow * 0.6) narrowRows++;
            shape[TwinShape.Chin] = Math.Clamp(narrowRows / (float)Math.Max(1, fH / 2), 0, 1);

            // 휘도 통계(얼굴) · 코 높이(중앙열 수직 기울기 에너지) · 눈 간격(눈 띠 최암 열)
            double lumSum = 0, lumSq = 0; long lumN = 0, skinIn = 0; double sr = 0, sg = 0, sb = 0;
            for (var y = fy0; y < fy1; y++)
                for (var x = fx0; x < fx1; x++)
                {
                    var i = (y * w + x) * 3;
                    var l = 0.299 * rgb[i] + 0.587 * rgb[i + 1] + 0.114 * rgb[i + 2];
                    lumSum += l; lumSq += l * l; lumN++;
                    if (mask[y * w + x] == 2) { skinIn++; sr += rgb[i]; sg += rgb[i + 1]; sb += rgb[i + 2]; }
                }
            var lumMean = lumSum / Math.Max(1, lumN);
            var lumStd = Math.Sqrt(Math.Max(0, lumSq / Math.Max(1, lumN) - lumMean * lumMean));
            double grad = 0; var midX = (fx0 + fx1) / 2; var gN = 0;
            for (var y = fy0 + fH / 3; y < fy0 + fH * 2 / 3 - 1 && y + 1 < h; y++)
            {
                var i0 = (y * w + midX) * 3; var i1 = ((y + 1) * w + midX) * 3;
                grad += Math.Abs(rgb[i1] + rgb[i1 + 1] - rgb[i0] - rgb[i0 + 1]); gN++;
            }
            shape[TwinShape.NoseDepth] = Math.Clamp((float)(grad / Math.Max(1, gN) / 40.0), 0, 1);
            var eyeY = fy0 + (int)(fH * 0.4);
            int darkL = DarkestCol(rgb, w, h, fx0, midX, eyeY), darkR = DarkestCol(rgb, w, h, midX, fx1, eyeY);
            shape[TwinShape.EyeSpacing] = Math.Clamp((darkR - darkL) / (float)fW, 0, 1);
            double asym = 0; var aN = 0;
            for (var y = fy0; y < fy1; y++)
            {
                int l = -1, r = -1;
                for (var x = fx0; x < fx1; x++) if (mask[y * w + x] == 2) { if (l < 0) l = x; r = x; }
                if (l < 0) continue;
                asym += Math.Abs((l + r) / 2.0 - (fx0 + fx1) / 2.0) / fW; aN++;
            }
            shape[TwinShape.Symmetry] = Math.Clamp(1f - (float)(asym / Math.Max(1, aN) * 4), 0, 1);

            // 5) 피부 톤
            int skr = 200, skg = 160, skb = 127;
            if (skinIn > 0) { skr = (int)(sr / skinIn); skg = (int)(sg / skinIn); skb = (int)(sb / skinIn); }
            var ita = ItaOf(skr, skg, skb);

            // 6) 헤어(얼굴 위 0.5H 띠 + 측면 띠) · 7) 체형(얼굴 아래 전경 최대 폭 · 두신)
            long hairPx = 0, hairBand = 0, sideHair = 0, sideBand = 0; double hr = 0, hg = 0, hb = 0;
            for (var y = Math.Max(0, fy0 - fH / 2); y < fy0 + fH / 6 && y < h; y++)
                for (var x = fx0; x < fx1; x++)
                {
                    hairBand++;
                    if (mask[y * w + x] != 1) continue;
                    hairPx++; var i = (y * w + x) * 3; hr += rgb[i]; hg += rgb[i + 1]; hb += rgb[i + 2];
                }
            int hcr = 40, hcg = 30, hcb = 26;
            if (hairPx > 0) { hcr = (int)(hr / hairPx); hcg = (int)(hg / hairPx); hcb = (int)(hb / hairPx); }
            var sideW = Math.Max(2, fW / 4);
            for (var y = fy0 + fH / 2; y < Math.Min(h, fy1 + fH / 2); y++)
            {
                for (var x = Math.Max(0, fx0 - sideW); x < fx0; x++) Side(rgb, x, y, ref sideHair, ref sideBand);
                for (var x = fx1; x < Math.Min(w, fx1 + sideW); x++) Side(rgb, x, y, ref sideHair, ref sideBand);
            }
            void Side(ReadOnlySpan<byte> s, int x, int y, ref long hit, ref long band)
            {
                band++;
                var i = (y * w + x) * 3;
                if (mask[y * w + x] == 1 && Math.Abs(s[i] - hcr) + Math.Abs(s[i + 1] - hcg) + Math.Abs(s[i + 2] - hcb) < 70) hit++;
            }
            var coverage = hairBand == 0 ? 0 : hairPx / (double)hairBand;
            var side = sideBand == 0 ? 0 : sideHair / (double)sideBand;
            var hair = coverage < 0.15 ? TwinHairStyle.Bald : side > 0.35 ? TwinHairStyle.Long : side > 0.15 ? TwinHairStyle.Medium : TwinHairStyle.Short;

            var maxBody = 0; var lowest = fy1;
            for (var y = Math.Min(h, fy1 + fH / 4); y < h; y++)
            {
                var cnt = 0;
                for (var x = 0; x < w; x++) if (mask[y * w + x] != 0) cnt++;
                if (cnt > maxBody) maxBody = cnt;
                if (cnt > fW / 2) lowest = y;
            }
            var shoulder = maxBody / (double)fW;
            var body = maxBody == 0 ? TwinBodyBuild.Average : shoulder < 2.2 ? TwinBodyBuild.Slim : shoulder < 2.9 ? TwinBodyBuild.Average : TwinBodyBuild.Broad;
            var heads = Math.Clamp((lowest - fy0) / (double)fH, 1, 8);

            // 8) 정체성 벡터 · 9) 품질
            var feats = new float[CharacterIdentityLockService.Dimensions];
            CharacterIdentityLockService.ComputeFeatures(rgb, w, h, face, feats);
            var hash = CharacterIdentityLockService.RegionHash(rgb, w, h, face);
            var skinRatio = skinIn / (double)Math.Max(1, lumN);
            var area = face.W * face.H;
            var sizeScore = area < 0.03 ? area / 0.03 : area > 0.5 ? Math.Max(0, 1 - (area - 0.5) * 2) : 1;
            var quality = !faceFound ? 15 : Math.Round(100 * (0.4 * Math.Min(1, skinRatio / 0.7) + 0.25 * sizeScore + 0.2 * Math.Min(1, lumStd / 40) + 0.15 * shape[TwinShape.Symmetry]), 1);

            var asset = new DigitalTwinAsset
            {
                Name = string.IsNullOrWhiteSpace(name) ? "디지털 트윈" : name.Trim(),
                Face = face, Shape = shape, SkinHex = Hex(skr, skg, skb), Ita = Math.Round(ita, 2), SkinTone = ToneOf(ita),
                HairHex = Hex(hcr, hcg, hcb), Hair = hair, HairCoverage = Math.Round(coverage, 3), Body = body, ShoulderRatio = Math.Round(shoulder, 3), HeadsTall = Math.Round(heads, 2),
                Features = feats, FaceHash = hash, Quality = quality,
            };
            asset.PromptTokens = ComposeTokens(asset, ColorName(hcr, hcg, hcb));
            asset.Mesh = BuildMesh(shape);
            asset.Texture = BuildAlbedo(rgb, w, h, face);
            return asset;
        }
        finally { ArrayPool<byte>.Shared.Return(mask); }
    }

    private static int SkinWidth(byte[] mask, int w, int x0, int x1, int y)
    {
        if (y < 0 || y * w >= mask.Length) return 0;
        var c = 0;
        for (var x = x0; x < x1; x++) if (mask[y * w + x] == 2) c++;
        return c;
    }

    private static int DarkestCol(ReadOnlySpan<byte> rgb, int w, int h, int x0, int x1, int y)
    {
        var best = x0; var min = int.MaxValue;
        for (var x = x0; x < x1; x++)
        {
            var s = 0;
            for (var dy = -1; dy <= 1; dy++)
            {
                var yy = Math.Clamp(y + dy, 0, h - 1); var i = (yy * w + x) * 3;
                s += rgb[i] + rgb[i + 1] + rgb[i + 2];
            }
            if (s < min) { min = s; best = x; }
        }
        return best;
    }

    private static string ComposeTokens(DigitalTwinAsset a, string hairColor)
    {
        var face = a.Shape[TwinShape.Jaw] < 0.75f ? "heart-shaped face" : a.Shape[TwinShape.Aspect] > 0.75f ? "oval face" : "round face";
        var hair = a.Hair == TwinHairStyle.Bald ? "bald head" : HairEnglish(a.Hair) + " " + hairColor + " hair";
        return "photorealistic digital twin of the reference person, " + ToneEnglish(a.SkinTone) + " skin tone, natural skin pores, " + hair + ", " + face + ", " + BodyEnglish(a.Body) + " build, identical facial geometry";
    }

    /// <summary>형상 계수 → 24×24 정면 타원체 메시(결정적 · 코/광대 융기).</summary>
    public static TwinMesh BuildMesh(ReadOnlySpan<float> shape)
    {
        const int R = TwinMesh.Rows, C = TwinMesh.Cols;
        var v = new float[R * C * 3];
        var uv = new float[R * C * 2];
        var idx = new int[(R - 1) * (C - 1) * 6];
        var aspect = 1.1f + shape[TwinShape.Aspect] * 0.6f;
        for (var r = 0; r < R; r++)
        {
            var t = r / (float)(R - 1);
            // 이마(0.22) → 광대(0.5) → 턱(0.8) → 턱끝 폭 프로파일
            var half = t < 0.22f ? Lerp(0.7f, shape[TwinShape.Forehead], t / 0.22f)
                : t < 0.5f ? Lerp(shape[TwinShape.Forehead], shape[TwinShape.Cheek], (t - 0.22f) / 0.28f)
                : t < 0.8f ? Lerp(shape[TwinShape.Cheek], shape[TwinShape.Jaw], (t - 0.5f) / 0.3f)
                : Lerp(shape[TwinShape.Jaw], 0.25f + 0.2f * (1 - shape[TwinShape.Chin]), (t - 0.8f) / 0.2f);
            half = Math.Max(0.15f, half) * 0.5f;
            for (var c = 0; c < C; c++)
            {
                var s = c / (float)(C - 1) * 2 - 1;
                var i = r * C + c;
                var z = MathF.Sqrt(Math.Max(0, 1 - s * s)) * 0.45f;
                var dn = (t - 0.55f) / 0.12f;
                z += (0.08f + 0.12f * shape[TwinShape.NoseDepth]) * MathF.Exp(-(s * s * 30) - dn * dn);           // 코
                var dc = (t - 0.5f) / 0.1f;
                z += 0.04f * MathF.Exp(-((MathF.Abs(s) - 0.55f) * (MathF.Abs(s) - 0.55f) * 40) - dc * dc);  // 광대
                v[i * 3] = s * half;
                v[i * 3 + 1] = (0.5f - t) * aspect * 0.5f;
                v[i * 3 + 2] = z;
                uv[i * 2] = (s + 1) / 2;
                uv[i * 2 + 1] = 1 - t;
            }
        }
        var k = 0;
        for (var r = 0; r < R - 1; r++)
            for (var c = 0; c < C - 1; c++)
            {
                var a = r * C + c;
                idx[k++] = a; idx[k++] = a + C; idx[k++] = a + 1;
                idx[k++] = a + 1; idx[k++] = a + C; idx[k++] = a + C + 1;
            }
        return new TwinMesh { Vertices = v, Uvs = uv, Indices = idx };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);

    /// <summary>얼굴 영역(15% 확장) → 128² 알베도(쌍선형 · 적분 영상 박스 블러 휘도 분할 디라이팅).</summary>
    public static byte[] BuildAlbedo(ReadOnlySpan<byte> rgb, int w, int h, FaceRegion face)
    {
        const int S = TextureSize, Rad = 8;
        var tex = new byte[S * S * 3];
        float ex = face.W * 0.15f, ey = face.H * 0.15f;
        float x0 = (face.X - ex) * w, y0 = (face.Y - ey) * h, sw = (face.W + 2 * ex) * w, sh = (face.H + 2 * ey) * h;
        for (var y = 0; y < S; y++)
            for (var x = 0; x < S; x++)
            {
                var fx = Math.Clamp(x0 + (x + 0.5f) * sw / S - 0.5f, 0, w - 1.001f);
                var fy = Math.Clamp(y0 + (y + 0.5f) * sh / S - 0.5f, 0, h - 1.001f);
                int ix = (int)fx, iy = (int)fy; float ax = fx - ix, ay = fy - iy;
                var o = (y * S + x) * 3;
                for (var ch = 0; ch < 3; ch++)
                {
                    float p00 = rgb[(iy * w + ix) * 3 + ch], p10 = rgb[(iy * w + ix + 1) * 3 + ch], p01 = rgb[((iy + 1) * w + ix) * 3 + ch], p11 = rgb[((iy + 1) * w + ix + 1) * 3 + ch];
                    tex[o + ch] = (byte)Math.Clamp((p00 * (1 - ax) + p10 * ax) * (1 - ay) + (p01 * (1 - ax) + p11 * ax) * ay + 0.5f, 0, 255);
                }
            }
        var sat = ArrayPool<long>.Shared.Rent((S + 1) * (S + 1));
        try
        {
            Array.Clear(sat, 0, (S + 1) * (S + 1));
            long total = 0;
            for (var y = 0; y < S; y++)
            {
                long row = 0;
                for (var x = 0; x < S; x++)
                {
                    var o = (y * S + x) * 3;
                    long l = (77 * tex[o] + 150 * tex[o + 1] + 29 * tex[o + 2]) >> 8;
                    row += l; total += l;
                    sat[(y + 1) * (S + 1) + x + 1] = sat[y * (S + 1) + x + 1] + row;
                }
            }
            var mean = total / (double)(S * S);
            for (var y = 0; y < S; y++)
                for (var x = 0; x < S; x++)
                {
                    int ya = Math.Max(0, y - Rad), yb = Math.Min(S, y + Rad + 1), xa = Math.Max(0, x - Rad), xb = Math.Min(S, x + Rad + 1);
                    var sum = sat[yb * (S + 1) + xb] - sat[ya * (S + 1) + xb] - sat[yb * (S + 1) + xa] + sat[ya * (S + 1) + xa];
                    var blur = sum / (double)((yb - ya) * (xb - xa));
                    var gain = Math.Clamp(mean / Math.Max(1, blur), 0.7, 1.4);
                    var o = (y * S + x) * 3;
                    for (var ch = 0; ch < 3; ch++) tex[o + ch] = (byte)Math.Clamp(tex[o + ch] * gain + 0.5, 0, 255);
                }
        }
        finally { ArrayPool<long>.Shared.Return(sat); }
        return tex;
    }

    /// <summary>다중 프레임 융합 — 최고 품질 프레임 기반 · 형상/정체성 품질 가중 평균 · 프레임 수 보너스.</summary>
    public static DigitalTwinAsset Fuse(IReadOnlyList<DigitalTwinAsset> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("프레임 없음", nameof(frames));
        var best = frames[0];
        foreach (var f in frames) if (f.Quality > best.Quality) best = f;
        if (frames.Count == 1) return best;
        var shape = new float[TwinShape.Count];
        var feats = new float[CharacterIdentityLockService.Dimensions];
        double wsum = 0;
        foreach (var f in frames)
        {
            var wgt = Math.Max(1, f.Quality);
            wsum += wgt;
            for (var i = 0; i < shape.Length; i++) shape[i] += (float)(f.Shape[i] * wgt);
            if (f.Features is { } ff) for (var i = 0; i < feats.Length && i < ff.Length; i++) feats[i] += (float)(ff[i] * wgt);
        }
        for (var i = 0; i < shape.Length; i++) shape[i] = (float)(shape[i] / wsum);
        double norm = 0;
        foreach (var x in feats) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm > 1e-9) for (var i = 0; i < feats.Length; i++) feats[i] = (float)(feats[i] / norm);
        best.Shape = shape;
        best.Features = feats;
        best.SourceFrames = frames.Count;
        best.Quality = Math.Min(100, Math.Round(best.Quality + Math.Min(10, 2 * (frames.Count - 1)), 1));
        best.Mesh = BuildMesh(shape);
        best.PromptTokens = ComposeTokens(best, ColorName(Convert.ToInt32(best.HairHex.Substring(1, 2), 16), Convert.ToInt32(best.HairHex.Substring(3, 2), 16), Convert.ToInt32(best.HairHex.Substring(5, 2), 16)));
        return best;
    }

    // ── 프롬프트 · 프로필 · 테마 ──────────────────────────────────────────────

    /// <summary>컷 프롬프트/네거티브에 트윈 토큰 주입(멱등).</summary>
    public static void ApplyToScene(StoryboardScene scene, DigitalTwinAsset asset)
    {
        var tokens = asset.PromptTokens.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        scene.Prompt = CharacterIdentityLockService.MergeTokens(scene.Prompt, tokens);
        scene.NegativePrompt = CharacterIdentityLockService.MergeTokens(scene.NegativePrompt, Negatives);
        if (!string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && string.IsNullOrWhiteSpace(scene.ImagePath) && IsImage(asset.SourcePath))
        {
            scene.ImagePath = asset.SourcePath;
            scene.Mode = SceneMode.ImageToVideo;
        }
    }

    private static bool IsImage(string p) => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    /// <summary>v16.22.0 캐릭터 프로필 변환(정체성 벡터 · dHash · 스타일 토큰 · 얼굴 영역).</summary>
    public static CharacterProfile ToCharacterProfile(DigitalTwinAsset a) => new()
    {
        Name = a.Name,
        FaceImagePath = a.SourcePath is { } s && File.Exists(s) && IsImage(s) ? s : a.TexturePath,
        Region = a.SourcePath is { } s2 && File.Exists(s2) && IsImage(s2) ? a.Face : new FaceRegion(0.15f, 0.15f, 0.7f, 0.7f),
        Features = a.Features,
        FaceHash = a.FaceHash,
        StyleTokens = a.PromptTokens,
        Seed = (int)(a.FaceHash & 0x7FFFFFFF),
    };

    /// <summary>테마 결합 — 질의 + 트윈 토큰 TF-IDF 추천 → 테마 시네마틱 + 트윈 프롬프트.</summary>
    public IReadOnlyList<TwinThemeBinding> BindThemes(DigitalTwinAsset asset, string? query, int top = 5)
    {
        var list = new List<TwinThemeBinding>(top);
        if (_themes is null) return list;
        try
        {
            foreach (var r in _themes.Recommend((query ?? "") + " " + asset.Name, Math.Clamp(top, 1, 20)))
            {
                var c = r.Theme.Cinematics;
                var p = r.Theme.Title + ", " + asset.PromptTokens + ", " + c.Lens + " lens, " + c.Lighting.ToString() + " lighting, " + c.Camera.ToString() + " camera";
                list.Add(new TwinThemeBinding(r.Theme.Key, r.Theme.Title, Math.Round(r.Score, 4), p));
            }
        }
        catch (Exception ex) { Program.Log("DigitalTwin.Themes", ex); }
        return list;
    }

    // ── 실행(디코드 · 생성 · 패키지) ─────────────────────────────────────────

    /// <summary>RGB24 버퍼(웹캠 스냅샷 등) → 트윈 생성 · 현재 등록.</summary>
    public DigitalTwinAsset GenerateFromRgb(byte[] rgb, int w, int h, string? name)
    {
        var a = Analyze(rgb, w, h, name);
        Register(a, "📸 스냅샷 트윈 · 품질 " + a.Quality.ToString("0", CultureInfo.InvariantCulture));
        return a;
    }

    /// <summary>사진/단편 영상 → 트윈(영상은 2fps ≤6프레임 융합 · ffmpeg 부재/실패 null).</summary>
    public async Task<DigitalTwinAsset?> GenerateAsync(string path, string? name, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        Raise();
        try
        {
            var frames = await DecodeFramesAsync(path, ct).ConfigureAwait(false);
            if (frames.Count == 0) { LastAction = FfmpegAvailable ? "⚠ 디코드 실패" : "⚠ ffmpeg 없음 — 웹캠 스냅샷을 사용하세요"; return null; }
            var list = new List<DigitalTwinAsset>(frames.Count);
            foreach (var f in frames) { ct.ThrowIfCancellationRequested(); list.Add(Analyze(f, AnalysisSize, AnalysisSize, name ?? Path.GetFileNameWithoutExtension(path))); }
            var a = Fuse(list);
            a.SourcePath = path;
            Register(a, "🧍 트윈 생성 · " + list.Count.ToString(CultureInfo.InvariantCulture) + "프레임 · 품질 " + a.Quality.ToString("0", CultureInfo.InvariantCulture));
            return a;
        }
        catch (OperationCanceledException) { LastAction = "⚠ 취소됨"; return null; }
        catch (Exception ex) { Program.Log("DigitalTwin.Generate", ex); LastAction = "⚠ 생성 실패: " + ex.Message; return null; }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    private async Task<List<byte[]>> DecodeFramesAsync(string path, CancellationToken ct)
    {
        var result = new List<byte[]>();
        var exe = _ffmpeg();
        if (exe is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_twin_" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            var video = Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi";
            var size = AnalysisSize.ToString(CultureInfo.InvariantCulture);
            var vf = (video ? "fps=2," : "") + "scale=" + size + ":" + size + ":flags=area,format=rgb24";
            var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-i", path, "-vf", vf, "-frames:v", (video ? MaxVideoFrames : 1).ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "rgb24", tmp };
            var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
            if (!r.Ok || !File.Exists(tmp)) return result;
            var all = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
            const int frame = AnalysisSize * AnalysisSize * 3;
            for (var o = 0; o + frame <= all.Length && result.Count < MaxVideoFrames; o += frame) result.Add(all.AsSpan(o, frame).ToArray());
            return result;
        }
        finally { try { File.Delete(tmp); } catch { /* 무시 */ } }
    }

    private void Register(DigitalTwinAsset a, string action)
    {
        lock (_gate)
        {
            _assets.Insert(0, a);
            if (_assets.Count > 12) _assets.RemoveAt(_assets.Count - 1);
        }
        Current = a;
        LastAction = action;
        Raise();
    }

    public void Select(string id)
    {
        lock (_gate) foreach (var a in _assets) if (a.Id == id) { Current = a; break; }
        Raise();
    }

    /// <summary>패키지 내보내기 — twin.json · mesh.obj/mtl · albedo.png(폴더명 안전화).</summary>
    public TwinPackage ExportPackage(DigitalTwinAsset a, string? root = null)
    {
        var safe = new StringBuilder(a.Name.Length);
        foreach (var ch in a.Name) safe.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        var dir = Path.Combine(root ?? _folder, safe + "_" + a.Id);
        Directory.CreateDirectory(dir);
        var tex = Path.Combine(dir, "albedo.png");
        if (a.Texture is { } t) File.WriteAllBytes(tex, EncodePng(t, TextureSize, TextureSize));
        a.TexturePath = tex;
        var mesh = a.Mesh ?? BuildMesh(a.Shape);
        var obj = Path.Combine(dir, "mesh.obj");
        File.WriteAllText(Path.Combine(dir, "mesh.mtl"), "newmtl twin\nKd 1 1 1\nmap_Kd albedo.png\n");
        File.WriteAllText(obj, ToObj(mesh));
        var meta = Path.Combine(dir, "twin.json");
        var tmp = meta + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(a, Json));
        File.Move(tmp, meta, true);
        LastAction = "📦 패키지 " + dir;
        Raise();
        return new TwinPackage(dir, meta, obj, tex);
    }

    /// <summary>패키지 로드(twin.json · 메시 재구성 · 폴더 밖 경로 차단 · 실패 null).</summary>
    public DigitalTwinAsset? LoadPackage(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var root = Path.GetFullPath(_folder);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            var meta = Path.Combine(full, "twin.json");
            if (!File.Exists(meta)) return null;
            var a = JsonSerializer.Deserialize<DigitalTwinAsset>(File.ReadAllText(meta), Json);
            if (a is null || a.Shape.Length != TwinShape.Count) return null;
            a.Mesh = BuildMesh(a.Shape);
            Register(a, "📂 트윈 로드 · " + a.Name);
            return a;
        }
        catch (Exception ex) { Program.Log("DigitalTwin.Load", ex); return null; }
    }

    public IReadOnlyList<string> ListPackages()
    {
        try { return Directory.Exists(_folder) ? Directory.GetDirectories(_folder) : Array.Empty<string>(); }
        catch (Exception ex) { Program.Log("DigitalTwin.List", ex); return Array.Empty<string>(); }
    }

    public static string ToObj(TwinMesh m)
    {
        var sb = new StringBuilder(m.VertexCount * 60 + m.TriangleCount * 30);
        sb.Append("# PromptFlow Studio v16.36.0 digital twin\nmtllib mesh.mtl\nusemtl twin\n");
        var v = m.Vertices;
        for (var i = 0; i < m.VertexCount; i++)
            sb.Append("v ").Append(v[i * 3].ToString("0.#####", CultureInfo.InvariantCulture)).Append(' ').Append(v[i * 3 + 1].ToString("0.#####", CultureInfo.InvariantCulture)).Append(' ').Append(v[i * 3 + 2].ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
        for (var i = 0; i < m.VertexCount; i++)
            sb.Append("vt ").Append(m.Uvs[i * 2].ToString("0.#####", CultureInfo.InvariantCulture)).Append(' ').Append(m.Uvs[i * 2 + 1].ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
        for (var i = 0; i < m.TriangleCount; i++)
        {
            sb.Append('f');
            for (var k = 0; k < 3; k++) { var x = m.Indices[i * 3 + k] + 1; sb.Append(' ').Append(x).Append('/').Append(x); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── PNG(BCL zlib · CRC32) ────────────────────────────────────────────────

    private static readonly uint[] CrcTable = BuildCrc();

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

    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFFu)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>RGB24 → PNG(필터 0 · zlib).</summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> rgb, int w, int h)
    {
        using var raw = new MemoryStream(h * (w * 3 + 1));
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            for (var y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb.Slice(y * w * 3, w * 3)); }
        using var png = new MemoryStream();
        png.Write(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> b4 = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        s.Write(type);
        s.Write(data);
        var crc = Crc32(data, Crc32(type)) ^ 0xFFFFFFFFu;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b4, crc);
        s.Write(b4);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("DigitalTwin.OnChange", ex); }
    }
}
