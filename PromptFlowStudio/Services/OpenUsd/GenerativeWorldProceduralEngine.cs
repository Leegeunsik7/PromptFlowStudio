using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.OpenUsd;

public enum Biome : byte { Ocean = 0, Beach, Desert, Grassland, Forest, Taiga, Snow, Mountain, Volcanic }

public enum AssetKind : byte { None = 0, Tree, Pine, Cactus, Rock, Shrub, Lava }

/// <summary>프롬프트에서 추출한 월드 특성(0..1). 양자화 키가 같으면 같은 시드(시밀러리티 시드 고정).</summary>
public struct WorldTraits
{
    public float Temperature, Moisture, Roughness, WaterLevel, SunElevation, SunAzimuth, Fog, AssetDensity, Volcanism;

    public const int Count = 9;

    public float this[int i]
    {
        readonly get => i switch { 0 => Temperature, 1 => Moisture, 2 => Roughness, 3 => WaterLevel, 4 => SunElevation, 5 => SunAzimuth, 6 => Fog, 7 => AssetDensity, _ => Volcanism };
        set
        {
            switch (i)
            {
                case 0: Temperature = value; break;
                case 1: Moisture = value; break;
                case 2: Roughness = value; break;
                case 3: WaterLevel = value; break;
                case 4: SunElevation = value; break;
                case 5: SunAzimuth = value; break;
                case 6: Fog = value; break;
                case 7: AssetDensity = value; break;
                default: Volcanism = value; break;
            }
        }
    }

    public static WorldTraits Neutral => new() { Temperature = 0.5f, Moisture = 0.5f, Roughness = 0.4f, WaterLevel = 0.3f, SunElevation = 0.7f, SunAzimuth = 0.3f, Fog = 0.15f, AssetDensity = 0.5f, Volcanism = 0f };
}

/// <summary>광원 상태(태양 방향 · 색온도 근사 RGB · 앰비언트 · 안개).</summary>
public readonly record struct WorldLight(Vector3 SunDir, Vector3 SunColor, float Ambient, float Fog);

/// <summary>
/// [PromptFlow Studio v16.72.0] 🌍 실시간 제너레이티브 절차적 월드 생성기.
///   ▸ 프롬프트(한/영 키워드) + 시나리오 메타데이터 → <see cref="WorldTraits"/> → 8단계 양자화 키 FNV-1a → 시드. 어순/수식어가 달라도 특성이 같으면 동일 시드
///     (시밀러리티 시드 고정) · <see cref="LockSeed"/> 수동 고정 · <see cref="Spectrum"/> 시드 스펙트럼(0..1 → 변주 1024단계).
///   ▸ 지형: 해시 값 노이즈 fBm + 도메인 워프 + 릿지(거칠기) — 좌표 해석적 함수라 청크 경계 연속 · 무한 좌표.
///   ▸ 생태계: 고도 · 기온(감률) · 습도 노이즈 · 화산성 → 9종 Biome. 자산: 셀 지터 산포(Biome별 밀도/종류 · 결정적).
///   ▸ 청크 스트리밍: 5×5 LRU 캐시(33×33 높이/바이옴 · 자산 64개) — 생성·질의 무할당.
///   ▸ 출력: <see cref="EmitGaussians"/>(3DGS 표면/자산 스플랫) · <see cref="EmitUsd"/>(USD 레이어 — 자산 프림) · <see cref="HeightAt"/>(물리 높이장).
/// </summary>
public sealed class GenerativeWorldProceduralEngine : IHeightField
{
    public const int ChunkCells = 32, ChunkVerts = ChunkCells + 1, MaxChunks = 25, MaxAssetsPerChunk = 64;
    public const float CellSize = 1f, HeightScale = 18f;
    public static float ChunkSize => ChunkCells * CellSize;

