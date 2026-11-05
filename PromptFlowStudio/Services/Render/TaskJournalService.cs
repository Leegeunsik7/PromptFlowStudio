using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Render;

/// <summary>클라우드 태스크 저널 항목 — <c>active_tasks.json</c> 1행.</summary>
public sealed record CloudTaskJournalEntry(
    string TaskId, string Engine, string Prompt, string DurationMode, string Status, DateTimeOffset StartedAt,
    string? OutputPath = null, string? Resolution = null, long Seed = 0, string? Project = null, bool PingPong = false)
{
    /// <summary>15초 앰비언트 루프 후처리 대상 여부.</summary>
    public bool IsAmbient => string.Equals(DurationMode, TaskJournalService.AmbientModeLabel, StringComparison.OrdinalIgnoreCase);
}

/// <summary>복구 1회 결과 — 탐색 · 완수(다운로드+후처리+갤러리) · 원격 진행 중 보류 · 폐기(실패/만료).</summary>
public sealed record TaskRecoveryResult(int Scanned, int Recovered, int Pending, int Dropped, IReadOnlyList<string> RecoveredPaths)
{
    public static readonly TaskRecoveryResult Empty = new(0, 0, 0, 0, Array.Empty<string>());
}

/// <summary>
/// ☁️ Wan 2.1 클라우드 태스크 복구 저널.
///   ▸ 발번 즉시 <c>active_tasks.json</c> 원자적 기록(임시 → 교체) — 네트워크 단절·앱 재시작에도 task_id 보존.
///   ▸ 로컬 저장 성공 · 원격 종료 실패 시 저널에서 제거.
///   ▸ 앱 시작/네트워크 재연결 시 미완료 저널 탐색 → DashScope 상태 조회 → 완료분 MP4 다운로드 → 15초 앰비언트 루프 → 갤러리 메타 기록.
///   ▸ 모든 IO·조회 예외 격리([부분 구제] — 실패 항목은 저널 유지 후 다음 기회 재시도).
/// </summary>
public sealed class TaskJournalService : IDisposable
{
    public const string FileName = "active_tasks.json";
    public const string AmbientModeLabel = "15s-Ambient";
    public const string StatusProcessing = "Processing";
    /// <summary>앱 종료·네트워크 단절로 중단 — 재시작 시 자동 회수 대상.</summary>
    public const string StatusInterrupted = "Interrupted";
    /// <summary>사용자 의도 [취소] — 자동 회수 영구 제외(좀비 다운로드 차단). 저널 인스펙터에서 수동 회수/재시도/삭제만 허용.</summary>
    public const string StatusUserCanceled = "UserCanceled";
    public const string StatusFailed = "Failed";
    /// <summary>DashScope 결과 URL 유효기간(24h) — 경과 + 원격 미확인 항목은 폐기.</summary>
    public static readonly TimeSpan ResultUrlTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private int _recovering, _pendingCount, _disposed, _netHooked, _count = -1;

