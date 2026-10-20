using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.HairCloth;

public enum GarmentType : byte { Cape, Skirt, Dress }

public enum FabricKind : byte { Silk, Cotton, Denim, Leather, Wool, Spandex }

/// <summary>원단 물성(XPBD 컴플라이언스 m/N · 면밀도 kg/m² · 셰이딩).</summary>
public readonly record struct FabricMaterial(string Name, float Density, float StretchCompliance, float ShearCompliance, float BendCompliance,
    float Damping, float Friction, Vector3 Color, float Roughness, float Sheen, float Anisotropy);

/// <summary>
/// [PromptFlow Studio v16.83.0] 제너레이티브 의상 드레이핑 · 인체 충돌 합성기(<see cref="Step"/> 무할당).
/// 의상 = 패턴 격자 40×28(망토 = 어깨 감싸는 열린 호 ±150° · 등방 격자 · 스커트/드레스 = 닫힌 고리 · 상단 행 몸 포즈 고정) →
/// XPBD 소스텝 8(Macklin 2019 · 서브스텝 1 반복): 신축 · 전단 · 굽힘(건너뛰기 거리) 컴플라이언스 · 예측/속도 [SIMD] ·
/// 공기역학(법선 상대풍 이차 항력) · 바디 캡슐/머리 충돌 + 위치 기반 마찰 →
/// 주름: 구조 압축률 + 곡률 → 주름 강도 · 좌굴 파장 λ ∝ (굽힘 강성)^¼ 절차 주름 변위 · 변형률 스펙트럼(히스토그램 16).
/// 프롬프트(한/영) → 의상 종류 · 원단.
/// </summary>
public sealed class GenerativeGarmentClothCollisionSynthesizer
{
    public const int Cols = 40, Rows = 28, MaxVerts = Cols * Rows, MaxConstraints = MaxVerts * 6, Substeps = 8, Bins = 16;
    public const float Margin = 0.006f, StrainRange = 0.2f;

    public static readonly FabricMaterial[] Fabrics =
    {
        new("실크", 0.08f, 1e-6f, 1e-4f, 0.6f, 0.15f, 0.15f, new Vector3(0.62f, 0.12f, 0.2f), 0.25f, 0.15f, 0.8f),
        new("면", 0.15f, 1e-6f, 5e-4f, 0.08f, 0.25f, 0.45f, new Vector3(0.82f, 0.8f, 0.74f), 0.8f, 0.25f, 0.1f),
        new("데님", 0.35f, 1e-7f, 1e-4f, 0.006f, 0.3f, 0.55f, new Vector3(0.14f, 0.24f, 0.45f), 0.85f, 0.2f, 0.2f),
        new("가죽", 0.6f, 1e-7f, 5e-5f, 0.0012f, 0.35f, 0.6f, new Vector3(0.24f, 0.13f, 0.07f), 0.45f, 0.05f, 0f),
        new("울", 0.3f, 2e-6f, 1e-3f, 0.03f, 0.35f, 0.7f, new Vector3(0.5f, 0.42f, 0.32f), 0.95f, 0.6f, 0f),
        new("스판덱스", 0.2f, 0.08f, 0.02f, 0.05f, 0.2f, 0.5f, new Vector3(0.1f, 0.1f, 0.12f), 0.4f, 0.1f, 0.3f),
    };

    public float[] X = new float[MaxVerts], Y = new float[MaxVerts], Z = new float[MaxVerts];
    public float[] PX = new float[MaxVerts], PY = new float[MaxVerts], PZ = new float[MaxVerts];
    public readonly float[] VX = new float[MaxVerts], VY = new float[MaxVerts], VZ = new float[MaxVerts];
    public readonly float[] AX = new float[MaxVerts], AY = new float[MaxVerts], AZ = new float[MaxVerts], InvMass = new float[MaxVerts];
    public readonly float[] NX = new float[MaxVerts], NY = new float[MaxVerts], NZ = new float[MaxVerts];
    public readonly float[] Compression = new float[MaxVerts], Curvature = new float[MaxVerts], Wrinkle = new float[MaxVerts], WrinkleOffset = new float[MaxVerts];
    private readonly float[] _cmpH = new float[MaxVerts], _cnt = new float[MaxVerts], _arc = new float[MaxVerts];
    private readonly Vector3[] _anchorLocal = new Vector3[Cols], _anchorPrev = new Vector3[Cols], _anchorNow = new Vector3[Cols];
    private readonly int[] _ca = new int[MaxConstraints], _cb = new int[MaxConstraints];
    private readonly float[] _rest = new float[MaxConstraints];
    private readonly byte[] _kind = new byte[MaxConstraints];
    private readonly int[] _hist = new int[Bins];
    private int _nc, _nStruct;
    private float _time, _restDx, _restDy;