    private static readonly (string Word, int Trait, float Delta)[] Lexicon =
    {
        ("desert", 0, 0.35f), ("사막", 0, 0.35f), ("desert", 1, -0.4f), ("사막", 1, -0.4f), ("dune", 1, -0.3f), ("모래", 1, -0.25f),
        ("snow", 0, -0.45f), ("설경", 0, -0.45f), ("눈밭", 0, -0.45f), ("눈보라", 0, -0.45f), ("ice", 0, -0.4f), ("빙하", 0, -0.45f), ("winter", 0, -0.3f), ("겨울", 0, -0.3f), ("arctic", 0, -0.45f), ("설원", 0, -0.45f),
        ("tropical", 0, 0.3f), ("열대", 0, 0.3f), ("jungle", 1, 0.4f), ("정글", 1, 0.4f), ("tropical", 1, 0.3f), ("rain", 1, 0.3f), ("빗속", 1, 0.3f), ("폭우", 1, 0.35f),
        ("forest", 1, 0.3f), ("숲", 1, 0.3f), ("forest", 7, 0.3f), ("숲", 7, 0.3f), ("swamp", 1, 0.45f), ("늪", 1, 0.45f),
        ("mountain", 2, 0.4f), ("산악", 2, 0.35f), ("산맥", 2, 0.45f), ("cliff", 2, 0.35f), ("절벽", 2, 0.35f), ("canyon", 2, 0.3f), ("협곡", 2, 0.3f),
        ("plain", 2, -0.3f), ("평원", 2, -0.3f), ("meadow", 2, -0.25f), ("초원", 2, -0.25f), ("meadow", 1, 0.15f), ("초원", 1, 0.15f),
        ("ocean", 3, 0.35f), ("바다", 3, 0.35f), ("island", 3, 0.3f), ("섬", 3, 0.3f), ("lake", 3, 0.15f), ("호수", 3, 0.15f), ("coast", 3, 0.2f), ("해안", 3, 0.2f),
        ("sunset", 4, -0.55f), ("노을", 4, -0.55f), ("석양", 4, -0.55f), ("dawn", 4, -0.5f), ("새벽", 4, -0.5f), ("night", 4, -0.9f), ("밤", 4, -0.9f), ("noon", 4, 0.3f), ("정오", 4, 0.3f),
        ("sunset", 5, 0.4f), ("노을", 5, 0.4f), ("석양", 5, 0.4f), ("dawn", 5, -0.25f), ("새벽", 5, -0.25f),
        ("fog", 6, 0.5f), ("안개", 6, 0.5f), ("mist", 6, 0.4f), ("misty", 6, 0.4f), ("storm", 6, 0.3f), ("폭풍", 6, 0.3f),
        ("dense", 7, 0.3f), ("울창", 7, 0.4f), ("barren", 7, -0.4f), ("황량", 7, -0.4f), ("empty", 7, -0.35f),
        ("volcano", 8, 0.8f), ("화산", 8, 0.8f), ("lava", 8, 0.7f), ("용암", 8, 0.7f), ("volcano", 0, 0.2f), ("화산", 0, 0.2f),
    };

    private readonly object _gate = new();
    private readonly int[] _cx = new int[MaxChunks], _cz = new int[MaxChunks];
    private readonly long[] _stamp = new long[MaxChunks];
    private readonly float[] _height = new float[MaxChunks * ChunkVerts * ChunkVerts];
    private readonly byte[] _biome = new byte[MaxChunks * ChunkVerts * ChunkVerts];
    private readonly byte[] _assetKind = new byte[MaxChunks * MaxAssetsPerChunk];
    private readonly float[] _assetX = new float[MaxChunks * MaxAssetsPerChunk], _assetZ = new float[MaxChunks * MaxAssetsPerChunk], _assetScale = new float[MaxChunks * MaxAssetsPerChunk];
    private readonly int[] _assetCount = new int[MaxChunks];
    private readonly int[] _biomeHist = new int[9];
    private WorldTraits _traits = WorldTraits.Neutral;
    private ulong _baseSeed, _seed, _lockedSeed;
    private bool _locked;
    private float _spectrum;
    private long _clock, _chunksGenerated;
    private int _focusX, _focusZ;

