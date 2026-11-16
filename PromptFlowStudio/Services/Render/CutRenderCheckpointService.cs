using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Platform;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] 컷 단위 디스크 체크포인트 — 멀티 컷(10초·2컷 / 15초·3컷)·시리즈 렌더에서 완료된 컷을 보존해
/// 후속 컷이 일시 장애로 실패해도 재실행 시 프로바이더 재호출 없이 재사용.
/// </summary>
public interface ICutRenderCheckpointService
{
    /// <summary>정규화 입력의 결정적 SHA-256 소문자 hex(64자).</summary>
    string ComputeCutKey(string prompt, int cutIndex, int durationSeconds, string resolution, string provider);

    /// <summary><c>{key}.mp4</c> 실존 · 24시간 이내 · MP4 무결성 통과 시에만 true.</summary>
    bool TryGetValidCheckpoint(string cutKey, out string cachedMp4Path);

    /// <summary>원본 MP4 검증 → 임시 파일 → 원자 이동 저장 · 30개 초과 시 오래된 순 정리.</summary>
    void SaveCheckpoint(string cutKey, string sourceMp4Path);
}

/// <summary>무동작 체크포인트(레거시 직접 생성 엔진 기본값) — 항상 미스 · 저장 생략.</summary>
public sealed class NullCutRenderCheckpointService : ICutRenderCheckpointService
{
    public static readonly NullCutRenderCheckpointService Instance = new();
    private NullCutRenderCheckpointService() { }
    public string ComputeCutKey(string prompt, int cutIndex, int durationSeconds, string resolution, string provider)
        => CutRenderCheckpointService.ComputeKey(prompt, cutIndex, durationSeconds, resolution, provider);
    public bool TryGetValidCheckpoint(string cutKey, out string cachedMp4Path) { cachedMp4Path = string.Empty; return false; }
    public void SaveCheckpoint(string cutKey, string sourceMp4Path) { }
}

/// <summary>순수 BCL(SHA256 · FileStream) 컷 체크포인트 — <c>{LocalAppData}\PromptFlowStudio\cut_checkpoints</c>.</summary>
public sealed class CutRenderCheckpointService : ICutRenderCheckpointService
{
    public const string FolderName = "cut_checkpoints";
    public const int MaxEntries = 30;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Func<string> _root;
    private readonly object _gate = new();

    /// <summary>현재 시각(테스트 주입용).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    public CutRenderCheckpointService() : this(null) { }

    /// <param name="root">저장 폴더 해석기(null = <see cref="DefaultRoot"/>, 호출 시점 평가 → 환경변수 격리 반영).</param>
    public CutRenderCheckpointService(Func<string>? root) => _root = root ?? DefaultRoot;

    public static string DefaultRoot() => Path.Combine(AppDataPaths.LocalAppData, "PromptFlowStudio", FolderName);

    public string Root => _root();

    public string ComputeCutKey(string prompt, int cutIndex, int durationSeconds, string resolution, string provider)
        => ComputeKey(prompt, cutIndex, durationSeconds, resolution, provider);