    public GenerativeGarmentClothCollisionSynthesizer() => Configure(GarmentType.Cape, FabricKind.Silk);

    public HumanBodyColliderRig Body { get; set; } = new();
    public GarmentType Garment { get; private set; }
    public FabricKind Fabric { get; private set; }
    public FabricMaterial Material => Fabrics[(int)Fabric];
    public bool Closed => Garment != GarmentType.Cape;
    public Vector3 Wind { get; set; }
    public float Turbulence { get; set; } = 0.35f;
    public float Gravity { get; set; } = -9.81f;
    /// <summary>사용자 배율(1 = 원단 기본): 신축 강성 · 굽힘 강성(컴플라이언스 역수 배).</summary>
    public float StretchScale { get; set; } = 1f;
    public float BendScale { get; set; } = 1f;
    public float DampingScale { get; set; } = 1f;

    public int VertexCount => MaxVerts;
    public int ConstraintCount => _nc;
    public int StructuralCount => _nStruct;
    public ReadOnlySpan<int> StrainHistogram => _hist;
    public float MaxStrain { get; private set; }
    public float MeanStrain { get; private set; }
    public float MeanWrinkle { get; private set; }
    public float WrinkleWavelength { get; private set; }
    public int LastContacts { get; private set; }
    public long Contacts { get; private set; }
    public double LastStepMs { get; private set; }
    public double MaxStepMs { get; private set; }
    public long Steps { get; private set; }
    public string? LastAction { get; private set; }

    public static int Index(int row, int col) => row * Cols + col;
    public Vector3 Position(int v) => new(X[v], Y[v], Z[v]);
    public Vector3 Normal(int v) => new(NX[v], NY[v], NZ[v]);
    public Vector3 AnchorWorld(int col) => _anchorNow[col];

    public static string GarmentName(GarmentType g) => g switch { GarmentType.Skirt => "스커트", GarmentType.Dress => "드레스", _ => "망토" };

    // ───────────────────────── 패턴 ─────────────────────────

    /// <summary>의상 · 원단 구성(무할당 · 선할당 버퍼 재사용) → 몸 포즈 기준 휴지 배치.</summary>
    public void Configure(GarmentType g, FabricKind f)
    {
        Garment = g; Fabric = f;
        float topY, rx, rz, length, flare, a0, a1;
        (topY, rx, rz, length, flare, a0, a1) = g switch
        {
            GarmentType.Skirt => (-0.78f, 0.205f, 0.165f, 0.52f, 1.3f, 0f, 2f * MathF.PI),
            GarmentType.Dress => (-0.34f, 0.19f, 0.15f, 1.0f, 1.0f, 0f, 2f * MathF.PI),
            _ => (-0.2f, 0.33f, 0.2f, 0.88f, 0.25f, -2.62f, 2.62f),
        };
        var closed = Closed;
        var span = closed ? Cols : Cols - 1;
        // 휴지 격자(몸 좌표): 열 = 둘레 각 · 행 = 아래로 · 반경 플레어
        for (var j = 0; j < Cols; j++)
        {
            var a = a0 + (a1 - a0) * j / span;
            _anchorLocal[j] = new Vector3(rx * MathF.Sin(a), topY, -rz * MathF.Cos(a));
            if (g != GarmentType.Cape) _anchorLocal[j] = new Vector3(rx * MathF.Sin(a), topY, rz * MathF.Cos(a));
        }
        var dy = length / (Rows - 1);
        var pose = Body.BodyPose;
        for (var i = 0; i < Rows; i++)
        {
            var sc = 1f + flare * i / (Rows - 1);
            for (var j = 0; j < Cols; j++)
            {
                var l = _anchorLocal[j];
                var p = Vector3.Transform(new Vector3(l.X * sc, topY - i * dy, l.Z * sc), pose);
                var v = Index(i, j);
                X[v] = PX[v] = p.X; Y[v] = PY[v] = p.Y; Z[v] = PZ[v] = p.Z;
                VX[v] = VY[v] = VZ[v] = 0f;
            }
        }
        // 정점 질량(면밀도 × 정점 면적)
        var arc = 0f;
        for (var j = 1; j < Cols; j++) arc += Vector3.Distance(_anchorLocal[j], _anchorLocal[j - 1]);
        if (closed) arc += Vector3.Distance(_anchorLocal[0], _anchorLocal[Cols - 1]);
        _restDx = arc / span; _restDy = dy;
        var mass = Fabrics[(int)f].Density * _restDx * (1f + 0.5f * flare) * dy;
        for (var i = 0; i < Rows; i++)
            for (var j = 0; j < Cols; j++)
            {
                InvMass[Index(i, j)] = i == 0 ? 0f : 1f / mass;
                _arc[Index(i, j)] = j * _restDx * (1f + flare * i / (Rows - 1));
            }
        // 제약(휴지 길이 = 초기 격자)
        _nc = 0;
        for (var i = 0; i < Rows; i++)
            for (var j = 0; j < Cols; j++)
            {
                if (j + 1 < Cols || closed) Add(Index(i, j), Index(i, (j + 1) % Cols), 0);
                if (i + 1 < Rows) Add(Index(i, j), Index(i + 1, j), 0);
            }
        _nStruct = _nc;
        for (var i = 0; i + 1 < Rows; i++)
            for (var j = 0; j < Cols; j++)
            {
                if (j + 1 >= Cols && !closed) continue;
                var j1 = (j + 1) % Cols;
                Add(Index(i, j), Index(i + 1, j1), 1); Add(Index(i, j1), Index(i + 1, j), 1);
            }
        for (var i = 0; i < Rows; i++)
            for (var j = 0; j < Cols; j++)
            {
                if (j + 2 < Cols || closed) Add(Index(i, j), Index(i, (j + 2) % Cols), 2);
                if (i + 2 < Rows) Add(Index(i, j), Index(i + 2, j), 2);
            }
        for (var j = 0; j < Cols; j++) _anchorPrev[j] = _anchorNow[j] = Vector3.Transform(_anchorLocal[j], pose);
        _time = 0f;
        Array.Clear(Compression); Array.Clear(Curvature); Array.Clear(Wrinkle); Array.Clear(WrinkleOffset); Array.Clear(_hist);
        ComputeNormals();
        LastAction = "👗 " + GarmentName(g) + " · " + Fabrics[(int)f].Name;
    }

