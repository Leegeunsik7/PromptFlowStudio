using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace PromptFlowStudio.Services.PathTracing;

/// <summary>
/// MaterialX <c>standard_surface</c> 부분집합 PBR 재질(선형 RGB). 유전체 F0 = specular · ((ior−1)/(ior+1))².
/// Subsurface = 표면 BSDF 대비 확산 랜덤워크 선택 확률 · SubsurfaceRadius = 평균 자유 행정(m).
/// </summary>
public struct PbrMaterial
{
    public Vector3 BaseColor;
    public float Metallic, Roughness, Specular, Ior, Subsurface, SubsurfaceRadius, Opacity;
    public Vector3 SubsurfaceColor, Emission;

    public static PbrMaterial Default => new()
    {
        BaseColor = new Vector3(0.8f), Metallic = 0, Roughness = 0.5f, Specular = 1, Ior = 1.5f,
        Subsurface = 0, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Emission = Vector3.Zero, Opacity = 1,
    };

    public readonly float DielectricF0 { get { var r = (Ior - 1) / (Ior + 1); return Specular * r * r; } }
}

public enum BsdfLobe : byte { None = 0, Diffuse, Specular }

/// <summary>재질 진단 결과(방향 반사율 · 경고).</summary>
public sealed record MaterialDiagnosis(string Name, float AlbedoNormal, float AlbedoGrazing, IReadOnlyList<string> Issues);

/// <summary>
/// [PromptFlow Studio v16.74.0] 🎨 MaterialX BSDF 지능형 재질 합성기.
///   ▸ 파싱: MaterialX 1.38 XML(<c>standard_surface</c> · <c>UsdPreviewSurface</c> · <c>surfacematerial</c> → nodename 연결) → 재질 라이브러리. 내보내기(.mtlx) 왕복.
///   ▸ BSDF(무할당 핫패스): Lambert 확산(1−metallic)(1−F) + GGX 마이크로패싯(Smith 높이 비상관 G · Schlick F · 금속 = 기저색 F0) — 평가 · 중요도 샘플링(로브 선택 + GGX 하프벡터/코사인) · 결합 pdf.
///   ▸ 피드백: 몬테카를로 방향 반사율(화이트 퍼니스) · 비물리 금속도 · 파이어플라이 위험 거칠기 · 에너지 과다 · SSS 반경 · 프롬프트 → 프리셋(금/구리/알루미늄/피부/대리석/옥/플라스틱/고무/왁스/발광).
/// </summary>
public sealed class MaterialXBsdfShaderSynthesizer
{
    public const int MaxMaterials = 64;

    private readonly object _gate = new();
    private readonly PbrMaterial[] _m = new PbrMaterial[MaxMaterials];
    private readonly string[] _names = new string[MaxMaterials];
    private int _n;
    private long _version;

    private static readonly (string[] Keys, string Name, PbrMaterial M)[] Presets =
    {
        (new[] { "gold", "금", "황금" }, "gold", new PbrMaterial { BaseColor = new(1f, 0.78f, 0.34f), Metallic = 1, Roughness = 0.25f, Specular = 1, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Opacity = 1 }),
        (new[] { "copper", "구리" }, "copper", new PbrMaterial { BaseColor = new(0.95f, 0.64f, 0.54f), Metallic = 1, Roughness = 0.3f, Specular = 1, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Opacity = 1 }),
        (new[] { "aluminum", "aluminium", "알루미늄", "chrome", "크롬", "steel", "강철" }, "aluminum", new PbrMaterial { BaseColor = new(0.91f, 0.92f, 0.92f), Metallic = 1, Roughness = 0.15f, Specular = 1, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Opacity = 1 }),
        (new[] { "skin", "피부", "얼굴" }, "skin", new PbrMaterial { BaseColor = new(0.8f, 0.55f, 0.45f), Metallic = 0, Roughness = 0.45f, Specular = 0.6f, Ior = 1.4f, Subsurface = 0.7f, SubsurfaceColor = new(0.95f, 0.6f, 0.45f), SubsurfaceRadius = 0.05f, Opacity = 1 }),
        (new[] { "marble", "대리석" }, "marble", new PbrMaterial { BaseColor = new(0.9f, 0.89f, 0.86f), Metallic = 0, Roughness = 0.2f, Specular = 1, Ior = 1.5f, Subsurface = 0.5f, SubsurfaceColor = new(0.95f, 0.94f, 0.9f), SubsurfaceRadius = 0.15f, Opacity = 1 }),
        (new[] { "jade", "옥", "비취" }, "jade", new PbrMaterial { BaseColor = new(0.35f, 0.7f, 0.45f), Metallic = 0, Roughness = 0.15f, Specular = 1, Ior = 1.6f, Subsurface = 0.8f, SubsurfaceColor = new(0.45f, 0.9f, 0.55f), SubsurfaceRadius = 0.2f, Opacity = 1 }),
        (new[] { "plastic", "플라스틱" }, "plastic", new PbrMaterial { BaseColor = new(0.8f, 0.15f, 0.12f), Metallic = 0, Roughness = 0.35f, Specular = 1, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Opacity = 1 }),
        (new[] { "rubber", "고무" }, "rubber", new PbrMaterial { BaseColor = new(0.08f, 0.08f, 0.08f), Metallic = 0, Roughness = 0.9f, Specular = 0.5f, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Opacity = 1 }),
        (new[] { "wax", "왁스", "양초", "candle" }, "wax", new PbrMaterial { BaseColor = new(0.95f, 0.88f, 0.7f), Metallic = 0, Roughness = 0.4f, Specular = 0.5f, Ior = 1.45f, Subsurface = 0.9f, SubsurfaceColor = new(0.98f, 0.9f, 0.7f), SubsurfaceRadius = 0.3f, Opacity = 1 }),
        (new[] { "emissive", "발광", "neon", "네온", "lamp", "조명" }, "emissive", new PbrMaterial { BaseColor = new(0.1f), Metallic = 0, Roughness = 0.5f, Specular = 0, Ior = 1.5f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Emission = new(6f, 5.2f, 4f), Opacity = 1 }),
    };