    internal static string ComputeKey(string? prompt, int cutIndex, int durationSeconds, string? resolution, string? provider)
    {
        var p = (prompt ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var sb = new StringBuilder(p.Length + 64);
        sb.Append(p).Append('\u001F')
          .Append(cutIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\u001F')
          .Append(durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\u001F')
          .Append((resolution ?? "").Trim().ToLowerInvariant()).Append('\u001F')
          .Append((provider ?? "").Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>64자 소문자 hex 만 허용(경로 조작 차단).</summary>
    public static bool IsValidKey(string? key)
    {
        if (key is null || key.Length != 64) return false;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    public bool TryGetValidCheckpoint(string cutKey, out string cachedMp4Path)
    {
        cachedMp4Path = string.Empty;
        if (!IsValidKey(cutKey)) return false;
        string path;
        try { path = Path.Combine(Root, cutKey + ".mp4"); } catch (Exception) { return false; }
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                if (Clock().UtcDateTime - fi.LastWriteTimeUtc > MaxAge) { TryDelete(path); return false; }
                if (!ExportMediaIntegrityGuard.TryValidateMp4(path, out _)) { TryDelete(path); return false; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        cachedMp4Path = path;
        return true;
    }

    public void SaveCheckpoint(string cutKey, string sourceMp4Path)
    {
        if (!IsValidKey(cutKey)) throw new ArgumentException("잘못된 컷 키", nameof(cutKey));
        ExportMediaIntegrityGuard.EnsureValidMp4(sourceMp4Path);
        var dir = Root;
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, cutKey + ".mp4");
        var temp = Path.Combine(dir, cutKey + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var src = new FileStream(sourceMp4Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                src.CopyTo(dst);
                dst.Flush(flushToDisk: true);
            }
            lock (_gate)
            {
                File.Move(temp, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, Clock().UtcDateTime);
                Prune(dir);
            }
        }
        finally { TryDelete(temp); }
    }

    private static void Prune(string dir)
    {
        var files = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.mp4"))
            if (IsValidKey(Path.GetFileNameWithoutExtension(f.Name))) files.Add(f);
        if (files.Count <= MaxEntries) return;
        files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (var i = 0; i < files.Count - MaxEntries; i++) TryDelete(files[i].FullName);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// [PromptFlowStudio] 컷 단위 유한 지수 백오프 재시도 — 최대 2회(1500ms → 3500ms) · 일시 장애(네트워크 · 타임아웃 · 429/502/503/504)만
/// 재시도, 인증/검증(401 · 403 · 잘못된 키) 오류는 즉시 실패. 호출자 취소는 즉시 전파.
/// </summary>
public static class CutRetryPolicy
{
    public const int MaxRetries = 2;
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(3500) };

    /// <summary>
    /// <paramref name="operation"/> 실행 — 일시 예외 또는 <paramref name="isTransientResult"/>=true 결과면 백오프 후 재시도.
    /// 재시도 소진 시 마지막 결과 반환/마지막 예외 재던짐.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        Func<T, bool>? isTransientResult,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<int, string>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        delay ??= static (d, c) => Task.Delay(d, c);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            T result;
            try
            {
                result = await operation(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransientException(ex, ct))
            {
                onRetry?.Invoke(attempt + 1, ex.Message);
                await delay(Backoff[attempt], ct).ConfigureAwait(false);
                continue;
            }
            if (attempt < MaxRetries && isTransientResult is not null && isTransientResult(result))
            {
                onRetry?.Invoke(attempt + 1, "transient result");
                await delay(Backoff[attempt], ct).ConfigureAwait(false);
                continue;
            }
            return result;
        }
    }

    /// <summary>일시 예외 판정 — HttpRequestException(401/403 제외) · 호출자 미취소 TaskCanceledException/TimeoutException · 일시 상태 메시지.</summary>
    public static bool IsTransientException(Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case TaskCanceledException:
                return !ct.IsCancellationRequested;
            case OperationCanceledException:
                return false;
            case TimeoutException:
                return true;
            case HttpRequestException http:
                if (http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
                return !IsPermanentMessage(http.Message);
            default:
                return IsTransientMessage(ex.Message);
        }
    }

    /// <summary>일시 상태 메시지 — 429/502/503/504 · Throttling · timeout(영구 인증/검증 오류가 아닐 때만).</summary>
    public static bool IsTransientMessage(string? message)
    {
        if (string.IsNullOrEmpty(message) || IsPermanentMessage(message)) return false;
        return message.Contains("429", StringComparison.Ordinal) || message.Contains("502", StringComparison.Ordinal)
            || message.Contains("503", StringComparison.Ordinal) || message.Contains("504", StringComparison.Ordinal)
            || message.Contains("Throttl", StringComparison.OrdinalIgnoreCase) || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) || message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase) || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("시간 초과", StringComparison.Ordinal)
            || message.Contains("속도 제한", StringComparison.Ordinal);
    }

    /// <summary>영구 오류 — 401 · 403 · Unauthorized · Forbidden · InvalidApiKey · invalid key · AccessDenied · API 키.</summary>
    public static bool IsPermanentMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid api key", StringComparison.OrdinalIgnoreCase) || message.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("API 키", StringComparison.Ordinal);
    }
}