    private void Add(int a, int b, byte kind)
    {
        if (_nc >= MaxConstraints) return;
        _ca[_nc] = a; _cb[_nc] = b; _kind[_nc] = kind;
        _rest[_nc] = Vector3.Distance(Position(a), Position(b));
        _nc++;
    }

    /// <summary>프롬프트(한/영) → 의상 · 원단. 반환 = 인식 여부.</summary>
    public static bool ParsePrompt(string? prompt, ref GarmentType g, ref FabricKind f)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var hit = false;
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        if (Has(prompt, "망토", "cape") || Has(prompt, "코트", "cloak")) { g = GarmentType.Cape; hit = true; }
        else if (Has(prompt, "스커트", "skirt") || Has(prompt, "치마", "kilt")) { g = GarmentType.Skirt; hit = true; }
        else if (Has(prompt, "드레스", "dress") || Has(prompt, "원피스", "gown")) { g = GarmentType.Dress; hit = true; }
        if (Has(prompt, "실크", "silk") || Has(prompt, "새틴", "satin")) { f = FabricKind.Silk; hit = true; }
        else if (Has(prompt, "데님", "denim") || Has(prompt, "청", "jean")) { f = FabricKind.Denim; hit = true; }
        else if (Has(prompt, "가죽", "leather")) { f = FabricKind.Leather; hit = true; }
        else if (Has(prompt, "울", "wool") || Has(prompt, "니트", "knit")) { f = FabricKind.Wool; hit = true; }
        else if (Has(prompt, "스판", "spandex") || Has(prompt, "라이크라", "lycra")) { f = FabricKind.Spandex; hit = true; }
        else if (Has(prompt, "면", "cotton") || Has(prompt, "린넨", "linen")) { f = FabricKind.Cotton; hit = true; }
        return hit;
    }

    // ───────────────────────── 스텝 ─────────────────────────

    /// <summary>1프레임(무할당): 앵커 갱신 → 공기역학 → XPBD 소스텝 × 8 → 주름/스펙트럼.</summary>
    public void Step(float dt)
    {
        if (dt <= 0f) return;
        var t0 = Stopwatch.GetTimestamp();
        _time += dt;
        var mat = Fabrics[(int)Fabric];
        var pose = Body.BodyPose;
        for (var j = 0; j < Cols; j++) { _anchorPrev[j] = _anchorNow[j]; _anchorNow[j] = Vector3.Transform(_anchorLocal[j], pose); }
        Aero(mat);
        var h = dt / Substeps; var invH = 1f / h; var h2 = h * h;
        var aS = mat.StretchCompliance / MathF.Max(StretchScale, 1e-3f) / h2;
        var aSh = mat.ShearCompliance / MathF.Max(StretchScale, 1e-3f) / h2;
        var aB = mat.BendCompliance / MathF.Max(BendScale, 1e-3f) / h2;
        var damp = MathF.Max(0f, 1f - mat.Damping * DampingScale * h);
        var fr = Math.Clamp(mat.Friction, 0f, 1f);
        var contacts = 0;
        for (var sub = 0; sub < Substeps; sub++)
        {
            var u = (sub + 1f) / Substeps;
            StrandSimd.AddScaled(VX, AX, h); StrandSimd.AddScaled(VY, AY, h); StrandSimd.AddScaled(VZ, AZ, h);
            StrandSimd.ScaleAdd(VX, damp, 0f); StrandSimd.ScaleAdd(VY, damp, 0f); StrandSimd.ScaleAdd(VZ, damp, 0f);
            StrandSimd.Predict(X, VX, PX, h); StrandSimd.Predict(Y, VY, PY, h); StrandSimd.Predict(Z, VZ, PZ, h);
            for (var j = 0; j < Cols; j++)
            {
                var a = Vector3.Lerp(_anchorPrev[j], _anchorNow[j], u);
                PX[j] = a.X; PY[j] = a.Y; PZ[j] = a.Z;
            }
            // XPBD(서브스텝당 λ 초기화 · 1 반복)
            for (var c = 0; c < _nc; c++)
            {
                int ia = _ca[c], ib = _cb[c];
                float wa = InvMass[ia], wb = InvMass[ib], ws = wa + wb;
                if (ws <= 0f) continue;
                float dx = PX[ia] - PX[ib], dy = PY[ia] - PY[ib], dz = PZ[ia] - PZ[ib];
                var l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (l < 1e-9f) continue;
                var at = _kind[c] == 0 ? aS : _kind[c] == 1 ? aSh : aB;
                var dl = -(l - _rest[c]) / (ws + at);
                var s = dl / l;
                PX[ia] += wa * s * dx; PY[ia] += wa * s * dy; PZ[ia] += wa * s * dz;
                PX[ib] -= wb * s * dx; PY[ib] -= wb * s * dy; PZ[ib] -= wb * s * dz;
            }
            // 바디 충돌 + 마찰
            var body = Body;
            for (var v = Cols; v < MaxVerts; v++)
            {
                var p = new Vector3(PX[v], PY[v], PZ[v]);
                if (!body.Resolve(ref p, Margin, false, out var n)) continue;
                contacts++;
                var disp = p - new Vector3(X[v], Y[v], Z[v]);
                var tan = disp - Vector3.Dot(disp, n) * n;
                p -= fr * tan;
                PX[v] = p.X; PY[v] = p.Y; PZ[v] = p.Z;
            }
            StrandSimd.VelocityFrom(VX, PX, X, invH); StrandSimd.VelocityFrom(VY, PY, Y, invH); StrandSimd.VelocityFrom(VZ, PZ, Z, invH);
            (X, PX) = (PX, X); (Y, PY) = (PY, Y); (Z, PZ) = (PZ, Z);
        }
        LastContacts = contacts; Contacts += contacts;
        ComputeNormals();
        Analyze(mat);
        Steps++;
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastStepMs = ms; if (ms > MaxStepMs) MaxStepMs = ms;
    }

    /// <summary>가속 = 중력 + 법선 상대풍 이차 항력(½ρC_d|u|u / 면밀도 · 상한 200 m/s²).</summary>
    private void Aero(in FabricMaterial mat)
    {
        var k = 0.5f * 1.2f * 1.1f / mat.Density;
        var w = Wind; var wl = w.Length();
        for (var v = 0; v < MaxVerts; v++)
        {
            var g = 1f + Turbulence * (0.6f * MathF.Sin(_time * 1.9f + v * 0.37f) + 0.4f * MathF.Sin(_time * 5.1f + v * 0.11f));
            var rel = new Vector3(w.X * g - VX[v], w.Y * g - VY[v], w.Z * g - VZ[v]);
            var n = Normal(v);
            var un = Vector3.Dot(rel, n);
            var a = n * (k * MathF.Abs(un) * un);
            // 접선 마찰 항력(약)
            a += (rel - un * n) * (0.02f * k * wl);
            var al = a.Length();
            if (al > 200f) a *= 200f / al;
            AX[v] = a.X; AY[v] = a.Y + Gravity; AZ[v] = a.Z;
        }
    }

    private int Wrap(int j) => Closed ? (j + Cols) % Cols : Math.Clamp(j, 0, Cols - 1);

    private void ComputeNormals()
    {
        for (var i = 0; i < Rows; i++)
        {
            int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, Rows - 1);
            for (var j = 0; j < Cols; j++)
            {
                var du = Position(Index(i, Wrap(j + 1))) - Position(Index(i, Wrap(j - 1)));
                var dv = Position(Index(i1, j)) - Position(Index(i0, j));
                var n = Vector3.Cross(dv, du); var l = n.Length();
                n = l > 1e-12f ? n / l : Vector3.UnitZ;
                var v = Index(i, j);
                NX[v] = n.X; NY[v] = n.Y; NZ[v] = n.Z;
            }
        }
    }

    /// <summary>주름(압축 + 곡률) · 좌굴 절차 변위 · 변형률 스펙트럼.</summary>
    private void Analyze(in FabricMaterial mat)
    {
        Array.Clear(Compression); Array.Clear(_cmpH); Array.Clear(_cnt); Array.Clear(_hist);
        float maxS = 0f, sumS = 0f;
        for (var c = 0; c < _nStruct; c++)
        {
            int a = _ca[c], b = _cb[c];
            var l = Vector3.Distance(Position(a), Position(b));
            var s = l / _rest[c] - 1f;
            var bin = (int)((s + StrainRange) / (2f * StrainRange) * Bins);
            _hist[Math.Clamp(bin, 0, Bins - 1)]++;
            var abs = MathF.Abs(s); sumS += abs; if (abs > maxS) maxS = abs;
            var cmp = MathF.Max(0f, -s);
            Compression[a] += cmp; Compression[b] += cmp; _cnt[a]++; _cnt[b]++;
            if (b == a + 1 || (Closed && b == a - Cols + 1)) { _cmpH[a] += cmp; _cmpH[b] += cmp; }
        }
        MaxStrain = maxS; MeanStrain = _nStruct > 0 ? sumS / _nStruct : 0f;
        // 좌굴 파장: λ ∝ (굽힘 강성)^¼ (굽힘 강성 ∝ 1/컴플라이언스)
        var stiff = BendScale / MathF.Max(mat.BendCompliance, 1e-6f);
        var lambda = Math.Clamp(0.012f * MathF.Pow(stiff / 10f, 0.25f), 0.006f, 0.06f);
        WrinkleWavelength = lambda;
        var sw = 0f;
        for (var i = 0; i < Rows; i++)
        {
            int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, Rows - 1);
            for (var j = 0; j < Cols; j++)
            {
                var v = Index(i, j);
                if (_cnt[v] > 0) Compression[v] /= _cnt[v];
                var nb = Normal(Index(i, Wrap(j - 1))) + Normal(Index(i, Wrap(j + 1))) + Normal(Index(i0, j)) + Normal(Index(i1, j));
                var nl = nb.Length();
                Curvature[v] = nl > 1e-6f ? Math.Clamp(1f - Vector3.Dot(Normal(v), nb / nl), 0f, 1f) : 0f;
                var w = Math.Clamp(8f * Compression[v] + 6f * Curvature[v], 0f, 1f);
                Wrinkle[v] = w; sw += w;
                var amp = MathF.Min(0.25f * lambda, lambda * MathF.Sqrt(MathF.Max(0f, 0.5f * _cmpH[v])));
                WrinkleOffset[v] = i == 0 ? 0f : amp * MathF.Sin(2f * MathF.PI * _arc[v] / lambda);
            }
        }
        MeanWrinkle = sw / MaxVerts;
    }

    /// <summary>진단: 최대 바디 관통(m).</summary>
    public float MeasurePenetration()
    {
        var m = 0f;
        for (var v = Cols; v < MaxVerts; v++) m = MathF.Max(m, Body.Penetration(Position(v), false));
        return m;
    }

    /// <summary>진단: 행 평균 반경(몸 축 기준 수평 · 플레어 측정).</summary>
    public float RowRadius(int row)
    {
        var o = Body.Offset; var s = 0f;
        for (var j = 0; j < Cols; j++) { var p = Position(Index(row, j)); s += MathF.Sqrt((p.X - o.X) * (p.X - o.X) + (p.Z - o.Z) * (p.Z - o.Z)); }
        return s / Cols;
    }

    public Vector3 RowCentroid(int row)
    {
        var a = Vector3.Zero;
        for (var j = 0; j < Cols; j++) a += Position(Index(row, j));
        return a / Cols;
    }
}
