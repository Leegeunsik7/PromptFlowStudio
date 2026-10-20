using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Production;

public enum ReleasePlatform : byte { Windows, MacOS, Linux, iOS, Android }

/// <summary>플랫폼 빌드 옵션 — 트리밍 · NativeAOT(iOS는 Full AOT) · ReadyToRun · 자체 포함.</summary>
public readonly record struct ReleaseBuildOptions(bool Trim, bool NativeAot, bool ReadyToRun, bool SelfContained);

/// <summary>패키징 입력 산출물 — 경로 + 스트림 오프너(실 파일/메모리 공통).</summary>
public sealed record ReleaseArtifact(string Path, Func<Stream> Open);

public sealed record PackagedFile(string Path, long Length, byte[] Sha256);

/// <summary>서명 매니페스트 — canonical 페이로드(집계: 파일 수 · 총 바이트 · 머클 루트 포함) + ECDSA P-256 서명.</summary>
public sealed record ReleaseManifest(ReleasePlatform Platform, string Version, ReleaseBuildOptions Options, IReadOnlyList<PackagedFile> Files,
    long TotalBytes, byte[] MerkleRoot, byte[] Canonical, byte[] Signature);

public sealed record PlatformFinding(string Code, bool Error, string Message);

public sealed record PlatformVerifyReport(ReleasePlatform Platform, bool SignatureValid, bool ManifestConsistent, int Modified, int Missing, int Extra,
    int FormatMismatches, IReadOnlyList<PlatformFinding> Findings, long TotalBytes, int Files, double Millis)
{
    public bool Passed => SignatureValid && ManifestConsistent && Modified == 0 && Missing == 0 && Extra == 0 && FormatMismatches == 0 && !Findings.Any(f => f.Error);
}

/// <summary>
/// [PromptFlow Studio v16.66.0] 📦 엔터프라이즈 최종 패키징 &amp; 다중 플랫폼 무결성 검증기.
/// 패키징: 파일별 SHA-256(스트리밍) → 경로 정렬 머클 루트 → canonical 매니페스트(집계 필드 포함) → ECDSA P-256 서명(런타임 생성 키 · 하드코딩 0).
/// 검증: 서명 · canonical 재구성 일치(FixedTimeEquals) · 파일 변조/누락/추가 · 바이너리 포맷(PE/Mach-O/ELF 매직 vs 플랫폼) ·
/// 플랫폼별 트림/AOT 규칙(WinForms 트림·AOT 금지 · iOS AOT+트림 필수 · Android 트림 필수/NativeAOT 미지원 · macOS 자체 포함 필수).
/// </summary>
public sealed class EnterpriseProductionPackagingVerifier : IDisposable
{
    public static readonly ReleasePlatform[] AllPlatforms = { ReleasePlatform.Windows, ReleasePlatform.MacOS, ReleasePlatform.Linux, ReleasePlatform.iOS, ReleasePlatform.Android };

    private readonly ECDsa _signer;
    private readonly byte[] _publicKey;
    private long _packaged, _verified, _failed;

    public EnterpriseProductionPackagingVerifier()
    {
        _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _publicKey = _signer.ExportSubjectPublicKeyInfo();
    }

    public event Action? OnChange;

    /// <summary>배포용 공개키(SPKI) — 검증 측은 개인키 불필요.</summary>
    public ReadOnlyMemory<byte> PublicKey => _publicKey;
    public long Packaged => Interlocked.Read(ref _packaged);
    public long Verified => Interlocked.Read(ref _verified);
    public long Failed => Interlocked.Read(ref _failed);
    public string? LastAction { get; private set; }

    /// <summary>플랫폼 권장 옵션.</summary>
    public static ReleaseBuildOptions RecommendedOptions(ReleasePlatform p) => p switch
    {
        ReleasePlatform.Windows => new(Trim: false, NativeAot: false, ReadyToRun: true, SelfContained: true),
        ReleasePlatform.MacOS => new(Trim: true, NativeAot: false, ReadyToRun: false, SelfContained: true),
        ReleasePlatform.Linux => new(Trim: true, NativeAot: false, ReadyToRun: true, SelfContained: true),
        ReleasePlatform.iOS => new(Trim: true, NativeAot: true, ReadyToRun: false, SelfContained: true),
        _ => new(Trim: true, NativeAot: false, ReadyToRun: false, SelfContained: true),
    };