    public TaskJournalService(string? journalPath = null)
    {
        _path = string.IsNullOrWhiteSpace(journalPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Render", FileName)
            : journalPath;
    }

    public string JournalPath => _path;
    /// <summary>DashScope 상태 조회(null = 복구 비활성 · 저널 유지).</summary>
    public Func<string, CancellationToken, Task<WanTaskResult>>? Query { get; set; }
    /// <summary>결과 URL → 로컬 파일 다운로더(null = 엔진 기본 다운로더).</summary>
    public Func<string, string, CancellationToken, Task>? Downloader { get; set; }
    /// <summary>15초 앰비언트 루프 후처리기(null = 생략).</summary>
    public AmbientLoopService? Ambient { get; set; }
    /// <summary>OutputPath 미기록 항목의 저장 폴더 공급자.</summary>
    public Func<string?>? MediaRootProvider { get; set; }
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>복원 진행 중 미완료 건수(인디케이터).</summary>
    public int RecoveringCount => Volatile.Read(ref _pendingCount);
    public bool IsRecovering => Volatile.Read(ref _recovering) == 1;
    /// <summary>상태 변화(기록·제거·복원 진행) — 구독자는 UI 스레드로 마샬링.</summary>
    public event Action? OnChanged;
    /// <summary>복원 완수 산출물 경로(갤러리 새로고침 트리거).</summary>
    public event Action<string>? OnRecovered;

    /// <summary>저널 총 건수(캐시 — 렌더 핫패스 IO 없음).</summary>
    public int Count
    {
        get
        {
            var c = Volatile.Read(ref _count);
            if (c >= 0) return c;
            lock (_gate) { c = ReadUnsafe().Count; Volatile.Write(ref _count, c); }
            return c;
        }
    }

    /// <summary>자동 회수 대상 여부 — <see cref="StatusUserCanceled"/> 는 명시적 제외.</summary>
    public static bool IsAutoRecoverable(CloudTaskJournalEntry e)
        => e is not null && !string.Equals(e.Status, StatusUserCanceled, StringComparison.Ordinal);

    public static string DurationLabel(VideoDurationMode mode) => mode == VideoDurationMode.Sec15_AmbientSlow ? AmbientModeLabel : mode.ToString();

    /// <summary>렌더 작업 → 저널 항목.</summary>
    public static CloudTaskJournalEntry EntryFor(BatchRenderJob job, string taskId, string engine, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new CloudTaskJournalEntry(taskId, engine, job.Prompt, DurationLabel(job.DurationMode), StatusProcessing, now,
            string.IsNullOrWhiteSpace(job.OutputPath) ? null : job.OutputPath, job.Resolution, job.Seed,
            MultiDurationPlanner.MetaOf(job, MultiDurationPlanner.MetaProject),
            string.Equals(MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaAmbientPingPong), "true", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CloudTaskJournalEntry> LoadAll()
    {
        lock (_gate) return ReadUnsafe().ToArray();
    }

    /// <summary>발번 즉시 기록(동일 task_id 는 갱신 · 중복 차단).</summary>
    public bool Record(CloudTaskJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.TaskId)) return false;
        bool ok;
        lock (_gate)
        {
            var list = ReadUnsafe();
            var idx = IndexOf(list, entry.TaskId);
            if (idx >= 0) list[idx] = entry; else list.Add(entry);
            ok = WriteUnsafe(list);
        }
        Raise();
        return ok;
    }

    /// <summary>완료(로컬 저장 성공) 또는 원격 종료 실패 시 안전 제거.</summary>
    public bool Remove(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        bool removed;
        lock (_gate)
        {
            var list = ReadUnsafe();
            var idx = IndexOf(list, taskId);
            removed = idx >= 0;
            if (removed) { list.RemoveAt(idx); WriteUnsafe(list); }
        }
        if (removed) Raise();
        return removed;
    }

    /// <summary>상태 필드만 갱신(예: Interrupted · UserCanceled). 변경 시 true.</summary>
    public bool MarkStatus(string? taskId, string status)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        lock (_gate)
        {
            var list = ReadUnsafe();
            var idx = IndexOf(list, taskId);
            if (idx < 0 || string.Equals(list[idx].Status, status, StringComparison.Ordinal)) return false;
            list[idx] = list[idx] with { Status = status };
            WriteUnsafe(list);
        }
        Raise();
        return true;
    }

    /// <summary>사용자 [취소] 확정 — 자동 회수 제외.</summary>
    public bool MarkUserCanceled(string? taskId) => MarkStatus(taskId, StatusUserCanceled);

    /// <summary>[재시도] — UserCanceled/Failed/Interrupted → Processing 복귀(다음 자동 회수 대상 편입).</summary>
    public bool Retry(string? taskId) => MarkStatus(taskId, StatusProcessing);

    /// <summary>[저널에서 영구 삭제].</summary>
    public bool Purge(string? taskId) => Remove(taskId);

    /// <summary>[전체 비우기] — 삭제 건수 반환.</summary>
    public int PurgeAll()
    {
        int n;
        lock (_gate)
        {
            var list = ReadUnsafe();
            n = list.Count;
            if (n == 0) return 0;
            list.Clear();
            WriteUnsafe(list);
        }
        Raise();
        return n;
    }

