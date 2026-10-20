using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Screenplay;
using PromptFlowStudio.Services.Sound;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.VTuber;

namespace PromptFlowStudio.Services.Mam;

public enum MamAssetKind : byte { Video, Audio, Mesh3D, Script, Character, Image, Other }

public enum DuplicateKind : byte { Exact, Perceptual, Semantic }

public sealed class MamAsset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public MamAssetKind Kind { get; init; }
    public string Title { get; set; } = "";
    public string? Path { get; set; }
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Language { get; set; } = "ko";
    public double DurationSec { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>SHA-256(hex) — 바이트 동일 중복 판정.</summary>
    public string? ContentHash { get; set; }
    /// <summary>지각 해시(이미지 dHash · 오디오 대역 지문 · 메시 모멘트 · 대본/ID SimHash) 64bit ×2.</summary>
    public ulong PHash { get; set; }
    public ulong PHash2 { get; set; }
    public bool HasPHash { get; set; }
    public bool Synthetic { get; set; } = true;
    public bool C2paSigned { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SemanticText => Kind + " " + Title + " " + string.Join(' ', Tags) + " " + Description;
}

/// <summary>검색 결과(값형) — Index = 카탈로그 슬롯.</summary>
public readonly record struct MamHit(int Index, float Score);

public sealed record DuplicateGroup(DuplicateKind Kind, MamAsset Primary, IReadOnlyList<MamAsset> Duplicates, double Similarity);

public sealed record IngestResult(MamAsset Asset, bool Added, MamAsset? DuplicateOf, DuplicateKind? Duplicate);

/// <summary>
/// [PromptFlow Studio v16.42.0] 🗄 엔터프라이즈 MAM &amp; AI 시맨틱 색인 엔진.
///   ▸ 자산(비디오 · 오디오 · 3D 메시 · 대본 · 캐릭터 ID · 이미지) → 256D 해시 임베딩(v16.38.0 Embed · 종류 토큰 가중) 연속 행렬 색인 + 지각 해시(dHash · 대역 지문 · 메시 모멘트 · SimHash) + SHA-256.
///   ▸ 시맨틱 자연어 검색: 쿼리 임베딩 → SIMD(Vector&lt;float&gt;) 내적 전수 스캔 → 호출자 Span 상위 K(삽입 정렬) — 워밍업 후 무할당 · kind: 필터.
///   ▸ 중복 제거: 바이트 동일(SHA-256) · 지각 근사(해밍 ≤ 임계) · 의미 근사(코사인 ≥ 0.95 + 길이 ±5%) — 수집 시 즉시 차단(alreadyRegistered) · 전수 그룹화.
///   ▸ 워크스페이스 수집(스토리보드 컷 · 디지털 트윈 · 대본) · 카탈로그 JSON 저장/로드(벡터 재계산).
/// </summary>
public sealed class EnterpriseMamSmartIndexingEngine
{
    public const int Dim = VTuberPersonaMemoryEngine.Dim, MaxAssets = 200_000;
    public const int PerceptualThreshold64 = 5, PerceptualThreshold128 = 12;
    public const float SemanticDuplicate = 0.95f;

    private readonly object _gate = new();
    private readonly List<MamAsset> _assets = new();
    private readonly Dictionary<string, int> _byHash = new(StringComparer.Ordinal);
    private float[] _vectors = new float[Dim * 256];
    private readonly float[] _query = new float[Dim];
    private readonly char[] _queryChars = new char[512];
    private readonly string _folder;

    public EnterpriseMamSmartIndexingEngine() : this(null) { }

    public EnterpriseMamSmartIndexingEngine(string? folder)
        => _folder = folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Mam");

    public event Action? OnChange;
    public int Count { get { lock (_gate) return _assets.Count; } }
    public long Searches { get; private set; }
    public long DuplicatesBlocked { get; private set; }
    public string? LastAction { get; private set; }
    public string CatalogPath => System.IO.Path.Combine(_folder, "catalog.json");
    public IReadOnlyList<MamAsset> Assets { get { lock (_gate) return _assets.ToArray(); } }
    public MamAsset? this[int index] { get { lock (_gate) return (uint)index < (uint)_assets.Count ? _assets[index] : null; } }
    public MamAsset? Find(string id) { lock (_gate) { foreach (var a in _assets) if (a.Id == id) return a; return null; } }

    // ══════════════════════ 지각 해시(무할당) ══════════════════════

    /// <summary>RGB(3B) → dHash 64bit(9×8 박스 다운샘플 회색조 · 수평 그래디언트).</summary>
    public static ulong DHash(ReadOnlySpan<byte> rgb, int w, int h)
    {
        Span<float> g = stackalloc float[72];
        g.Clear();
        Span<int> cnt = stackalloc int[72];
        cnt.Clear();
        for (var y = 0; y < h; y++)
        {
            var gy = y * 8 / h;
            for (var x = 0; x < w; x++)
            {
                var gx = x * 9 / w;
                var o = (y * w + x) * 3;
                g[gy * 9 + gx] += 0.299f * rgb[o] + 0.587f * rgb[o + 1] + 0.114f * rgb[o + 2];
                cnt[gy * 9 + gx]++;
            }
        }
        for (var i = 0; i < 72; i++) if (cnt[i] > 0) g[i] /= cnt[i];
        ulong hsh = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if (g[y * 9 + x + 1] > g[y * 9 + x]) hsh |= 1UL << (y * 8 + x);
        return hsh;
    }

    /// <summary>텍스트 SimHash 64bit(단어 · 2-gram 가중 · 대소문자 무시 · 무할당).</summary>
    public static ulong SimHash(ReadOnlySpan<char> text)
    {
        Span<int> acc = stackalloc int[64];
        acc.Clear();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            var s = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            if (i <= s) continue;
            var w = text[s..i];
            Vote(acc, Fnv64(w), 2);
            for (var k = 0; k + 1 < w.Length; k++) Vote(acc, Fnv64(w.Slice(k, 2)) ^ 0x9E3779B97F4A7C15UL, 1);
        }
        ulong r = 0;
        for (var b = 0; b < 64; b++) if (acc[b] > 0) r |= 1UL << b;
        return r;

        static void Vote(Span<int> a, ulong hv, int wgt) { for (var b = 0; b < 64; b++) a[b] += ((hv >> b) & 1) != 0 ? wgt : -wgt; }
    }

    public static ulong Fnv64(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= char.ToLowerInvariant(c); h *= 1099511628211UL; }
        return h;
    }

    /// <summary>메시 정점 → 형상 모멘트 해시(정규화 바운딩 · 8×8 점유 격자 · 회전 불변 아님).</summary>
    public static ulong MeshHash(ReadOnlySpan<float> xyz)
    {
        var n = xyz.Length / 3;
        if (n == 0) return 0;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < n; i++) { minX = MathF.Min(minX, xyz[i * 3]); maxX = MathF.Max(maxX, xyz[i * 3]); minY = MathF.Min(minY, xyz[i * 3 + 1]); maxY = MathF.Max(maxY, xyz[i * 3 + 1]); }
        Span<int> grid = stackalloc int[64];
        grid.Clear();
        float sx = MathF.Max(1e-6f, maxX - minX), sy = MathF.Max(1e-6f, maxY - minY);
        for (var i = 0; i < n; i++)
        {
            var gx = Math.Clamp((int)((xyz[i * 3] - minX) / sx * 8), 0, 7);
            var gy = Math.Clamp((int)((xyz[i * 3 + 1] - minY) / sy * 8), 0, 7);
            grid[gy * 8 + gx]++;
        }
        var mean = n / 64f;
        ulong r = 0;
        for (var b = 0; b < 64; b++) if (grid[b] > mean) r |= 1UL << b;
        return r;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    // ══════════════════════ 수집 ══════════════════════

    private void EmbedInto(MamAsset a, Span<float> dst)
    {
        VTuberPersonaMemoryEngine.Embed(a.SemanticText, dst);
    }

    /// <summary>자산 등록(중복 즉시 판정 · 중복이면 미등록 + DuplicateOf).</summary>
    public IngestResult Ingest(MamAsset a, bool allowDuplicate = false)
    {
        Span<float> v = stackalloc float[Dim];
        EmbedInto(a, v);
        int dup;
        DuplicateKind dk;
        MamAsset? dupAsset = null;
        lock (_gate)
        {
            dup = FindDuplicateLocked(a, v, -1, out dk);
            if (dup >= 0) dupAsset = _assets[dup];
            if (dup >= 0 && !allowDuplicate)
            {
                DuplicatesBlocked++;
                LastAction = "♻ 중복 차단(" + DupLabel(dk) + ") · " + a.Title + " ≈ " + _assets[dup].Title;
                Raise();
                return new IngestResult(a, false, _assets[dup], dk);
            }
            if (_assets.Count >= MaxAssets) return new IngestResult(a, false, null, null);
            var slot = _assets.Count;
            if (_vectors.Length < (slot + 1) * Dim) Array.Resize(ref _vectors, Math.Max(_vectors.Length * 2, (slot + 1) * Dim));
            v.CopyTo(_vectors.AsSpan(slot * Dim, Dim));
            _assets.Add(a);
            if (a.ContentHash is { Length: > 0 } h) _byHash.TryAdd(h, slot);
            LastAction = "📥 색인 · " + a.Title + " (" + KindLabel(a.Kind) + ")";
        }
        Raise();
        return new IngestResult(a, true, dupAsset, dup >= 0 ? dk : null);
    }

    private int FindDuplicateLocked(MamAsset a, ReadOnlySpan<float> v, int self, out DuplicateKind kind)
    {
        kind = DuplicateKind.Exact;
        if (a.ContentHash is { Length: > 0 } h && _byHash.TryGetValue(h, out var ex) && ex != self) return ex;
        for (var i = 0; i < _assets.Count; i++)
        {
            if (i == self) continue;
            var b = _assets[i];
            if (b.Kind != a.Kind) continue;
            if (a.HasPHash && b.HasPHash)
            {
                var two = a.Kind == MamAssetKind.Audio;
                var d = Hamming(a.PHash, b.PHash) + (two ? Hamming(a.PHash2, b.PHash2) : 0);
                if (d <= (two ? PerceptualThreshold128 : PerceptualThreshold64)) { kind = DuplicateKind.Perceptual; return i; }
            }
            var cos = Dot(v, _vectors.AsSpan(i * Dim, Dim));
            if (cos >= SemanticDuplicate && SimilarLength(a.DurationSec, b.DurationSec)) { kind = DuplicateKind.Semantic; return i; }
        }
        return -1;
    }

    private static bool SimilarLength(double a, double b) => a <= 0 || b <= 0 || Math.Abs(a - b) <= Math.Max(a, b) * 0.05;

    public static string KindFromExtension(string path, out MamAssetKind kind)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        kind = ext switch
        {
            ".mp4" or ".mov" or ".mkv" or ".webm" or ".m4v" => MamAssetKind.Video,
            ".wav" or ".mp3" or ".flac" or ".aac" or ".m4a" or ".ogg" => MamAssetKind.Audio,
            ".obj" or ".glb" or ".gltf" or ".fbx" or ".ply" => MamAssetKind.Mesh3D,
            ".fountain" or ".fdx" or ".txt" => MamAssetKind.Script,
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" => MamAssetKind.Image,
            _ => MamAssetKind.Other,
        };
        return ext;
    }

    /// <summary>파일 자산(SHA-256 스트리밍 · 대본 텍스트는 SimHash · 메시 OBJ 정점 해시).</summary>
    public IngestResult IngestFile(string path, string? description = null, IEnumerable<string>? tags = null)
    {
        KindFromExtension(path, out var kind);
        var fi = new FileInfo(path);
        var a = new MamAsset { Kind = kind, Title = System.IO.Path.GetFileNameWithoutExtension(path), Path = fi.FullName, SizeBytes = fi.Length, Description = description ?? "", ContentHash = Sha256Hex(path) };
        if (tags is not null) a.Tags.AddRange(tags);
        if (kind == MamAssetKind.Script && fi.Length < 4 * 1024 * 1024)
        {
            var text = File.ReadAllText(path);
            a.PHash = SimHash(text); a.HasPHash = true;
            if (a.Description.Length == 0) a.Description = text.Length > 400 ? text[..400] : text;
        }
        else if (kind == MamAssetKind.Mesh3D && path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) && fi.Length < 64 * 1024 * 1024)
        {
            var verts = new List<float>();
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("v ", StringComparison.Ordinal)) continue;
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) { verts.Add(x); verts.Add(y); verts.Add(z); }
            }
            a.PHash = MeshHash(verts.ToArray()); a.HasPHash = verts.Count > 0;
        }
        return Ingest(a);
    }

    public static string Sha256Hex(string path)
    {
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var fs = File.OpenRead(path);
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) ih.AppendData(buf, 0, n);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        return Convert.ToHexString(ih.GetHashAndReset()).ToLowerInvariant();
    }

    public IngestResult IngestImage(string title, ReadOnlySpan<byte> rgb, int w, int h, string description = "", params string[] tags)
    {
        var a = new MamAsset { Kind = MamAssetKind.Image, Title = title, Description = description, PHash = DHash(rgb, w, h), HasPHash = true, SizeBytes = rgb.Length, ContentHash = Convert.ToHexString(SHA256.HashData(rgb)).ToLowerInvariant() };
        a.Tags.AddRange(tags);
        return Ingest(a);
    }

    public IngestResult IngestAudio(string title, float[] mono, int rate, string description = "", params string[] tags)
    {
        var fp = DolbyAtmosSpatialAudioMapper.Fingerprint(mono, rate);
        var a = new MamAsset
        {
            Kind = MamAssetKind.Audio, Title = title, Description = description, DurationSec = mono.Length / (double)Math.Max(1, rate), SizeBytes = mono.Length * 4L,
            PHash = ulong.Parse(fp.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture), PHash2 = ulong.Parse(fp.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture), HasPHash = true,
        };
        a.Tags.AddRange(tags);
        return Ingest(a);
    }

    public IngestResult IngestMesh(string title, ReadOnlySpan<float> xyz, string description = "", params string[] tags)
    {
        var a = new MamAsset { Kind = MamAssetKind.Mesh3D, Title = title, Description = description, PHash = MeshHash(xyz), HasPHash = xyz.Length >= 3, SizeBytes = xyz.Length * 4L };
        a.Tags.AddRange(tags);
        return Ingest(a);
    }

    public IngestResult IngestScript(ScreenplayDoc doc)
    {
        var text = SceneBeatSheetScriptGenerator.ToFountain(doc);
        var a = new MamAsset { Kind = MamAssetKind.Script, Title = doc.Title, Description = text.Length > 600 ? text[..600] : text, DurationSec = doc.TotalSeconds, PHash = SimHash(text), HasPHash = true, SizeBytes = text.Length * 2L };
        a.Tags.Add("대본"); a.Tags.Add(doc.Structure.ToString());
        return Ingest(a);
    }

    public IngestResult IngestTwin(DigitalTwinAsset t)
    {
        var a = new MamAsset { Kind = MamAssetKind.Character, Title = t.Name, Description = t.PromptTokens, Path = t.TexturePath, PHash = t.FaceHash, HasPHash = t.FaceHash != 0 };
        a.Tags.Add("캐릭터"); a.Tags.Add(t.Hair.ToString()); a.Tags.Add(t.Body.ToString());
        return Ingest(a);
    }

    /// <summary>스토리보드 컷 영상 수집(로컬 파일 존재 시 SHA-256 · 프롬프트 = 설명). 반환 = 신규 등록 수.</summary>
    public int IngestStoryboard(StoryboardProject p)
    {
        var added = 0;
        foreach (var s in p.Scenes)
        {
            var a = new MamAsset { Kind = MamAssetKind.Video, Title = p.Title + " · 컷 " + (p.Scenes.IndexOf(s) + 1).ToString(CultureInfo.InvariantCulture), Description = s.Prompt, Path = s.LocalPath };
            a.Tags.Add("스토리보드");
            if (s.LocalPath is { } lp && File.Exists(lp)) { a.ContentHash = Sha256Hex(lp); a.SizeBytes = new FileInfo(lp).Length; }
            if (Ingest(a).Added) added++;
        }
        return added;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var i = _assets.FindIndex(a => a.Id == id);
            if (i < 0) return false;
            _assets.RemoveAt(i);
            RebuildLocked();
        }
        Raise();
        return true;
    }

    private void RebuildLocked()
    {
        _byHash.Clear();
        if (_vectors.Length < Math.Max(1, _assets.Count) * Dim) _vectors = new float[Math.Max(Dim * 256, _assets.Count * 2 * Dim)];
        for (var i = 0; i < _assets.Count; i++)
        {
            EmbedInto(_assets[i], _vectors.AsSpan(i * Dim, Dim));
            if (_assets[i].ContentHash is { Length: > 0 } h) _byHash.TryAdd(h, i);
        }
    }

    // ══════════════════════ 검색(무할당) ══════════════════════

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var acc = Vector<float>.Zero;
        var i = 0;
        var w = Vector<float>.Count;
        for (; i + w <= n; i += w) acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        var s = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>시맨틱 자연어 검색 → hits(내림차순 · 길이 = K). kindFilter null = 전체. "kind:video" 접두 지원. 반환 = 채운 수.</summary>
    public int Search(ReadOnlySpan<char> query, Span<MamHit> hits, MamAssetKind? kindFilter = null, float minScore = 0.05f)
    {
        if (hits.IsEmpty) return 0;
        var q = query.Trim();
        if (q.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
        {
            var sp = q.IndexOf(' ');
            var token = sp < 0 ? q[5..] : q[5..sp];
            if (TryParseKind(token, out var k)) kindFilter = k;
            q = sp < 0 ? ReadOnlySpan<char>.Empty : q[(sp + 1)..];
        }
        lock (_gate)
        {
            var len = Math.Min(q.Length, _queryChars.Length);
            q[..len].CopyTo(_queryChars);
            VTuberPersonaMemoryEngine.Embed(_queryChars.AsSpan(0, len), _query);
            var filled = 0;
            var empty = len == 0;
            for (var i = 0; i < _assets.Count; i++)
            {
                if (kindFilter is { } kf && _assets[i].Kind != kf) continue;
                var s = empty ? 1f - i * 1e-6f : Dot(_query, _vectors.AsSpan(i * Dim, Dim));
                if (s < minScore) continue;
                if (filled < hits.Length) filled++;
                else if (s <= hits[filled - 1].Score) continue;
                var p = filled - 1;
                while (p > 0 && hits[p - 1].Score < s) { hits[p] = hits[p - 1]; p--; }
                hits[p] = new MamHit(i, s);
            }
            Searches++;
            return filled;
        }
    }

    public static bool TryParseKind(ReadOnlySpan<char> s, out MamAssetKind k)
    {
        k = MamAssetKind.Other;
        if (s.Equals("video", StringComparison.OrdinalIgnoreCase) || s.Equals("영상", StringComparison.Ordinal)) k = MamAssetKind.Video;
        else if (s.Equals("audio", StringComparison.OrdinalIgnoreCase) || s.Equals("오디오", StringComparison.Ordinal)) k = MamAssetKind.Audio;
        else if (s.Equals("mesh", StringComparison.OrdinalIgnoreCase) || s.Equals("3d", StringComparison.OrdinalIgnoreCase)) k = MamAssetKind.Mesh3D;
        else if (s.Equals("script", StringComparison.OrdinalIgnoreCase) || s.Equals("대본", StringComparison.Ordinal)) k = MamAssetKind.Script;
        else if (s.Equals("character", StringComparison.OrdinalIgnoreCase) || s.Equals("캐릭터", StringComparison.Ordinal)) k = MamAssetKind.Character;
        else if (s.Equals("image", StringComparison.OrdinalIgnoreCase) || s.Equals("이미지", StringComparison.Ordinal)) k = MamAssetKind.Image;
        else return false;
        return true;
    }

    /// <summary>전체 중복 그룹(정확 → 지각 → 의미 · 각 자산은 최초 그룹에만 소속).</summary>
    public IReadOnlyList<DuplicateGroup> FindDuplicates()
    {
        var groups = new List<DuplicateGroup>();
        lock (_gate)
        {
            var used = new bool[_assets.Count];
            for (var i = 0; i < _assets.Count; i++)
            {
                if (used[i]) continue;
                var a = _assets[i];
                List<MamAsset>? dups = null;
                var kind = DuplicateKind.Semantic;
                double sim = 1;
                for (var j = i + 1; j < _assets.Count; j++)
                {
                    if (used[j]) continue;
                    var b = _assets[j];
                    DuplicateKind? k = null; double s = 0;
                    if (a.ContentHash is { Length: > 0 } && a.ContentHash == b.ContentHash) { k = DuplicateKind.Exact; s = 1; }
                    else if (a.Kind == b.Kind && a.HasPHash && b.HasPHash)
                    {
                        var two = a.Kind == MamAssetKind.Audio;
                        var d = Hamming(a.PHash, b.PHash) + (two ? Hamming(a.PHash2, b.PHash2) : 0);
                        if (d <= (two ? PerceptualThreshold128 : PerceptualThreshold64)) { k = DuplicateKind.Perceptual; s = 1 - d / (two ? 128.0 : 64.0); }
                    }
                    if (k is null && a.Kind == b.Kind)
                    {
                        var c = Dot(_vectors.AsSpan(i * Dim, Dim), _vectors.AsSpan(j * Dim, Dim));
                        if (c >= SemanticDuplicate && SimilarLength(a.DurationSec, b.DurationSec)) { k = DuplicateKind.Semantic; s = c; }
                    }
                    if (k is not { } kk) continue;
                    (dups ??= new()).Add(b);
                    used[j] = true;
                    if (kk < kind) kind = kk;
                    sim = Math.Min(sim, s);
                }
                if (dups is not null) groups.Add(new DuplicateGroup(kind, a, dups, Math.Round(sim, 3)));
            }
        }
        return groups;
    }

    // ══════════════════════ 저장 ══════════════════════

    public void Save()
    {
        Directory.CreateDirectory(_folder);
        MamAsset[] snap;
        lock (_gate) snap = _assets.ToArray();
        var tmp = CatalogPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap), new UTF8Encoding(false));
        File.Move(tmp, CatalogPath, true);
        LastAction = "💾 카탈로그 저장 · " + snap.Length.ToString(CultureInfo.InvariantCulture) + "건";
        Raise();
    }

    public int Load()
    {
        if (!File.Exists(CatalogPath)) return 0;
        try
        {
            var list = JsonSerializer.Deserialize<List<MamAsset>>(File.ReadAllText(CatalogPath));
            if (list is null) return 0;
            lock (_gate) { _assets.Clear(); _assets.AddRange(list); RebuildLocked(); }
            LastAction = "📂 카탈로그 로드 · " + list.Count.ToString(CultureInfo.InvariantCulture) + "건";
            Raise();
            return list.Count;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { Program.Log("Mam.Load", ex); return 0; }
    }

    // ══════════════════════ 데모 ══════════════════════

    /// <summary>데모 카탈로그(종류별 자산 + 의도적 중복 2건 차단). 반환 = 등록 수.</summary>
    public int SeedDemo()
    {
        var added = 0;
        void Add(MamAssetKind k, string t, string d, double dur, params string[] tags)
        {
            var a = new MamAsset { Kind = k, Title = t, Description = d, DurationSec = dur, PHash = SimHash(t + " " + d), HasPHash = k is MamAssetKind.Script or MamAssetKind.Character };
            a.Tags.AddRange(tags);
            if (Ingest(a).Added) added++;
        }
        Add(MamAssetKind.Video, "서울 야경 드론 오프닝", "한강 위로 상승하는 드론 야경 시네마틱 오프닝 컷", 12, "야경", "드론", "서울");
        Add(MamAssetKind.Video, "카페 대화 투샷", "따뜻한 조명의 카페에서 두 인물이 대화하는 장면", 18, "카페", "대화", "로맨스");
        Add(MamAssetKind.Video, "추격전 핸드헬드", "골목길 오토바이 추격 액션 핸드헬드 장면", 9, "액션", "추격", "골목");
        Add(MamAssetKind.Audio, "몽환적 신스 BGM", "느린 템포의 몽환적인 신스웨이브 배경음악", 120, "BGM", "신스웨이브");
        Add(MamAssetKind.Audio, "빗소리 앰비언스", "창밖 빗소리와 천둥 환경음", 60, "앰비언스", "비");
        Add(MamAssetKind.Mesh3D, "사이버펑크 오토바이", "네온 조명 사이버펑크 오토바이 3D 모델", 0, "3D", "차량");
        Add(MamAssetKind.Script, "EP1 붉은 서약", "왕궁 서재에서 서윤과 도현이 배신자를 논하는 판타지 대본", 0, "판타지", "대본");
        Add(MamAssetKind.Character, "서윤", "은빛 갑옷의 여기사 주인공 캐릭터 ID", 0, "주인공", "기사");
        Add(MamAssetKind.Image, "포스터 키비주얼", "붉은 달 아래 검을 든 기사 포스터", 0, "포스터", "키비주얼");
        Add(MamAssetKind.Video, "서울 야경 드론 오프닝", "한강 위로 상승하는 드론 야경 시네마틱 오프닝 컷", 12.2, "야경", "드론", "서울");   // 의미 중복
        Add(MamAssetKind.Script, "EP1 붉은 서약", "왕궁 서재에서 서윤과 도현이 배신자를 논하는 판타지 대본", 0, "판타지", "대본");       // 지각 중복
        return added;
    }

    public static string KindLabel(MamAssetKind k) => k switch
    {
        MamAssetKind.Video => "🎬 비디오", MamAssetKind.Audio => "🎵 오디오", MamAssetKind.Mesh3D => "🧊 3D 메시", MamAssetKind.Script => "📜 대본",
        MamAssetKind.Character => "🧍 캐릭터 ID", MamAssetKind.Image => "🖼 이미지", _ => "📦 기타",
    };

    public static string DupLabel(DuplicateKind k) => k switch { DuplicateKind.Exact => "바이트 동일", DuplicateKind.Perceptual => "지각 근사", _ => "의미 근사" };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Mam.OnChange", ex); } }
}
