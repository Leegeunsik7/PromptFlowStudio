using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞️ 렌더 완료 영상 메타 — 성과 피드백 귀속(훅 · 룩 · BGM · 태그) + 학습 반영 여부.</summary>
public sealed record PlutoRenderedClipRecord(string VideoId, string Title, string RenderMode, string HookStyle, string ColorLookId, string BgmMood,
    IReadOnlyList<string> Tags, string OutputPath, DateTimeOffset RenderedAtUtc, bool TelemetryLogged);

/// <summary>🎞️ 영속 렌더 카탈로그 — 시리즈 편별 · 재시작 후에도 최근 렌더 영상을 성과 피드백에 다시 선택.</summary>
public interface IPlutoRenderCatalogService
{
    /// <summary>렌더 기록(같은 VideoId → 최신 메타로 교체 후 맨 앞 · 상한 <see cref="PlutoRenderCatalogService.Capacity"/>건).</summary>
    Task RecordRenderedClipAsync(PlutoRenderedClipRecord record, CancellationToken ct = default);

    /// <summary>최신순 최대 <paramref name="maxCount"/>건.</summary>
    Task<IReadOnlyList<PlutoRenderedClipRecord>> GetRecentClipsAsync(int maxCount = 30, CancellationToken ct = default);

    /// <summary>VideoId(대소문자 무시) → 정규화 제목 → 정규화 VideoId 순 조회(없음 → null).</summary>
    Task<PlutoRenderedClipRecord?> FindByVideoIdOrTitleAsync(string videoIdOrTitle, CancellationToken ct = default);

    /// <summary>학습 반영 완료 표시(미추적 → 무시).</summary>
    Task MarkTelemetryLoggedAsync(string videoId, CancellationToken ct = default);

    /// <summary>카탈로그 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🎞️ <see cref="IPlutoRenderCatalogService"/> 구현 — <c>pluto_render_catalog.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
/// 적재 불가(잠금·권한) 세션은 기존 파일 덮어쓰기 금지 · 영속 실패 → [부분 구제] 메모리 유지.
/// </summary>
public sealed class PlutoRenderCatalogService : IPlutoRenderCatalogService, IPlutoHotReloadable, IDisposable
{
    public const string FileName = "pluto_render_catalog.json";
    public const int Capacity = 100;
    public const int MaxFieldLength = 300;
    public const int MaxTags = 12;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PlutoRenderedClipRecord> _clips = new();   // 최신순
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — Drive 우선 · 로컬 폴백.</summary>
    public PlutoRenderCatalogService(IPlutoStorageManager storage) : this((string?)null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoRenderCatalogService(string? storagePath)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    public event Action? Changed;

    /// <summary>직렬화 DTO.</summary>
    public sealed class ClipDto
    {
        public string VideoId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? RenderMode { get; set; }
        public string? HookStyle { get; set; }
        public string? ColorLookId { get; set; }
        public string? BgmMood { get; set; }
        public string[]? Tags { get; set; }
        public string? OutputPath { get; set; }
        public DateTimeOffset RenderedAtUtc { get; set; }
        public bool TelemetryLogged { get; set; }
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<ClipDto>? Clips { get; set; }
    }

    /// <summary>매칭 키 정규화 — 확장자 제거 · 소문자 · 문자/숫자만(공백·구두점·이모지 제거).</summary>
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var s = value.Trim();
        var ext = Path.GetExtension(s);
        if (ext.Length is > 1 and <= 5 && (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)))
            s = s[..^ext.Length];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string Clip(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim() is { Length: > MaxFieldLength } t ? t[..MaxFieldLength] : s.Trim();

    private static PlutoRenderedClipRecord Sanitize(PlutoRenderedClipRecord r)
    {
        var tags = new List<string>(Math.Min(r.Tags?.Count ?? 0, MaxTags));
        if (r.Tags is not null)
            foreach (var t in r.Tags)
            {
                if (tags.Count >= MaxTags) break;
                var c = Clip(t);
                if (c.Length > 0) tags.Add(c);
            }
        return new PlutoRenderedClipRecord(Clip(r.VideoId), Clip(r.Title), Clip(r.RenderMode), Clip(r.HookStyle), Clip(r.ColorLookId), Clip(r.BgmMood),
            tags.ToArray(), r.OutputPath?.Trim() ?? string.Empty, r.RenderedAtUtc, r.TelemetryLogged);
    }

    private static PlutoRenderedClipRecord FromDto(ClipDto d) => new(d.VideoId, d.Title ?? "", d.RenderMode ?? "", d.HookStyle ?? "", d.ColorLookId ?? "",
        d.BgmMood ?? "", d.Tags ?? Array.Empty<string>(), d.OutputPath ?? "", d.RenderedAtUtc, d.TelemetryLogged);

    private static ClipDto ToDto(PlutoRenderedClipRecord r) => new()
    {
        VideoId = r.VideoId, Title = r.Title, RenderMode = r.RenderMode, HookStyle = r.HookStyle, ColorLookId = r.ColorLookId, BgmMood = r.BgmMood,
        Tags = r.Tags is string[] arr ? arr : new List<string>(r.Tags).ToArray(), OutputPath = r.OutputPath, RenderedAtUtc = r.RenderedAtUtc, TelemetryLogged = r.TelemetryLogged,
    };

    private async Task EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.RenderCatalog", ct).ConfigureAwait(false);
        _readOnly = status == PlutoLoadStatus.Unreadable;
        _clips = BuildList(data);
        _loaded = true;
    }

