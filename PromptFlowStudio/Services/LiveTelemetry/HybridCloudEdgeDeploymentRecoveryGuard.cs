using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.LiveTelemetry;

public enum DeployOutcome : byte { UpToDate, Applied, RolledBack, Rejected, Failed }

/// <summary>파일 매니페스트 항목: 상대 경로 · 크기 · 전체 SHA-256 · 블록(4 KB) SHA-256 목록.</summary>
public sealed class ManifestEntry
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public required byte[] Hash { get; init; }
    public required byte[][] Blocks { get; init; }
}

/// <summary>파일 델타: 신규 크기 · 변경 블록(인덱스, 바이트) · 목표 해시.</summary>
public sealed class FileDelta
{
    public required string Path { get; init; }
    public long NewSize { get; init; }
    public required byte[] TargetHash { get; init; }
    public bool Delete { get; init; }
    public readonly List<(int Index, byte[] Data)> Blocks = new();
}

/// <summary>델타 패키지(서명: HMAC-SHA256 canonical — 집계 필드 포함).</summary>
public sealed class DeltaPackage
{
    public required string Version { get; init; }
    public readonly List<FileDelta> Files = new();
    public long PayloadBytes, TotalBytes;
    public int ChangedBlocks, TotalBlocks;
    public byte[]? Signature;
}

/// <summary>에지 노드 상태.</summary>
public sealed class EdgeNode
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public string? Version;
    public DeployOutcome LastOutcome;
    public long Deploys, Rollbacks, BytesReceived;
    public double LastMs;
    public string? LastError;
    public bool InjectFault;
    public readonly List<string> Checkpoints = new();
}

/// <summary>
/// [PromptFlow Studio v16.94.0] ☁↔🖥 하이브리드 클라우드-에지 델타 OTA 배포 &amp; 자율 복구 가드(Pure BCL · 파일 시스템 저장소).
///   ▸ 매니페스트: 파일별 SHA-256 + 4 KB 블록 해시 → 델타 = 변경/추가 블록 + 삭제 · 전송량(PayloadBytes) vs 전체.
///   ▸ 서명: HMAC-SHA256(canonical: 버전 · 파일 수 · 변경 블록 수 · 페이로드 바이트 · 파일별 경로/크기/해시) — 키는 환경변수 PROMPTFLOW_OTA_KEY(미설정 = 무서명 · 해시 검증만) · FixedTimeEquals.
///   ▸ 적용(무단절): 파일별 스테이징(.ota-tmp) 재구성 → 목표 해시 검증 → 원자적 교체(File.Move overwrite). 교체 전 체크포인트(변경 파일 원본) 보관.
///   ▸ 복구: 재구성 해시 불일치/서명 불일치 = 거부(노드 미변경) · 적용 후 헬스 체크 실패/예외 = 체크포인트 롤백 · 수동 롤백 · 체크포인트 보존 N개.
///   "클라우드" = 릴리스 디렉터리(네트워크 공유/동기화 폴더 가능) · 외부 서비스 호출 없음.
/// </summary>
public sealed class HybridCloudEdgeDeploymentRecoveryGuard
{
    public const int BlockSize = 4096;
    public const string StageSuffix = ".ota-tmp", CheckpointDir = ".ota-checkpoints", VersionFile = ".ota-version";

    private readonly object _gate = new();
    private readonly List<EdgeNode> _nodes = new();
    private readonly byte[]? _key;

    public HybridCloudEdgeDeploymentRecoveryGuard(byte[]? hmacKey = null)
    {
        _key = hmacKey ?? KeyFromEnvironment();
    }

    public int KeepCheckpoints { get; set; } = 3;
    public bool IsSigned => _key is not null;
    /// <summary>헬스 체크(노드, 적용 버전) → false = 롤백.</summary>
    public Func<EdgeNode, string, bool>? HealthCheck { get; set; }
    public int NodeCount { get { lock (_gate) return _nodes.Count; } }
    public EdgeNode Node(int i) { lock (_gate) return _nodes[i]; }
    public long TotalDeploys { get; private set; }
    public long TotalRollbacks { get; private set; }
    public long TotalRejected { get; private set; }
    public DeltaPackage? LastPackage { get; private set; }
    public string? LastAction { get; private set; }

