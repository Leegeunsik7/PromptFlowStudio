using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Gallery;   // GalleryMediaService.MetaPathFor

namespace PromptFlowStudio.Services.Render;

/// <summary>캐시 정리 결과 — 임시 파일 회수 · LRU 정리 · 전후 총 용량.</summary>
public sealed record RenderCacheReport(
    int TempDeleted, long TempBytes, int PrunedDeleted, long PrunedBytes,
    long TotalBefore, long TotalAfter, int Skipped)
{
    public static readonly RenderCacheReport Empty = new(0, 0, 0, 0, 0, 0, 0);
    public int TotalDeleted => TempDeleted + PrunedDeleted;
    public long FreedBytes => TempBytes + PrunedBytes;

    public string Summary => TotalDeleted == 0
        ? "✨ 정리할 임시 파일이 없습니다 · 사용량 " + MediaProbeService.FormatBytes(TotalAfter)
        : "🧹 " + TotalDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 정리 · "
          + MediaProbeService.FormatBytes(FreedBytes) + " 회수 · 사용량 " + MediaProbeService.FormatBytes(TotalAfter)
          + (Skipped > 0 ? " · 잠금/진행 중 " + Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 보존" : "");
}

/// <summary>
/// [PromptFlowStudio] 렌더 캐시 자동 정리기 — 순수 BCL.
///   ▸ ① 고아 임시 파일 회수: <c>*_temp*.mp4</c> · <c>_temp_*</c> · <c>*.tmp</c> · <c>concat_*.txt</c> · <c>synth_*.wav</c>(최근 <see cref="MinAge"/> 이내 수정본은 진행 중으로 보고 보존).
///   ▸ ② LRU Prune: 전체 용량이 상한(기본 10GB) 초과 시 <c>.meta.json</c> 이 없는 오래된 미디어부터 상한 이하가 될 때까지 삭제(메타 보유 = 갤러리 자산 → 보호).
///   ▸ 파일별 예외 격리(잠금/권한 → 건너뜀 · Skipped 집계) · 재진입 차단(동시 실행 시 빈 보고).
/// </summary>
public sealed class RenderCacheCleaner
{
    public const int DefaultMaxCacheGb = 10, MinMaxCacheGb = 1, MaxMaxCacheGb = 500;
    /// <summary>자동 정리 시 진행 중 산출물 보호 구간.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(10);
    /// <summary>[즉시 비우기] 시 보호 구간(실행 중 인코더 산출물만 보존).</summary>
    public static readonly TimeSpan ImmediateMinAge = TimeSpan.FromSeconds(60);

    private static readonly string[] PrunableExt = [".mp4", ".webm", ".mov", ".mkv", ".wav", ".mp3", ".m4a", ".png", ".jpg", ".jpeg", ".gif"];

    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly Func<int> _maxGb;
    private readonly Func<DateTime> _utcNow;
    private int _busy;

    /// <param name="roots">정리 대상 루트(_renders · Media 캐시 등).</param>
    /// <param name="maxCacheGb">캐시 상한(GB) 공급자 — 범위 밖은 클램프.</param>
    /// <param name="utcNow">시계(테스트 주입).</param>
    public RenderCacheCleaner(Func<IReadOnlyList<string>> roots, Func<int>? maxCacheGb = null, Func<DateTime>? utcNow = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _maxGb = maxCacheGb ?? (() => DefaultMaxCacheGb);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>상한 바이트 직접 지정(테스트/진단 주입) — null 이면 GB 설정 사용.</summary>
    public long? LimitBytesOverride { get; init; }

    public static int ClampMaxGb(int gb) => gb < MinMaxCacheGb ? MinMaxCacheGb : gb > MaxMaxCacheGb ? MaxMaxCacheGb : gb;

    /// <summary>중간 인코딩 임시 산출물 판별(대소문자 무시).</summary>
    public static bool IsTempArtifact(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var n = fileName.ToLowerInvariant();
        var ext = Path.GetExtension(n);
        if (ext == ".tmp" || ext == ".part") return true;
        if (n.StartsWith("_temp_", StringComparison.Ordinal)) return true;
        if (n.StartsWith("concat_", StringComparison.Ordinal) && ext == ".txt") return true;
        if (n.StartsWith("synth_", StringComparison.Ordinal) && ext == ".wav") return true;
        return n.Contains("_temp", StringComparison.Ordinal) && (ext is ".mp4" or ".webm" or ".mkv" or ".mov" or ".wav" or ".txt");
    }

    /// <summary>루트 하위 전체 파일 용량 합(접근 불가 항목 무시).</summary>
    public long MeasureBytes()
    {
        long total = 0;
        foreach (var f in Enumerate()) total += f.Length;
        return total;
    }

    /// <summary>백그라운드 정리(UI 스레드 비차단).</summary>
    public Task<RenderCacheReport> CleanAsync(TimeSpan? minAge = null, CancellationToken ct = default)
        => Task.Run(() => Clean(minAge, ct), ct);

    /// <summary>① 임시 파일 회수 → ② 상한 초과 시 LRU Prune.</summary>
    public RenderCacheReport Clean(TimeSpan? minAge = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return RenderCacheReport.Empty;
        try
        {
            var cutoff = _utcNow() - (minAge ?? MinAge);
            var files = new List<FileInfo>();
            long before = 0;
            foreach (var f in Enumerate()) { files.Add(f); before += f.Length; }

            int tempN = 0, skipped = 0;
            long tempB = 0;
            var survivors = new List<FileInfo>(files.Count);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsTempArtifact(f.Name)) { survivors.Add(f); continue; }
                if (f.LastWriteTimeUtc > cutoff) { skipped++; survivors.Add(f); continue; }
                var len = f.Length;
                if (TryDelete(f)) { tempN++; tempB += len; }
                else { skipped++; survivors.Add(f); }
            }

            long total = before - tempB;
            var limit = LimitBytesOverride is long lb && lb > 0 ? lb : (long)ClampMaxGb(_maxGb()) << 30;
            int pruneN = 0;
            long pruneB = 0;
            if (total > limit)
            {
                var candidates = new List<FileInfo>();
                foreach (var f in survivors)
                    if (IsPrunable(f) && f.LastWriteTimeUtc <= cutoff) candidates.Add(f);
                candidates.Sort(static (a, b) => LastUse(a).CompareTo(LastUse(b)));
                foreach (var f in candidates)
                {
                    if (total <= limit) break;
                    ct.ThrowIfCancellationRequested();
                    var len = f.Length;
                    if (TryDelete(f)) { pruneN++; pruneB += len; total -= len; }
                    else skipped++;
                }
            }
            return new RenderCacheReport(tempN, tempB, pruneN, pruneB, before, total, skipped);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Program.Log("RenderCacheCleaner.Clean", ex);
            return RenderCacheReport.Empty;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>LRU 후보 — 미디어 확장자 · 임시 아님 · <c>.meta.json</c> 사이드카 없음.</summary>
    public static bool IsPrunable(FileInfo f)
    {
        var ext = f.Extension.ToLowerInvariant();
        if (Array.IndexOf(PrunableExt, ext) < 0 || IsTempArtifact(f.Name)) return false;
        try { return !File.Exists(GalleryMediaService.MetaPathFor(f.FullName)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static DateTime LastUse(FileInfo f) => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;

    private IEnumerable<FileInfo> Enumerate()
    {
        IReadOnlyList<string> roots;
        try { roots = _roots() ?? Array.Empty<string>(); }
        catch (Exception ex) { Program.Log("RenderCacheCleaner.Roots", ex); yield break; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string full;
            try { full = Path.GetFullPath(r); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!seen.Add(full.TrimEnd(Path.DirectorySeparatorChar)) || !Directory.Exists(full)) continue;

            IEnumerator<FileInfo>? it = null;
            try { it = new DirectoryInfo(full).EnumerateFiles("*", opts).GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (it is null) continue;
            using (it)
            {
                while (true)
                {
                    FileInfo cur;
                    try
                    {
                        if (!it.MoveNext()) break;
                        cur = it.Current;
                        if (!seen.Add(cur.FullName)) continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                    yield return cur;
                }
            }
        }
    }

    private static bool TryDelete(FileInfo f)
    {
        try
        {
            if ((f.Attributes & FileAttributes.ReadOnly) != 0) return false;
            f.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