    private static List<PlutoRenderedClipRecord> BuildList(Snapshot? data)
    {
        var list = new List<PlutoRenderedClipRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data?.Clips is { } clips)
            foreach (var c in clips)
            {
                if (c is null || string.IsNullOrWhiteSpace(c.VideoId) || !seen.Add(c.VideoId.Trim())) continue;
                list.Add(Sanitize(FromDto(c)));
                if (list.Count >= Capacity) break;
            }
        return list;
    }

    // ── ♻️ Step 20 — 실행 중 재적재 ──

    public string HubFileName => FileName;

    /// <summary>♻️ 게이트 안에서 대기 해결 승격 → 파싱 → 원자 교체(손상 · 부재 · 잠김 · 읽기 전용 세션 → 라이브 카탈로그 유지 + false).</summary>
    public async Task<bool> ReloadFromDiskAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            PlutoHotReload.PromotePending(_storage, FileName);
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.RenderCatalog.Reload", ct).ConfigureAwait(false);
            if (status != PlutoLoadStatus.Ok || data is null) return false;
            _clips = BuildList(data);
            _loaded = true;
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
    }

    public async Task RecordRenderedClipAsync(PlutoRenderedClipRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.VideoId)) throw new ArgumentException("VideoId required", nameof(record));
        var clean = Sanitize(record);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = _clips.Count - 1; i >= 0; i--)
                if (string.Equals(_clips[i].VideoId, clean.VideoId, StringComparison.OrdinalIgnoreCase)) _clips.RemoveAt(i);
            _clips.Insert(0, clean);
            if (_clips.Count > Capacity) _clips.RemoveRange(Capacity, _clips.Count - Capacity);
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public async Task<IReadOnlyList<PlutoRenderedClipRecord>> GetRecentClipsAsync(int maxCount = 30, CancellationToken ct = default)
    {
        if (maxCount <= 0) return Array.Empty<PlutoRenderedClipRecord>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var n = Math.Min(maxCount, _clips.Count);
            var result = new PlutoRenderedClipRecord[n];
            _clips.CopyTo(0, result, 0, n);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<PlutoRenderedClipRecord?> FindByVideoIdOrTitleAsync(string videoIdOrTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrTitle)) return null;
        var raw = videoIdOrTitle.Trim();
        var norm = NormalizeKey(raw);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            foreach (var c in _clips)
                if (string.Equals(c.VideoId, raw, StringComparison.OrdinalIgnoreCase)) return c;
            if (norm.Length == 0) return null;
            foreach (var c in _clips)
                if (string.Equals(NormalizeKey(c.Title), norm, StringComparison.Ordinal)) return c;
            foreach (var c in _clips)
                if (string.Equals(NormalizeKey(c.VideoId), norm, StringComparison.Ordinal)) return c;
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task MarkTelemetryLoggedAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return;
        var id = videoId.Trim();
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < _clips.Count; i++)
            {
                var c = _clips[i];
                if (!c.TelemetryLogged && string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase))
                {
                    _clips[i] = c with { TelemetryLogged = true };
                    changed = true;
                }
            }
            if (changed) await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed) RaiseChanged();
    }

    private async Task<bool> PersistLockedAsync(CancellationToken ct)
    {
        if (_readOnly) return false;   // 🛡️ 적재 불가 파일 보호
        try
        {
            var dtos = new List<ClipDto>(_clips.Count);
            foreach (var c in _clips) dtos.Add(ToDto(c));
            var json = JsonSerializer.Serialize(new Snapshot { Clips = dtos }, JsonOpts);
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.RenderCatalog", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.RenderCatalog] persist: " + ex.Message); return false; }   // [부분 구제]
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.RenderCatalog] Changed handler: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