    public MaterialXBsdfShaderSynthesizer() => Register("default", PbrMaterial.Default);

    public event Action? OnChange;

    public int Count => _n;
    public long Version => Interlocked.Read(ref _version);
    public string Name(int i) => _names[i];
    /// <summary>핫패스 직접 접근(무할당 · 읽기 전용 사용).</summary>
    public PbrMaterial[] Items => _m;
    public ref readonly PbrMaterial Get(int i) => ref _m[i];
    public string? LastAction { get; private set; }

    public int Find(string name)
    {
        for (var i = 0; i < _n; i++) if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>재질 등록(같은 이름 = 갱신). 반환 = 인덱스(가득 차면 −1).</summary>
    public int Register(string name, in PbrMaterial m)
    {
        int i;
        lock (_gate)
        {
            i = Find(name);
            if (i < 0)
            {
                if (_n >= MaxMaterials) return -1;
                i = _n++;
                _names[i] = name;
            }
            _m[i] = Sanitize(m);
            _version++;
        }
        Raise();
        return i;
    }

    public void Set(int i, in PbrMaterial m)
    {
        lock (_gate) { _m[i] = Sanitize(m); _version++; }
        Raise();
    }

    public static PbrMaterial Sanitize(in PbrMaterial m)
    {
        var r = m;
        r.BaseColor = Vector3.Clamp(r.BaseColor, Vector3.Zero, Vector3.One);
        r.SubsurfaceColor = Vector3.Clamp(r.SubsurfaceColor, Vector3.Zero, Vector3.One);
        r.Emission = Vector3.Max(r.Emission, Vector3.Zero);
        r.Metallic = Math.Clamp(r.Metallic, 0f, 1f);
        r.Roughness = Math.Clamp(r.Roughness, 0f, 1f);
        r.Specular = Math.Clamp(r.Specular, 0f, 1f);
        r.Ior = Math.Clamp(r.Ior, 1f, 3f);
        r.Subsurface = Math.Clamp(r.Subsurface, 0f, 1f);
        r.SubsurfaceRadius = Math.Clamp(r.SubsurfaceRadius, 1e-3f, 10f);
        r.Opacity = Math.Clamp(r.Opacity, 0f, 1f);
        return r;
    }

    // ══════════════════════ MaterialX 파싱/내보내기(제어 평면) ══════════════════════

    /// <summary>MaterialX XML → 라이브러리. surfacematerial 은 연결된 셰이더 값으로 재질명 등록. 반환 = 등록 수.</summary>
    public int LoadMtlx(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("MaterialX 루트 없음");
        if (root.Name.LocalName != "materialx") throw new FormatException("<materialx> 루트가 아닙니다.");
        var shaders = new Dictionary<string, PbrMaterial>(StringComparer.Ordinal);
        foreach (var el in root.Elements())
        {
            var kind = el.Name.LocalName;
            if (kind is not ("standard_surface" or "UsdPreviewSurface")) continue;
            var nm = (string?)el.Attribute("name") ?? throw new FormatException(kind + " name 누락");
            shaders[nm] = ParseShader(el, kind == "UsdPreviewSurface");
        }
        var count = 0;
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in root.Elements().Where(e => e.Name.LocalName == "surfacematerial"))
        {
            var nm = (string?)el.Attribute("name") ?? throw new FormatException("surfacematerial name 누락");
            var link = el.Elements().FirstOrDefault(i => (string?)i.Attribute("name") == "surfaceshader");
            var node = (string?)link?.Attribute("nodename");
            if (node is null || !shaders.TryGetValue(node, out var m)) throw new FormatException("surfacematerial '" + nm + "' 의 셰이더 '" + node + "' 를 찾을 수 없습니다.");
            if (Register(nm, m) >= 0) count++;
            bound.Add(node);
        }
        foreach (var (nm, m) in shaders)
            if (!bound.Contains(nm) && Register(nm, m) >= 0) count++;
        LastAction = "📥 MaterialX 로드 · " + count.ToString(CultureInfo.InvariantCulture) + "개";
        Raise();
        return count;
    }