    /// <summary>플랫폼별 트림/AOT 규칙 평가.</summary>
    public static List<PlatformFinding> EvaluateOptions(ReleasePlatform p, ReleaseBuildOptions o)
    {
        var f = new List<PlatformFinding>();
        switch (p)
        {
            case ReleasePlatform.Windows:
                if (o.Trim) f.Add(new("WIN-TRIM", true, "WinForms/BlazorWebView는 트리밍 미지원 — 리플렉션 바인딩 손상"));
                if (o.NativeAot) f.Add(new("WIN-AOT", true, "WinForms NativeAOT 미지원"));
                if (!o.ReadyToRun) f.Add(new("WIN-R2R", false, "ReadyToRun 권장 — 콜드 스타트 단축"));
                break;
            case ReleasePlatform.MacOS:
                if (!o.SelfContained) f.Add(new("MAC-SC", true, "Mac Catalyst는 자체 포함 번들 필수"));
                if (o.NativeAot) f.Add(new("MAC-AOT", false, "Mac Catalyst NativeAOT는 실험적 — 회귀 검증 필요"));
                break;
            case ReleasePlatform.Linux:
                if (!o.ReadyToRun) f.Add(new("LNX-R2R", false, "ReadyToRun 권장"));
                break;
            case ReleasePlatform.iOS:
                if (!o.NativeAot) f.Add(new("IOS-AOT", true, "iOS는 JIT 금지 — AOT 필수"));
                if (!o.Trim) f.Add(new("IOS-TRIM", true, "iOS 앱 크기 상한 — 트리밍 필수"));
                break;
            case ReleasePlatform.Android:
                if (!o.Trim) f.Add(new("AND-TRIM", true, "Android 링커(트리밍) 필수"));
                if (o.NativeAot) f.Add(new("AND-AOT", true, ".NET 8 Android NativeAOT 미지원 — Mono AOT 사용"));
                break;
        }
        return f;
    }

    public enum BinaryFormat : byte { Unknown, Pe, MachO, Elf }

    /// <summary>매직 바이트 판별(PE 'MZ' · Mach-O 64/32/FAT · ELF).</summary>
    public static BinaryFormat DetectFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') return BinaryFormat.Pe;
        if (head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F') return BinaryFormat.Elf;
        if (head.Length >= 4)
        {
            var m = BitConverter.ToUInt32(head);
            if (m is 0xFEEDFACF or 0xCFFAEDFE or 0xFEEDFACE or 0xCEFAEDFE or 0xCAFEBABE or 0xBEBAFECA) return BinaryFormat.MachO;
        }
        return BinaryFormat.Unknown;
    }