    public GenerativeWorldProceduralEngine()
    {
        Array.Fill(_cx, int.MinValue);
        Configure("", 0);
    }

    public event Action? OnChange;

    public WorldTraits Traits { get { lock (_gate) return _traits; } }
    public ulong Seed { get { lock (_gate) return _seed; } }
    public ulong SimilarityKey { get; private set; }
    public bool SeedLocked { get { lock (_gate) return _locked; } }
    public float Spectrum { get { lock (_gate) return _spectrum; } }
    public long ChunksGenerated => Interlocked.Read(ref _chunksGenerated);
    public string Prompt { get; private set; } = "";
    public string? LastAction { get; private set; }

    // ══════════════════════ 프롬프트 → 특성 → 시드 ══════════════════════

    /// <summary>프롬프트 키워드 → 특성(중립 + Δ 누적 · 같은 키워드 중복은 1회). 제어 평면.</summary>
    public static WorldTraits AnalyzePrompt(string prompt)
    {
        var t = WorldTraits.Neutral;
        var text = prompt.AsSpan();
        Span<bool> used = stackalloc bool[Lexicon.Length];
        for (var k = 0; k < Lexicon.Length; k++)
        {
            var (word, trait, delta) = Lexicon[k];
            if (used[k] || !ContainsWord(text, word)) continue;
            used[k] = true;
            t[trait] = Math.Clamp(t[trait] + delta, 0f, 1f);
        }
        return t;
    }