    private static byte[]? KeyFromEnvironment()
    {
        var k = Environment.GetEnvironmentVariable("PROMPTFLOW_OTA_KEY");
        return string.IsNullOrEmpty(k) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(k));
    }

    public EdgeNode AddNode(string name, string root)
    {
        lock (_gate)
        {
            foreach (var n in _nodes) if (n.Name == name) return n; // alreadyRegistered
            Directory.CreateDirectory(root);
            var node = new EdgeNode { Name = name, Root = root, Version = ReadVersion(root) };
            node.Checkpoints.AddRange(ListCheckpoints(root));
            _nodes.Add(node);
            return node;
        }
    }

    /// <summary>디렉터리 매니페스트(체크포인트/스테이징/버전 파일 제외 · 경로 정렬).</summary>
    public static List<ManifestEntry> BuildManifest(string root)
    {
        var list = new List<ManifestEntry>();
        if (!Directory.Exists(root)) return list;
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith(CheckpointDir, StringComparison.Ordinal) || rel.EndsWith(StageSuffix, StringComparison.Ordinal) || rel == VersionFile) continue;
            files.Add(rel);
        }
        files.Sort(StringComparer.Ordinal);
        foreach (var rel in files)
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(root, rel));
            var n = (bytes.Length + BlockSize - 1) / BlockSize;
            var blocks = new byte[n][];
            for (var b = 0; b < n; b++) blocks[b] = SHA256.HashData(bytes.AsSpan(b * BlockSize, Math.Min(BlockSize, bytes.Length - b * BlockSize)));
            list.Add(new ManifestEntry { Path = rel, Size = bytes.Length, Hash = SHA256.HashData(bytes), Blocks = blocks });
        }
        return list;
    }

    /// <summary>델타 생성(클라우드 릴리스 → 노드 현재 상태).</summary>
    public DeltaPackage CreateDelta(string cloudRoot, string version, string nodeRoot)
    {
        var src = BuildManifest(cloudRoot);
        var dst = BuildManifest(nodeRoot);
        var dmap = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var e in dst) dmap[e.Path] = e;
        var pkg = new DeltaPackage { Version = version };
        foreach (var e in src)
        {
            pkg.TotalBytes += e.Size; pkg.TotalBlocks += e.Blocks.Length;
            dmap.TryGetValue(e.Path, out var old);
            if (old is not null) dmap.Remove(e.Path);
            if (old is not null && CryptographicOperations.FixedTimeEquals(old.Hash, e.Hash)) continue;
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(cloudRoot, e.Path));
            var fd = new FileDelta { Path = e.Path, NewSize = e.Size, TargetHash = e.Hash };
            for (var b = 0; b < e.Blocks.Length; b++)
            {
                if (old is not null && b < old.Blocks.Length && CryptographicOperations.FixedTimeEquals(old.Blocks[b], e.Blocks[b])) continue;
                var len = (int)Math.Min(BlockSize, e.Size - (long)b * BlockSize);
                fd.Blocks.Add((b, bytes.AsSpan(b * BlockSize, len).ToArray()));
                pkg.PayloadBytes += len; pkg.ChangedBlocks++;
            }
            pkg.Files.Add(fd);
        }
        foreach (var gone in dmap.Values) pkg.Files.Add(new FileDelta { Path = gone.Path, Delete = true, TargetHash = Array.Empty<byte>() });
        pkg.Files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        if (_key is not null) pkg.Signature = Sign(pkg);
        LastPackage = pkg;
        return pkg;
    }

    private static byte[] Canonical(DeltaPackage p)
    {
        var sb = new StringBuilder(256);
        sb.Append("ota/1|").Append(p.Version).Append('|').Append(p.Files.Count).Append('|').Append(p.ChangedBlocks).Append('|').Append(p.PayloadBytes).Append('\n');
        foreach (var f in p.Files)
        {
            sb.Append(f.Path).Append('|').Append(f.Delete ? 'D' : 'U').Append('|').Append(f.NewSize).Append('|').Append(Convert.ToHexString(f.TargetHash)).Append('|').Append(f.Blocks.Count);
            foreach (var (i, d) in f.Blocks) sb.Append('|').Append(i).Append(':').Append(Convert.ToHexString(SHA256.HashData(d)));
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private byte[] Sign(DeltaPackage p) => HMACSHA256.HashData(_key!, Canonical(p));

    public bool Verify(DeltaPackage p) => _key is null || (p.Signature is { } s && CryptographicOperations.FixedTimeEquals(s, Sign(p)));

    /// <summary>노드 적용: 서명 검증 → 스테이징 재구성/해시 검증 → 체크포인트 → 원자 교체 → 헬스 체크 → (실패) 롤백.</summary>
    public DeployOutcome Apply(EdgeNode node, DeltaPackage pkg)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (pkg.Files.Count == 0) { node.Version = pkg.Version; WriteVersion(node.Root, pkg.Version); return Done(node, DeployOutcome.UpToDate, null); }
            if (!Verify(pkg)) { TotalRejected++; return Done(node, DeployOutcome.Rejected, "서명 불일치"); }

            // 1) 스테이징 재구성 + 해시 검증(노드 미변경)
            var staged = new List<(string Rel, string Stage)>();
            try
            {
                foreach (var f in pkg.Files)
                {
                    if (f.Delete) continue;
                    var target = SafePath(node.Root, f.Path);
                    var baseBytes = File.Exists(target) ? File.ReadAllBytes(target) : Array.Empty<byte>();
                    var buf = new byte[f.NewSize];
                    Buffer.BlockCopy(baseBytes, 0, buf, 0, (int)Math.Min(baseBytes.Length, f.NewSize));
                    foreach (var (i, d) in f.Blocks) Buffer.BlockCopy(d, 0, buf, i * BlockSize, d.Length);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(buf), f.TargetHash)) throw new InvalidDataException("재구성 해시 불일치: " + f.Path);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    var stage = target + StageSuffix;
                    File.WriteAllBytes(stage, buf);
                    staged.Add((f.Path, stage));
                }
            }
            catch (InvalidDataException ex)
            {
                foreach (var (_, s) in staged) TryDelete(s);
                TotalRejected++;
                return Done(node, DeployOutcome.Rejected, ex.Message);
            }

            // 2) 체크포인트(변경/삭제 대상 원본 + 이전 버전)
            var cp = CreateCheckpoint(node, pkg);

            // 3) 원자 교체
            try
            {
                foreach (var (rel, stage) in staged) File.Move(stage, SafePath(node.Root, rel), overwrite: true);
                foreach (var f in pkg.Files) if (f.Delete) TryDelete(SafePath(node.Root, f.Path));
                WriteVersion(node.Root, pkg.Version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var (_, s) in staged) TryDelete(s);
                Restore(node, cp);
                return Done(node, DeployOutcome.RolledBack, "교체 실패 → 롤백: " + ex.Message);
            }

            // 4) 헬스 체크
            bool healthy;
            try { healthy = !node.InjectFault && (HealthCheck?.Invoke(node, pkg.Version) ?? true); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Ota.Health", ex); healthy = false; }
            if (!healthy) { Restore(node, cp); return Done(node, DeployOutcome.RolledBack, "헬스 체크 실패 → 체크포인트 복원"); }

            node.Version = pkg.Version; node.Deploys++; node.BytesReceived += pkg.PayloadBytes; TotalDeploys++;
            Prune(node);
            return Done(node, DeployOutcome.Applied, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            PromptFlowStudio.Program.Log("Ota.Apply", ex);
            return Done(node, DeployOutcome.Failed, ex.Message);
        }
        finally { node.LastMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
    }

    /// <summary>전 노드 동기화(노드별 델타 · 결과 수 반환).</summary>
    public int SyncAll(string cloudRoot, string version)
    {
        var applied = 0;
        for (var i = 0; i < NodeCount; i++)
        {
            var n = Node(i);
            var pkg = CreateDelta(cloudRoot, version, n.Root);
            if (Apply(n, pkg) == DeployOutcome.Applied) applied++;
        }
        LastAction = "☁ 동기화 v" + version + " — 적용 " + applied + "/" + NodeCount;
        return applied;
    }

    /// <summary>수동 롤백(최신 체크포인트).</summary>
    public bool RollbackLatest(EdgeNode node)
    {
        if (node.Checkpoints.Count == 0) return false;
        var cp = node.Checkpoints[^1];
        Restore(node, cp);
        node.LastOutcome = DeployOutcome.RolledBack;
        LastAction = "↺ " + node.Name + " 롤백 → v" + (node.Version ?? "?");
        return true;
    }

    private string CreateCheckpoint(EdgeNode node, DeltaPackage pkg)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + node.Checkpoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dir = System.IO.Path.Combine(node.Root, CheckpointDir, id);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append("version=").Append(node.Version ?? "").Append('\n');
        foreach (var f in pkg.Files)
        {
            var target = SafePath(node.Root, f.Path);
            if (File.Exists(target))
            {
                var dst = System.IO.Path.Combine(dir, "files", f.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                File.Copy(target, dst, true);
                sb.Append("keep=").Append(f.Path).Append('\n');
            }
            else sb.Append("new=").Append(f.Path).Append('\n');
        }
        File.WriteAllText(System.IO.Path.Combine(dir, "checkpoint.txt"), sb.ToString());
        node.Checkpoints.Add(id);
        return id;
    }

    /// <summary>체크포인트 복원: 원본 파일 복사(원자 교체) · 신규 파일 삭제 · 버전 복원 → 체크포인트 소비.</summary>
    private void Restore(EdgeNode node, string id)
    {
        var dir = System.IO.Path.Combine(node.Root, CheckpointDir, id);
        var meta = System.IO.Path.Combine(dir, "checkpoint.txt");
        if (!File.Exists(meta)) return;
        string? version = null;
        foreach (var line in File.ReadAllLines(meta))
        {
            if (line.StartsWith("version=", StringComparison.Ordinal)) version = line[8..];
            else if (line.StartsWith("keep=", StringComparison.Ordinal))
            {
                var rel = line[5..]; var target = SafePath(node.Root, rel);
                var stage = target + StageSuffix;
                File.Copy(System.IO.Path.Combine(dir, "files", rel), stage, true);
                File.Move(stage, target, overwrite: true);
            }
            else if (line.StartsWith("new=", StringComparison.Ordinal)) TryDelete(SafePath(node.Root, line[4..]));
        }
        node.Version = string.IsNullOrEmpty(version) ? null : version;
        WriteVersion(node.Root, node.Version);
        node.Rollbacks++; TotalRollbacks++;
        node.Checkpoints.Remove(id);
        try { Directory.Delete(dir, true); } catch (IOException ex) { PromptFlowStudio.Program.Log("Ota.Checkpoint.Delete", ex); }
    }

    private void Prune(EdgeNode node)
    {
        while (node.Checkpoints.Count > KeepCheckpoints)
        {
            var id = node.Checkpoints[0]; node.Checkpoints.RemoveAt(0);
            try { Directory.Delete(System.IO.Path.Combine(node.Root, CheckpointDir, id), true); } catch (IOException ex) { PromptFlowStudio.Program.Log("Ota.Prune", ex); }
        }
    }

    private static List<string> ListCheckpoints(string root)
    {
        var l = new List<string>();
        var d = System.IO.Path.Combine(root, CheckpointDir);
        if (Directory.Exists(d)) foreach (var x in Directory.EnumerateDirectories(d)) l.Add(System.IO.Path.GetFileName(x));
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    /// <summary>경로 탈출(../, 절대 경로) 차단.</summary>
    private static string SafePath(string root, string rel)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
        var r = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("경로 탈출 차단: " + rel);
        return full;
    }

    private static string? ReadVersion(string root)
    {
        var p = System.IO.Path.Combine(root, VersionFile);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
    }

    private static void WriteVersion(string root, string? v)
    {
        var p = System.IO.Path.Combine(root, VersionFile);
        if (v is null) { TryDelete(p); return; }
        File.WriteAllText(p, v);
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException ex) { PromptFlowStudio.Program.Log("Ota.Delete", ex); }
    }

    private DeployOutcome Done(EdgeNode n, DeployOutcome o, string? err)
    {
        n.LastOutcome = o; n.LastError = err;
        LastAction = n.Name + " → " + o.ToString() + (err is null ? "" : " · " + err);
        return o;
    }
}