    /// <summary>경로·플랫폼별 기대 포맷(관리 어셈블리 .dll은 전 플랫폼 PE · 확장자 없는 네이티브 호스트는 플랫폼 포맷).</summary>
    public static BinaryFormat ExpectedFormat(ReleasePlatform p, string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".dll" or ".exe" => BinaryFormat.Pe,
            ".dylib" => BinaryFormat.MachO,
            ".so" => BinaryFormat.Elf,
            "" => p switch { ReleasePlatform.Windows => BinaryFormat.Unknown, ReleasePlatform.MacOS or ReleasePlatform.iOS => BinaryFormat.MachO, _ => BinaryFormat.Elf },
            _ => BinaryFormat.Unknown,
        };
    }

    /// <summary>패키징 + 서명.</summary>
    public ReleaseManifest Package(ReleasePlatform platform, string version, ReleaseBuildOptions options, IReadOnlyList<ReleaseArtifact> artifacts, CancellationToken ct = default)
    {
        var files = new List<PackagedFile>(artifacts.Count);
        long total = 0;
        foreach (var a in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            using var s = a.Open();
            var len = s.CanSeek ? s.Length : -1;
            var h = SHA256.HashData(s);
            if (len < 0) len = s.Position;
            files.Add(new PackagedFile(Normalize(a.Path), len, h));
            total += len;
        }
        files.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        var root = MerkleRoot(files);
        var canonical = Canonical(platform, version, options, files, total, root);
        var sig = _signer.SignData(canonical, HashAlgorithmName.SHA256);
        Interlocked.Increment(ref _packaged);
        LastAction = "📦 " + PlatformLabel(platform) + " 패키징 · " + files.Count.ToString(CultureInfo.InvariantCulture) + "파일";
        Raise();
        return new ReleaseManifest(platform, version, options, files, total, root, canonical, sig);
    }

    /// <summary>무결성 검증(공개키만 사용).</summary>
    public PlatformVerifyReport Verify(ReleaseManifest m, IReadOnlyList<ReleaseArtifact> artifacts, ReadOnlySpan<byte> publicKeySpki, CancellationToken ct = default)
    {
        var t0 = Stopwatch.GetTimestamp();
        bool sigOk;
        using (var ec = ECDsa.Create())
        {
            try { ec.ImportSubjectPublicKeyInfo(publicKeySpki, out _); sigOk = ec.VerifyData(m.Canonical, m.Signature, HashAlgorithmName.SHA256); }
            catch (CryptographicException) { sigOk = false; }
        }
        // 매니페스트 필드가 서명된 canonical과 동일한지(필드 위조 차단)
        var rebuilt = Canonical(m.Platform, m.Version, m.Options, m.Files, m.TotalBytes, MerkleRoot(m.Files));
        var consistent = CryptographicOperations.FixedTimeEquals(rebuilt, m.Canonical) && CryptographicOperations.FixedTimeEquals(MerkleRoot(m.Files), m.MerkleRoot);

        var expected = new Dictionary<string, PackagedFile>(m.Files.Count, StringComparer.Ordinal);
        foreach (var f in m.Files) expected[f.Path] = f;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int modified = 0, extra = 0, formatBad = 0;
        var findings = EvaluateOptions(m.Platform, m.Options);
        Span<byte> head = stackalloc byte[4];
        foreach (var a in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            var path = Normalize(a.Path);
            int n;
            using (var hs = a.Open()) n = hs.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            var want = ExpectedFormat(m.Platform, path);
            if (want != BinaryFormat.Unknown && DetectFormat(head[..n]) != want)
            {
                formatBad++;
                findings.Add(new("FMT", true, path + " — 기대 " + want + " / 실제 " + DetectFormat(head[..n])));
            }
            byte[] h;
            using (var s = a.Open()) h = SHA256.HashData(s);
            if (!expected.TryGetValue(path, out var pf)) { extra++; findings.Add(new("EXTRA", true, path + " — 매니페스트에 없는 파일")); continue; }
            seen.Add(path);
            if (!CryptographicOperations.FixedTimeEquals(h, pf.Sha256)) { modified++; findings.Add(new("TAMPER", true, path + " — 해시 불일치(변조)")); }
        }
        var missing = 0;
        foreach (var f in m.Files) if (!seen.Contains(f.Path)) { missing++; findings.Add(new("MISSING", true, f.Path + " — 누락")); }
        if (!sigOk) findings.Add(new("SIG", true, "ECDSA 서명 검증 실패"));
        if (!consistent) findings.Add(new("MANIFEST", true, "매니페스트 필드가 서명 페이로드와 불일치"));
        var r = new PlatformVerifyReport(m.Platform, sigOk, consistent, modified, missing, extra, formatBad, findings, m.TotalBytes, m.Files.Count,
            (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Interlocked.Increment(ref _verified);
        if (!r.Passed) Interlocked.Increment(ref _failed);
        LastAction = (r.Passed ? "✅ " : "❌ ") + PlatformLabel(m.Platform) + " 검증";
        Raise();
        return r;
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    /// <summary>머클 루트 — 잎 = SHA256(path ‖ 0 ‖ sha) · 홀수 노드는 승계.</summary>
    public static byte[] MerkleRoot(IReadOnlyList<PackagedFile> files)
    {
        if (files.Count == 0) return SHA256.HashData(ReadOnlySpan<byte>.Empty);
        var level = new List<byte[]>(files.Count);
        foreach (var f in files)
        {
            var p = Encoding.UTF8.GetBytes(f.Path);
            var leaf = new byte[p.Length + 1 + f.Sha256.Length];
            p.CopyTo(leaf, 0); f.Sha256.CopyTo(leaf, p.Length + 1);
            level.Add(SHA256.HashData(leaf));
        }
        Span<byte> pair = stackalloc byte[64];
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                if (i + 1 >= level.Count) { next.Add(level[i]); continue; }
                level[i].CopyTo(pair); level[i + 1].CopyTo(pair[32..]);
                next.Add(SHA256.HashData(pair));
            }
            level = next;
        }
        return level[0];
    }

    private static byte[] Canonical(ReleasePlatform p, string version, ReleaseBuildOptions o, IReadOnlyList<PackagedFile> files, long total, byte[] root)
    {
        var sb = new StringBuilder(256 + files.Count * 96);
        sb.Append("PFS-RELEASE-1\n");
        sb.Append(p.ToString()).Append('\n').Append(version).Append('\n');
        sb.Append(o.Trim ? '1' : '0').Append(o.NativeAot ? '1' : '0').Append(o.ReadyToRun ? '1' : '0').Append(o.SelfContained ? '1' : '0').Append('\n');
        sb.Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(total.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Convert.ToHexString(root)).Append('\n');
        foreach (var f in files)
            sb.Append(f.Path).Append('\t').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Convert.ToHexString(f.Sha256)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>데모 산출물 — 플랫폼 올바른 매직 + 결정적 페이로드.</summary>
    public static List<ReleaseArtifact> DemoArtifacts(ReleasePlatform p, string version)
    {
        var list = new List<ReleaseArtifact>();
        void Add(string path, BinaryFormat fmt, int size)
        {
            var b = new byte[size];
            var seed = (uint)(path.GetHashCode(StringComparison.Ordinal) ^ version.GetHashCode(StringComparison.Ordinal)) | 1;
            for (var i = 0; i < b.Length; i++) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; b[i] = (byte)seed; }
            switch (fmt)
            {
                case BinaryFormat.Pe: b[0] = (byte)'M'; b[1] = (byte)'Z'; break;
                case BinaryFormat.Elf: b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; break;
                case BinaryFormat.MachO: BitConverter.TryWriteBytes(b, 0xFEEDFACFu); break;
                default: b[0] = (byte)'{'; break;
            }
            list.Add(new ReleaseArtifact(path, () => new MemoryStream(b, writable: false)));
        }
        Add("PromptFlowStudio.dll", BinaryFormat.Pe, 48 * 1024);
        Add("PromptFlowStudio.Shared.dll", BinaryFormat.Pe, 24 * 1024);
        Add("appsettings.json", BinaryFormat.Unknown, 2 * 1024);
        switch (p)
        {
            case ReleasePlatform.Windows: Add("PromptFlowStudio.exe", BinaryFormat.Pe, 16 * 1024); break;
            case ReleasePlatform.MacOS: Add("Contents/MacOS/PromptFlowStudio", BinaryFormat.MachO, 32 * 1024); Add("Contents/MonoBundle/libSkiaSharp.dylib", BinaryFormat.MachO, 20 * 1024); break;
            case ReleasePlatform.Linux: Add("promptflow", BinaryFormat.Elf, 32 * 1024); Add("libe_sqlite3.so", BinaryFormat.Elf, 12 * 1024); break;
            case ReleasePlatform.iOS: Add("PromptFlowStudio.app/PromptFlowStudio", BinaryFormat.MachO, 64 * 1024); Add("PromptFlowStudio.app/Info.plist", BinaryFormat.Unknown, 1024); break;
            case ReleasePlatform.Android: Add("lib/arm64-v8a/libmonodroid.so", BinaryFormat.Elf, 40 * 1024); Add("AndroidManifest.xml", BinaryFormat.Unknown, 1024); break;
        }
        return list;
    }

    /// <summary>실 산출물 스캔(최상위 *.dll/*.exe · 상한 파일 수/총 바이트).</summary>
    public static List<ReleaseArtifact> ScanDirectory(string dir, int maxFiles = 400, long maxBytes = 512L * 1024 * 1024)
    {
        var list = new List<ReleaseArtifact>();
        if (!Directory.Exists(dir)) return list;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var ext = System.IO.Path.GetExtension(path);
            if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var len = new FileInfo(path).Length;
            if (list.Count >= maxFiles || total + len > maxBytes) break;
            total += len;
            var full = path;
            list.Add(new ReleaseArtifact(System.IO.Path.GetFileName(path), () => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan)));
        }
        return list;
    }

    public static string PlatformLabel(ReleasePlatform p) => p switch
    {
        ReleasePlatform.Windows => "Windows",
        ReleasePlatform.MacOS => "macOS",
        ReleasePlatform.Linux => "Linux",
        ReleasePlatform.iOS => "iOS",
        _ => "Android",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Production.Packager.OnChange", ex); }
    }

    public void Dispose() => _signer.Dispose();
}