    /// <summary>영문은 단어 경계 일치(대소문자 무시) · 한글은 부분 문자열(조사 결합 허용 · 1음절은 앞이 경계일 때만).</summary>
    private static bool ContainsWord(ReadOnlySpan<char> text, string word)
    {
        var ascii = word[0] < 128;
        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var at = text[from..].IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            at += from;
            var beforeOk = at == 0 || !char.IsLetter(text[at - 1]);
            var end = at + word.Length;
            if (ascii)
            {
                var afterOk = end >= text.Length || !char.IsAsciiLetter(text[end]) || (end + 1 <= text.Length && (text[end] == 's' || text[end] == 'y') && (end + 1 == text.Length || !char.IsLetter(text[end + 1])));
                if (beforeOk && afterOk) return true;
            }
            else if (word.Length > 1 || beforeOk) return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>특성 8단계 양자화 키(FNV-1a 64).</summary>
    public static ulong QuantizedKey(in WorldTraits t)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < WorldTraits.Count; i++)
        {
            h ^= (ulong)Math.Clamp((int)(t[i] * 8f), 0, 7);
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>프롬프트 + 시나리오 시드로 월드 재구성(캐시 무효화).</summary>
    public void Configure(string prompt, ulong scenarioSeed)
    {
        var t = AnalyzePrompt(prompt ?? "");
        lock (_gate)
        {
            Prompt = prompt ?? "";
            _traits = t;
            SimilarityKey = QuantizedKey(t);
            _baseSeed = Mix(SimilarityKey ^ scenarioSeed * 0x9E3779B97F4A7C15UL);
            Reseed();
        }
        LastAction = "🌍 월드 구성 · 시드 " + _seed.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>특성 직접 지정(슬라이더 · 시나리오 메타데이터).</summary>
    public void SetTraits(in WorldTraits t, ulong scenarioSeed = 0)
    {
        lock (_gate)
        {
            _traits = t;
            SimilarityKey = QuantizedKey(t);
            _baseSeed = Mix(SimilarityKey ^ scenarioSeed * 0x9E3779B97F4A7C15UL);
            Reseed();
        }
        Raise();
    }

    public void LockSeed(ulong? seed)
    {
        lock (_gate)
        {
            _locked = seed.HasValue;
            _lockedSeed = seed ?? 0;
            Reseed();
        }
        LastAction = seed.HasValue ? "🔒 시드 고정 · " + seed.Value.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) : "🔓 시드 고정 해제";
        Raise();
    }

    public void SetSpectrum(float s)
    {
        lock (_gate)
        {
            _spectrum = Math.Clamp(s, 0f, 1f);
            Reseed();
        }
        Raise();
    }

    private void Reseed()
    {
        var variant = (ulong)(int)MathF.Round(_spectrum * 1023f);
        _seed = _locked ? _lockedSeed : variant == 0 ? _baseSeed : Mix(_baseSeed + variant * 0xD1B54A32D192ED03UL);
        Array.Fill(_cx, int.MinValue);
        _clock = 0;
    }

    // ══════════════════════ 해석적 필드(무할당) ══════════════════════

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static float Hash01(ulong seed, int x, int z) =>
        (Mix(seed ^ (ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL) >> 40) * (1f / (1 << 24));

    private static float ValueNoise(ulong seed, float x, float z)
    {
        var x0 = (int)MathF.Floor(x); var z0 = (int)MathF.Floor(z);
        float fx = x - x0, fz = z - z0;
        fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
        var a = Hash01(seed, x0, z0); var b = Hash01(seed, x0 + 1, z0);
        var c = Hash01(seed, x0, z0 + 1); var d = Hash01(seed, x0 + 1, z0 + 1);
        return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
    }

    private static float Fbm(ulong seed, float x, float z, int octaves)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (var o = 0; o < octaves; o++)
        {
            sum += amp * ValueNoise(seed + (ulong)o * 0x632BE59BD9B4E019UL, x, z);
            norm += amp; amp *= 0.5f; x *= 2.03f; z *= 2.03f;
        }
        return sum / norm;
    }

    /// <summary>월드 좌표 높이(m · 해석적 → 청크 경계 연속 · 무할당).</summary>
    public float HeightAt(float x, float z)
    {
        ulong seed; WorldTraits t;
        lock (_gate) { seed = _seed; t = _traits; }
        return Height(seed, t, x, z);
    }

    private static float Height(ulong seed, in WorldTraits t, float x, float z)
    {
        const float f = 1f / 48f;
        var wx = x * f + 1.7f * (Fbm(seed ^ 0xA1, x * f * 0.5f, z * f * 0.5f, 2) - 0.5f);
        var wz = z * f + 1.7f * (Fbm(seed ^ 0xB2, x * f * 0.5f + 5.2f, z * f * 0.5f, 2) - 0.5f);
        var baseH = Fbm(seed, wx, wz, 5);
        var ridge = 1f - MathF.Abs(2f * Fbm(seed ^ 0xC3, wx * 1.3f, wz * 1.3f, 4) - 1f);
        var h = baseH * (1f - t.Roughness) + ridge * ridge * t.Roughness;
        h = h * (0.55f + 0.9f * t.Roughness) - t.WaterLevel * 0.45f;
        return h * HeightScale;
    }

    public float WaterHeight => 0f;

    public float AbsorptionAt(float x, float z) => BiomeAbsorption(BiomeAt(x, z));

    /// <summary>높이장 법선(중앙 차분).</summary>
    public Vector3 NormalAt(float x, float z)
    {
        const float e = 0.25f;
        var hx = HeightAt(x + e, z) - HeightAt(x - e, z);
        var hz = HeightAt(x, z + e) - HeightAt(x, z - e);
        return Vector3.Normalize(new Vector3(-hx, 2 * e, -hz));
    }

    public Biome BiomeAt(float x, float z)
    {
        ulong seed; WorldTraits t;
        lock (_gate) { seed = _seed; t = _traits; }
        return Classify(seed, t, x, z, Height(seed, t, x, z));
    }

    private static Biome Classify(ulong seed, in WorldTraits t, float x, float z, float h)
    {
        const float f = 1f / 96f;
        var temp = t.Temperature + 0.3f * (Fbm(seed ^ 0xD4, x * f, z * f, 3) - 0.5f) - MathF.Max(0, h) / HeightScale * 0.35f;
        var moist = t.Moisture + 0.35f * (Fbm(seed ^ 0xE5, x * f + 11, z * f, 3) - 0.5f);
        if (h < 0) return Biome.Ocean;
        if (t.Volcanism > 0.3f && Fbm(seed ^ 0xF6, x * f * 2, z * f * 2, 2) > 1.05f - t.Volcanism * 0.6f) return Biome.Volcanic;
        if (h < 0.6f && temp > 0.2f) return Biome.Beach;
        if (temp < 0.22f) return Biome.Snow;
        if (h > HeightScale * 0.62f) return temp < 0.35f ? Biome.Snow : Biome.Mountain;
        if (temp > 0.6f && moist < 0.36f) return Biome.Desert;
        if (moist > 0.55f) return temp < 0.42f ? Biome.Taiga : Biome.Forest;
        return Biome.Grassland;
    }

    public static Vector3 BiomeColor(Biome b) => b switch
    {
        Biome.Ocean => new(0.12f, 0.32f, 0.62f),
        Biome.Beach => new(0.86f, 0.80f, 0.58f),
        Biome.Desert => new(0.88f, 0.70f, 0.42f),
        Biome.Grassland => new(0.45f, 0.70f, 0.32f),
        Biome.Forest => new(0.18f, 0.48f, 0.22f),
        Biome.Taiga => new(0.25f, 0.42f, 0.36f),
        Biome.Snow => new(0.93f, 0.95f, 0.98f),
        Biome.Mountain => new(0.52f, 0.50f, 0.48f),
        _ => new(0.35f, 0.12f, 0.08f),
    };

    /// <summary>음향 흡음률(0..1 · 물리 잔향 메시용).</summary>
    public static float BiomeAbsorption(Biome b) => b switch
    {
        Biome.Ocean => 0.05f, Biome.Beach => 0.35f, Biome.Desert => 0.45f, Biome.Grassland => 0.55f, Biome.Forest => 0.7f,
        Biome.Taiga => 0.65f, Biome.Snow => 0.85f, Biome.Mountain => 0.08f, _ => 0.12f,
    };

    public WorldLight Light
    {
        get
        {
            WorldTraits t;
            lock (_gate) t = _traits;
            var el = (t.SunElevation * 85f - 8f) * (MathF.PI / 180f);
            var az = t.SunAzimuth * MathF.Tau;
            var dir = Vector3.Normalize(new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az)));
            var warm = Math.Clamp(1f - t.SunElevation * 1.6f, 0f, 1f);
            var night = t.SunElevation < 0.12f;
            var col = night ? new Vector3(0.35f, 0.42f, 0.65f) : Vector3.Lerp(new Vector3(1f, 0.97f, 0.92f), new Vector3(1f, 0.55f, 0.28f), warm);
            return new WorldLight(dir, col * (night ? 0.25f : 1f), night ? 0.08f : 0.18f + 0.15f * t.SunElevation, t.Fog);
        }
    }

    // ══════════════════════ 청크 스트리밍 ══════════════════════

    /// <summary>초점 청크 기준 5×5 청크 확보(캐시 미스만 생성 · 무할당). 반환 = 신규 생성 수.</summary>
    public int EnsureAround(float x, float z)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("World.Proc.Ensure");
            var fx = (int)MathF.Floor(x / ChunkSize); var fz = (int)MathF.Floor(z / ChunkSize);
            _focusX = fx; _focusZ = fz;
            var made = 0;
            for (var dz = -2; dz <= 2; dz++)
                for (var dx = -2; dx <= 2; dx++)
                    if (Acquire(fx + dx, fz + dz, out var created) >= 0 && created) made++;
            return made;
        }
    }

    /// <summary>청크 슬롯 확보(캐시 히트/LRU 교체 생성).</summary>
    private int Acquire(int cx, int cz, out bool created)
    {
        created = false;
        var lru = 0;
        for (var s = 0; s < MaxChunks; s++)
        {
            if (_cx[s] == cx && _cz[s] == cz) { _stamp[s] = ++_clock; return s; }
            if (_stamp[s] < _stamp[lru] || _cx[s] == int.MinValue && _cx[lru] != int.MinValue) lru = s;
        }
        // 초점 5×5 범위 안 슬롯은 교체 금지 — 범위 밖 중 LRU
        var best = -1;
        for (var s = 0; s < MaxChunks; s++)
        {
            var inside = _cx[s] != int.MinValue && Math.Abs(_cx[s] - _focusX) <= 2 && Math.Abs(_cz[s] - _focusZ) <= 2;
            if (inside) continue;
            if (best < 0 || _stamp[s] < _stamp[best]) best = s;
        }
        if (best < 0) best = lru;
        Generate(best, cx, cz);
        _stamp[best] = ++_clock;
        created = true;
        return best;
    }

    private void Generate(int slot, int cx, int cz)
    {
        _cx[slot] = cx; _cz[slot] = cz;
        var seed = _seed; var t = _traits;
        var baseX = cx * ChunkSize; var baseZ = cz * ChunkSize;
        var o = slot * ChunkVerts * ChunkVerts;
        for (var j = 0; j < ChunkVerts; j++)
            for (var i = 0; i < ChunkVerts; i++)
            {
                var x = baseX + i * CellSize; var z = baseZ + j * CellSize;
                var h = Height(seed, t, x, z);
                _height[o + j * ChunkVerts + i] = h;
                _biome[o + j * ChunkVerts + i] = (byte)Classify(seed, t, x, z, h);
            }
        // 자산 산포: 8×8 셀 지터(셀당 최대 1) · 결정적
        var ao = slot * MaxAssetsPerChunk;
        var n = 0;
        var aseed = seed ^ 0x5EED_A55E7UL;
        for (var gj = 0; gj < 8; gj++)
            for (var gi = 0; gi < 8; gi++)
            {
                var gx = cx * 8 + gi; var gz = cz * 8 + gj;
                var px = baseX + (gi + Hash01(aseed, gx, gz * 3 + 1)) * (ChunkSize / 8);
                var pz = baseZ + (gj + Hash01(aseed, gx * 5 + 2, gz)) * (ChunkSize / 8);
                var h = Height(seed, t, px, pz);
                var b = Classify(seed, t, px, pz, h);
                var (kind, density) = AssetFor(b);
                if (kind == AssetKind.None || Hash01(aseed ^ 0x77, gx, gz) > density * (0.3f + 1.4f * t.AssetDensity)) continue;
                _assetKind[ao + n] = (byte)kind; _assetX[ao + n] = px; _assetZ[ao + n] = pz;
                _assetScale[ao + n] = 0.6f + 0.8f * Hash01(aseed ^ 0x99, gx, gz);
                n++;
            }
        _assetCount[slot] = n;
        Interlocked.Increment(ref _chunksGenerated);
    }

    private static (AssetKind, float) AssetFor(Biome b) => b switch
    {
        Biome.Forest => (AssetKind.Tree, 0.85f),
        Biome.Taiga => (AssetKind.Pine, 0.75f),
        Biome.Grassland => (AssetKind.Shrub, 0.35f),
        Biome.Desert => (AssetKind.Cactus, 0.2f),
        Biome.Mountain => (AssetKind.Rock, 0.4f),
        Biome.Snow => (AssetKind.Pine, 0.12f),
        Biome.Volcanic => (AssetKind.Lava, 0.45f),
        Biome.Beach => (AssetKind.Rock, 0.08f),
        _ => (AssetKind.None, 0f),
    };

    private int SlotOf(int cx, int cz)
    {
        for (var s = 0; s < MaxChunks; s++) if (_cx[s] == cx && _cz[s] == cz) return s;
        return -1;
    }

    /// <summary>청크 정보(높이 · 바이옴 복사 · 자산 수). 미생성이면 생성. 무할당.</summary>
    public int CopyChunk(int cx, int cz, Span<float> heights, Span<byte> biomes)
    {
        lock (_gate)
        {
            var s = SlotOf(cx, cz);
            if (s < 0) s = Acquire(cx, cz, out _);
            const int N = ChunkVerts * ChunkVerts;
            _height.AsSpan(s * N, Math.Min(N, heights.Length)).CopyTo(heights);
            _biome.AsSpan(s * N, Math.Min(N, biomes.Length)).CopyTo(biomes);
            return _assetCount[s];
        }
    }

    /// <summary>캐시된 청크의 높이 내용 해시(결정성 검증용).</summary>
    public ulong ChunkHash(int cx, int cz)
    {
        lock (_gate)
        {
            var s = SlotOf(cx, cz);
            if (s < 0) s = Acquire(cx, cz, out _);
            const int N = ChunkVerts * ChunkVerts;
            var h = 14695981039346656037UL;
            for (var i = 0; i < N; i++) { h ^= (ulong)BitConverter.SingleToInt32Bits(_height[s * N + i]) ^ _biome[s * N + i]; h *= 1099511628211UL; }
            for (var a = 0; a < _assetCount[s]; a++) { h ^= (ulong)BitConverter.SingleToInt32Bits(_assetX[s * MaxAssetsPerChunk + a]); h *= 1099511628211UL; }
            return h;
        }
    }

    /// <summary>캐시된 청크 전체 바이옴 히스토그램(9칸 · 무할당).</summary>
    public void BiomeHistogram(Span<int> hist9)
    {
        lock (_gate)
        {
            hist9[..9].Clear();
            for (var s = 0; s < MaxChunks; s++)
            {
                if (_cx[s] == int.MinValue) continue;
                const int N = ChunkVerts * ChunkVerts;
                for (var i = 0; i < N; i++) hist9[_biome[s * N + i]]++;
            }
        }
    }

    public Biome DominantBiome
    {
        get
        {
            BiomeHistogram(_biomeHist);
            var best = 0;
            for (var i = 1; i < 9; i++) if (_biomeHist[i] > _biomeHist[best]) best = i;
            return (Biome)best;
        }
    }

    public int AssetCount
    {
        get { lock (_gate) { var n = 0; for (var s = 0; s < MaxChunks; s++) if (_cx[s] != int.MinValue) n += _assetCount[s]; return n; } }
    }

    // ══════════════════════ 출력 ══════════════════════

    /// <summary>캐시 청크 → 3DGS(지표 스플랫 = stride 간격 · 자산 = 종류별 색/크기). 용량 충분 시 무할당. 반환 = 추가 수.</summary>
    public int EmitGaussians(GaussianScene dst, int stride = 4, bool append = false)
    {
        lock (_gate)
        {
            if (!append) dst.Count = 0;
            var start = dst.Count;
            const int N = ChunkVerts * ChunkVerts;
            var r = stride * CellSize * 0.5f;
            for (var s = 0; s < MaxChunks; s++)
            {
                if (_cx[s] == int.MinValue) continue;
                var bx = _cx[s] * ChunkSize; var bz = _cz[s] * ChunkSize;
                for (var j = 0; j < ChunkCells; j += stride)
                    for (var i = 0; i < ChunkCells; i += stride)
                    {
                        var h = _height[s * N + j * ChunkVerts + i];
                        var b = (Biome)_biome[s * N + j * ChunkVerts + i];
                        dst.Add(new Vector3(bx + i * CellSize, MathF.Max(h, 0), bz + j * CellSize), Quaternion.Identity, new Vector3(r, 0.15f, r), 0.95f, BiomeColor(b));
                    }
                for (var a = 0; a < _assetCount[s]; a++)
                {
                    var k = s * MaxAssetsPerChunk + a;
                    var x = _assetX[k]; var z = _assetZ[k]; var sc = _assetScale[k];
                    var y = Height(_seed, _traits, x, z);
                    dst.Add(new Vector3(x, y + sc, z), Quaternion.Identity, new Vector3(0.45f * sc, sc, 0.45f * sc), 0.9f, AssetColor((AssetKind)_assetKind[k]));
                }
            }
            dst.Version++;
            return dst.Count - start;
        }
    }

    public static Vector3 AssetColor(AssetKind k) => k switch
    {
        AssetKind.Tree => new(0.14f, 0.52f, 0.18f),
        AssetKind.Pine => new(0.10f, 0.36f, 0.24f),
        AssetKind.Cactus => new(0.30f, 0.60f, 0.25f),
        AssetKind.Rock => new(0.45f, 0.44f, 0.42f),
        AssetKind.Shrub => new(0.35f, 0.58f, 0.22f),
        AssetKind.Lava => new(1f, 0.35f, 0.05f),
        _ => new(0.8f, 0.8f, 0.8f),
    };

    /// <summary>
    /// 초점 청크(최대 <paramref name="maxAssets"/>) → USD 레이어: /World(Xform) · /World/Sun(DistantLight) · /World/Terrain/Asset_N(Sphere=수목 · Cube=암석/용암).
    /// 제어 평면.
    /// </summary>
    public int EmitUsd(UsdLayer dst, int maxAssets = 96)
    {
        lock (_gate)
        {
            dst.Clear();
            var world = dst.AddPrim(-1, "World", UsdSpec.Def, UsdPrimType.Xform);
            var sun = dst.AddPrim(world, "Sun", UsdSpec.Def, UsdPrimType.DistantLight);
            var t = _traits;
            dst.SetIntensity(sun, 0.3f + 2.7f * t.SunElevation);
            var terrain = dst.AddPrim(world, "Terrain", UsdSpec.Def, UsdPrimType.Scope);
            Span<char> name = stackalloc char[16];
            var n = 0;
            var center = SlotOf(_focusX, _focusZ);
            for (var pass = 0; pass < 2 && n < maxAssets; pass++)
                for (var s = 0; s < MaxChunks && n < maxAssets; s++)
                {
                    if (_cx[s] == int.MinValue || (pass == 0) != (s == center)) continue;
                    for (var a = 0; a < _assetCount[s] && n < maxAssets; a++)
                    {
                        var k = s * MaxAssetsPerChunk + a;
                        var kind = (AssetKind)_assetKind[k];
                        "Asset_".AsSpan().CopyTo(name);
                        n.TryFormat(name[6..], out var w, default, System.Globalization.CultureInfo.InvariantCulture);
                        var cube = kind is AssetKind.Rock or AssetKind.Lava;
                        var p = dst.AddPrim(terrain, name[..(6 + w)], UsdSpec.Def, cube ? UsdPrimType.Cube : UsdPrimType.Sphere);
                        if (p < 0) return n;
                        var x = _assetX[k]; var z = _assetZ[k]; var sc = _assetScale[k];
                        dst.SetTranslate(p, x, Height(_seed, t, x, z) + sc, z);
                        if (cube) dst.SetSize(p, sc * 1.2f); else dst.SetRadius(p, sc * 0.8f);
                        var c = AssetColor(kind);
                        dst.SetColor(p, c.X, c.Y, c.Z);
                        dst.SetRigidBody(p, false);
                        n++;
                    }
                }
            return n;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("World.Proc.OnChange", ex); }
    }
}