    private static PbrMaterial ParseShader(XElement el, bool preview)
    {
        var m = PbrMaterial.Default;
        var emissionColor = Vector3.One; var emissionWeight = preview ? 1f : 0f;
        var baseWeight = 1f;
        foreach (var input in el.Elements().Where(e => e.Name.LocalName == "input"))
        {
            var name = (string?)input.Attribute("name");
            var value = (string?)input.Attribute("value");
            if (name is null || value is null) continue;
            switch (preview ? MapPreview(name) : name)
            {
                case "base": baseWeight = F(value); break;
                case "base_color": m.BaseColor = V(value); break;
                case "metalness": m.Metallic = F(value); break;
                case "specular": m.Specular = F(value); break;
                case "specular_roughness": m.Roughness = F(value); break;
                case "specular_IOR": m.Ior = F(value); break;
                case "subsurface": m.Subsurface = F(value); break;
                case "subsurface_color": m.SubsurfaceColor = V(value); break;
                case "subsurface_radius": var r = V(value); m.SubsurfaceRadius = (r.X + r.Y + r.Z) / 3f; break;
                case "subsurface_scale": m.SubsurfaceRadius *= F(value); break;
                case "emission": emissionWeight = F(value); break;
                case "emission_color": emissionColor = V(value); break;
                case "opacity": var o = V(value); m.Opacity = (o.X + o.Y + o.Z) / 3f; break;
            }
        }
        m.BaseColor *= baseWeight;
        m.Emission = emissionColor * emissionWeight;
        if (preview && m.Emission == Vector3.One) m.Emission = Vector3.Zero;   // UsdPreviewSurface 기본 emissiveColor = 0
        return Sanitize(m);
    }

    private static string MapPreview(string n) => n switch
    {
        "diffuseColor" => "base_color", "metallic" => "metalness", "roughness" => "specular_roughness", "ior" => "specular_IOR",
        "emissiveColor" => "emission_color", "opacity" => "opacity", _ => "",
    };

    private static float F(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Vector3 V(string s)
    {
        var p = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 1) { var v = F(p[0]); return new Vector3(v); }
        if (p.Length != 3) throw new FormatException("color3/vector3 값이 아닙니다: " + s);
        return new Vector3(F(p[0]), F(p[1]), F(p[2]));
    }

    /// <summary>라이브러리 → MaterialX 1.38 XML(standard_surface + surfacematerial).</summary>
    public string ExportMtlx()
    {
        var root = new XElement("materialx", new XAttribute("version", "1.38"));
        lock (_gate)
        {
            for (var i = 0; i < _n; i++)
            {
                var m = _m[i];
                var sr = "SR_" + _names[i];
                var emis = MathF.Max(m.Emission.X, MathF.Max(m.Emission.Y, m.Emission.Z));
                root.Add(new XElement("standard_surface", new XAttribute("name", sr), new XAttribute("type", "surfaceshader"),
                    In("base_color", "color3", C(m.BaseColor)), In("metalness", "float", S(m.Metallic)), In("specular", "float", S(m.Specular)),
                    In("specular_roughness", "float", S(m.Roughness)), In("specular_IOR", "float", S(m.Ior)),
                    In("subsurface", "float", S(m.Subsurface)), In("subsurface_color", "color3", C(m.SubsurfaceColor)),
                    In("subsurface_radius", "color3", C(new Vector3(m.SubsurfaceRadius))),
                    In("emission", "float", S(emis > 0 ? emis : 0)), In("emission_color", "color3", C(emis > 0 ? m.Emission / emis : Vector3.One)),
                    In("opacity", "color3", C(new Vector3(m.Opacity)))));
                root.Add(new XElement("surfacematerial", new XAttribute("name", _names[i]), new XAttribute("type", "material"),
                    new XElement("input", new XAttribute("name", "surfaceshader"), new XAttribute("type", "surfaceshader"), new XAttribute("nodename", sr))));
            }
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root).ToString();
    }