    /// <summary>[수동 회수/다운로드] — 상태 무관 단건 회수(UserCanceled 포함).</summary>
    public Task<TaskRecoveryResult> RecoverTaskAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return Task.FromResult(TaskRecoveryResult.Empty);
        return RecoverCoreAsync(e => string.Equals(e.TaskId, taskId, StringComparison.Ordinal), ct);
    }

    /// <summary>앱 시작 시 1회 복구 + 네트워크 재연결 감시(멱등).</summary>
    public void StartAutoRecovery()
    {
        if (Interlocked.Exchange(ref _netHooked, 1) == 1) return;
        try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { Program.Log("TaskJournal.NetHook", ex); }
        _ = RecoverSafeAsync();
    }

    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ = RecoverSafeAsync();
    }

    private async Task RecoverSafeAsync()
    {
        try { await RecoverAsync().ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("TaskJournal.Recover", ex); }
    }

    /// <summary>미완료 저널 자동 복구(UserCanceled 제외) — 재진입 차단(진행 중이면 Empty).</summary>
    public Task<TaskRecoveryResult> RecoverAsync(CancellationToken ct = default) => RecoverCoreAsync(IsAutoRecoverable, ct);

    private async Task<TaskRecoveryResult> RecoverCoreAsync(Func<CloudTaskJournalEntry, bool> filter, CancellationToken ct)
    {
        var query = Query;
        if (query is null || Volatile.Read(ref _disposed) == 1) return TaskRecoveryResult.Empty;
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return TaskRecoveryResult.Empty;
        try
        {
            var all = LoadAll();
            var entries = new List<CloudTaskJournalEntry>(all.Count);
            for (var i = 0; i < all.Count; i++) if (filter(all[i])) entries.Add(all[i]);
            if (entries.Count == 0) return TaskRecoveryResult.Empty;
            Volatile.Write(ref _pendingCount, entries.Count);
            Raise();
            int recovered = 0, pending = 0, dropped = 0;
            var paths = new List<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await RecoverOneAsync(entries[i], query, ct).ConfigureAwait(false);
                if (outcome.Path is { } p) { recovered++; paths.Add(p); }
                else if (outcome.Dropped) dropped++;
                else pending++;
                Interlocked.Decrement(ref _pendingCount);
                Raise();
                if (outcome.Path is { } rp) RaiseRecovered(rp);
            }
            return new TaskRecoveryResult(entries.Count, recovered, pending, dropped, paths);
        }
        finally
        {
            Volatile.Write(ref _pendingCount, 0);
            Volatile.Write(ref _recovering, 0);
            Raise();
        }
    }

    private async Task<(string? Path, bool Dropped)> RecoverOneAsync(CloudTaskJournalEntry e, Func<string, CancellationToken, Task<WanTaskResult>> query, CancellationToken ct)
    {
        WanTaskResult st;
        try { st = await query(e.TaskId, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Program.Log("TaskJournal.Query", ex);
            return (null, false);   // 네트워크 단절 — 다음 재연결 시 재시도
        }

        if (st.Status is WanTaskStatus.Failed or WanTaskStatus.Canceled
            || (st.Status == WanTaskStatus.Unknown && st.Ok && UtcNow() - e.StartedAt > ResultUrlTtl))
        {
            Remove(e.TaskId);
            return (null, true);
        }
        if (st.Status != WanTaskStatus.Succeeded || string.IsNullOrWhiteSpace(st.VideoUrl)) return (null, false);

        var output = ResolveOutputPath(e);
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await (Downloader ?? VideoEngineRenderExecutionEngine.DefaultDownloadAsync)(st.VideoUrl, output, ct).ConfigureAwait(false);
            if (!RenderQueueService.HasLocalOutput(output)) return (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Program.Log("TaskJournal.Download", ex);
            return (null, false);
        }

        var final = output;
        if (e.IsAmbient && Ambient is { } amb)
        {
            try
            {
                var target = MultiDurationPlanner.BuildOutputPath(Path.GetDirectoryName(output) ?? "",
                    e.Project ?? Path.GetFileNameWithoutExtension(output), VideoDurationMode.Sec15_AmbientSlow);
                var res = await amb.RenderAsync(output, target, e.PingPong, ct).ConfigureAwait(false);
                if (res.OutputPath is { } p && File.Exists(p)) final = p;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Program.Log("TaskJournal.Ambient", ex);   // 원본 5초 유지(유실 금지)
            }
        }

        WriteGalleryMeta(e, output, UtcNow());
        if (!string.Equals(final, output, StringComparison.OrdinalIgnoreCase)) WriteGalleryMeta(e, final, UtcNow());
        Remove(e.TaskId);
        return (final, false);
    }

    public string ResolveOutputPath(CloudTaskJournalEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.OutputPath) && Path.IsPathRooted(e.OutputPath)) return e.OutputPath;
        string? root = null;
        try { root = MediaRootProvider?.Invoke(); } catch (Exception ex) { Program.Log("TaskJournal.MediaRoot", ex); }
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Media");
        return Path.Combine(root, "recovered_" + MultiDurationPlanner.SanitizeName(e.TaskId) + ".mp4");
    }

    /// <summary>갤러리 사이드카 메타(<see cref="GalleryMediaService"/> 스키마) — 실패 격리.</summary>
    internal static void WriteGalleryMeta(CloudTaskJournalEntry e, string mediaPath, DateTimeOffset now)
    {
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(512);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                w.WriteString("title", Path.GetFileNameWithoutExtension(mediaPath));
                w.WriteString("prompt", e.Prompt);
                w.WriteNumber("seed", e.Seed);
                w.WriteString("resolution", e.Resolution ?? "");
                w.WriteString("createdAt", now);
                w.WriteString("engine", e.Engine);
                w.WriteString("cloudTaskId", e.TaskId);
                w.WriteString("durationMode", e.DurationMode);
                w.WriteBoolean("recovered", true);
                w.WriteEndObject();
            }
            File.WriteAllBytes(GalleryMediaService.MetaPathFor(mediaPath), buf.WrittenSpan.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("TaskJournal.Meta", ex); }
    }

    private List<CloudTaskJournalEntry> ReadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new List<CloudTaskJournalEntry>();
            var list = JsonSerializer.Deserialize<List<CloudTaskJournalEntry>>(File.ReadAllText(_path), s_json);
            if (list is null) return new List<CloudTaskJournalEntry>();
            list.RemoveAll(static x => x is null || string.IsNullOrWhiteSpace(x.TaskId));   // 스키마 즉시 검증
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Program.Log("TaskJournal.Read", ex);
            return new List<CloudTaskJournalEntry>();
        }
    }

    private bool WriteUnsafe(List<CloudTaskJournalEntry> list)
    {
        var tmp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (list.Count == 0) { if (File.Exists(_path)) File.Delete(_path); Volatile.Write(ref _count, 0); return true; }
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, s_json));
            File.Move(tmp, _path, overwrite: true);
            Volatile.Write(ref _count, list.Count);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Program.Log("TaskJournal.Write", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static int IndexOf(List<CloudTaskJournalEntry> list, string taskId)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i].TaskId, taskId, StringComparison.Ordinal)) return i;
        return -1;
    }

    private void Raise()
    {
        var h = OnChanged;
        if (h is null) return;
        try { h(); } catch (Exception ex) { Program.Log("TaskJournal.OnChanged", ex); }
    }

    private void RaiseRecovered(string path)
    {
        var h = OnRecovered;
        if (h is null) return;
        try { h(path); } catch (Exception ex) { Program.Log("TaskJournal.OnRecovered", ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (Volatile.Read(ref _netHooked) == 1)
        {
            try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; }
            catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { Program.Log("TaskJournal.NetUnhook", ex); }
        }
        OnChanged = null;
        OnRecovered = null;
    }
}