    private static XElement In(string name, string type, string value) =>
        new("input", new XAttribute("name", name), new XAttribute("type", type), new XAttribute("value", value));

    private static string S(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string C(Vector3 v) => S(v.X) + ", " + S(v.Y) + ", " + S(v.Z);

    // ══════════════════════ BSDF(무할당) ══════════════════════

    private static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static float Alpha(in PbrMaterial m) => MathF.Max(m.Roughness * m.Roughness, 2e-3f);

    private static float SpecularPick(in PbrMaterial m) => Math.Clamp(m.Metallic + (1 - m.Metallic) * MathF.Min(1f, m.Specular * 0.5f), 0f, 1f);

    private static Vector3 Fresnel(in PbrMaterial m, float voh, out Vector3 fDiel)
    {
        var w = MathF.Pow(1 - Math.Clamp(voh, 0f, 1f), 5);
        var f0 = m.DielectricF0;
        fDiel = new Vector3(m.Specular > 0 ? f0 + (MathF.Min(1f, m.Specular) - f0) * w : 0f);
        var fMetal = m.BaseColor + (Vector3.One - m.BaseColor) * w;
        return Vector3.Lerp(fDiel, fMetal, m.Metallic);
    }

    private static float DielectricSchlick(in PbrMaterial m, float cos)
    {
        if (m.Specular <= 0) return 0;
        var f0 = m.DielectricF0;
        return f0 + (MathF.Min(1f, m.Specular) - f0) * MathF.Pow(1 - Math.Clamp(cos, 0f, 1f), 5);
    }

    private static float G1(float x, float a2) => 2 * x / (x + MathF.Sqrt(a2 + (1 - a2) * x * x));

    private static float Ggx(float noh, float a2)
    {
        var d = noh * noh * (a2 - 1) + 1;
        return a2 / (MathF.PI * d * d);
    }

    /// <summary>f(wo, wi)·cosθi (n·wo &gt; 0 · n·wi &gt; 0 아니면 0). 결합 pdf 동시 반환.</summary>
    public static Vector3 Eval(in PbrMaterial m, Vector3 n, Vector3 wo, Vector3 wi, out float pdf)
    {
        pdf = 0;
        var nol = Vector3.Dot(n, wi); var nov = Vector3.Dot(n, wo);
        if (nol <= 0 || nov <= 0) return Vector3.Zero;
        var h = Vector3.Normalize(wo + wi);
        var noh = MathF.Max(0, Vector3.Dot(n, h)); var voh = MathF.Max(1e-6f, Vector3.Dot(wo, h));
        var a = Alpha(m); var a2 = a * a;
        var F = Fresnel(m, voh, out _);
        var D = Ggx(noh, a2);
        var spec = F * (D * G1(nov, a2) * G1(nol, a2) / (4 * nov * nol));
        // 유전체 프레넬 투과(입사·출사 양방향 곱 — 상반성 유지 · 에너지 ≤ 1)
        var tIn = 1 - DielectricSchlick(m, nol); var tOut = 1 - DielectricSchlick(m, nov);
        var diff = (1 - m.Metallic) * m.BaseColor * (tIn * tOut / MathF.PI);
        var ps = SpecularPick(m);
        pdf = ps * D * noh / (4 * voh) + (1 - ps) * nol / MathF.PI;
        return (diff + spec) * nol;
    }

    public static void Basis(Vector3 n, out Vector3 t, out Vector3 b)
    {
        var s = n.Z >= 0 ? 1f : -1f;
        var a = -1f / (s + n.Z);
        var bb = n.X * n.Y * a;
        t = new Vector3(1 + s * n.X * n.X * a, s * bb, -s * n.X);
        b = new Vector3(bb, s + n.Y * n.Y * a, -n.Y);
    }

    public static Vector3 CosineSample(Vector3 n, float u1, float u2)
    {
        Basis(n, out var t, out var b);
        var r = MathF.Sqrt(u1); var phi = MathF.Tau * u2;
        return Vector3.Normalize(t * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(MathF.Max(0, 1 - u1)));
    }

    /// <summary>중요도 샘플링: 반환 false = 흡수/하반구. weight = f·cos/pdf.</summary>
    public static bool Sample(in PbrMaterial m, Vector3 n, Vector3 wo, float u0, float u1, float u2, out Vector3 wi, out Vector3 weight, out float pdf, out BsdfLobe lobe)
    {
        var ps = SpecularPick(m);
        if (u0 < ps)
        {
            var a = Alpha(m); var a2 = a * a;
            var cosT = MathF.Sqrt((1 - u2) / (1 + (a2 - 1) * u2));
            var sinT = MathF.Sqrt(MathF.Max(0, 1 - cosT * cosT));
            var phi = MathF.Tau * u1;
            Basis(n, out var t, out var b);
            var h = t * (sinT * MathF.Cos(phi)) + b * (sinT * MathF.Sin(phi)) + n * cosT;
            wi = Vector3.Reflect(-wo, h);
            lobe = BsdfLobe.Specular;
        }
        else
        {
            wi = CosineSample(n, u1, u2);
            lobe = BsdfLobe.Diffuse;
        }
        var f = Eval(m, n, wo, wi, out pdf);
        if (pdf <= 1e-8f) { weight = Vector3.Zero; lobe = BsdfLobe.None; return false; }
        weight = f / pdf;
        return true;
    }

    /// <summary>몬테카를로 방향 반사율(화이트 퍼니스 · 결정적 시드).</summary>
    public static float DirectionalAlbedo(in PbrMaterial m, float cosV, int samples, ulong seed = 0x1674)
    {
        var n = Vector3.UnitZ;
        var wo = new Vector3(MathF.Sqrt(MathF.Max(0, 1 - cosV * cosV)), 0, cosV);
        var acc = Vector3.Zero;
        var s = seed;
        for (var i = 0; i < samples; i++)
        {
            float u0 = Rnd(ref s), u1 = Rnd(ref s), u2 = Rnd(ref s);
            if (Sample(m, n, wo, u0, u1, u2, out _, out var w, out _, out _)) acc += w;
        }
        return Lum(acc / samples);
    }

    private static float Rnd(ref ulong s)
    {
        s += 0x9E3779B97F4A7C15UL;
        var z = s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
        return (z >> 40) * (1f / (1 << 24));
    }

    // ══════════════════════ 지능형 피드백 ══════════════════════

    public MaterialDiagnosis Diagnose(int i)
    {
        var m = _m[i];
        var issues = new List<string>();
        var a0 = DirectionalAlbedo(m, 1f, 4096);
        var a1 = DirectionalAlbedo(m, 0.15f, 4096);
        if (m.Metallic is > 0.1f and < 0.9f) issues.Add("금속도 " + S(m.Metallic) + " — 실재 재질은 0 또는 1(혼합 표면은 텍스처로 분리 권장)");
        if (m.Roughness < 0.05f) issues.Add("거칠기 " + S(m.Roughness) + " — 저 SPP 에서 파이어플라이 위험(≥0.05 권장)");
        if (a0 > 1.001f || a1 > 1.001f) issues.Add("방향 반사율 > 1 — 에너지 과다");
        if (Lum(m.BaseColor) > 0.9f && m.Metallic < 0.5f) issues.Add("기저색 휘도 " + S(Lum(m.BaseColor)) + " — 실재 유전체 반사율(≤0.9) 초과");
        if (Lum(m.BaseColor) < 0.02f && m.Metallic < 0.5f && m.Emission == Vector3.Zero) issues.Add("기저색이 지나치게 어두움(숯 ≈ 0.04)");
        if (m.Subsurface > 0 && m.SubsurfaceRadius < 0.005f) issues.Add("SSS 반경이 장면 스케일 대비 너무 작음 — 확산 효과 미미");
        if (m.Subsurface > 0 && m.Metallic > 0.5f) issues.Add("금속에는 서브서피스 산란이 없음");
        if (m.Opacity < 1) issues.Add("불투명도 < 1 은 현재 경로 추적기에서 무시됨(투과 미지원)");
        return new MaterialDiagnosis(_names[i], a0, a1, issues);
    }

    /// <summary>프롬프트 키워드 → 프리셋(없으면 null).</summary>
    public static (string Name, PbrMaterial Material)? SuggestFromPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        foreach (var (keys, name, m) in Presets)
            foreach (var k in keys)
                if (prompt.Contains(k, StringComparison.OrdinalIgnoreCase)) return (name, m);
        return null;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mtlx.OnChange", ex); }
    }
}
