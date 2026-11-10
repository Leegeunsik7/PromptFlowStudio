using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🛡️ Pluto JSON 적재 결과.</summary>
internal enum PlutoLoadStatus
{
    /// <summary>파일 없음(첫 실행) — 빈 상태로 시작 · 저장 허용.</summary>
    Missing = 0,
    /// <summary>정상 적재.</summary>
    Ok = 1,
    /// <summary>JSON 손상 — <c>.corrupt</c> 격리 사본 보존 후 빈 상태로 시작 · 저장 허용.</summary>
    Corrupt = 2,
    /// <summary>재시도 소진 후에도 읽기 불가(공유 위반·권한) — 세션 내 메모리만 사용 · 디스크 덮어쓰기 금지(기존 학습 데이터 보호).</summary>
    Unreadable = 3,
}

/// <summary>
/// 🛡️ Pluto 4종 저장소(아이덴티티·트렌드 메모리·DNA·SNS 레이더) 공용 내구성 헬퍼.
/// 쓰기: 고유 임시 파일(<c>{path}.{guid}.tmp</c>) → <see cref="File.Move(string, string, bool)"/> 원자 교체 · 공유 위반(AV/인덱서/백업 리더) 시 선형 백오프 재시도 · 임시 파일 항상 정리.
/// 읽기: 공유 허용 스트림 · 일시적 IO 오류 재시도 · 손상 JSON 격리.
/// </summary>
internal static class PlutoJsonStore
{
    /// <summary>최대 시도 횟수(최초 포함).</summary>
    public const int MaxAttempts = 4;

    /// <summary>재시도 기본 지연(선형 1×·2×·3×).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>손상 파일 격리 사본 확장자.</summary>
    public const string CorruptSuffix = ".corrupt";

    public static async Task<(T? Data, PlutoLoadStatus Status)> TryLoadAsync<T>(string path, string tag, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (default, PlutoLoadStatus.Missing);
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                var data = await JsonSerializer.DeserializeAsync<T>(fs, cancellationToken: ct).ConfigureAwait(false);
                return (data, PlutoLoadStatus.Ok);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine("[" + tag + "] corrupt json: " + ex.Message);
                Quarantine(path, tag);
                return (default, PlutoLoadStatus.Corrupt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MaxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine("[" + tag + "] load failed (read-only session): " + ex.Message);
                    return (default, PlutoLoadStatus.Unreadable);
                }
                await Task.Delay(RetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>원자적 기록 — 성공 true · IO 실패 false([부분 구제] 호출자 메모리 유지). 취소는 전파(임시 파일 정리 후).</summary>
    public static async Task<bool> WriteAtomicAsync(string path, string json, string tag, CancellationToken ct)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (attempt < MaxAttempts && (ex is IOException or UnauthorizedAccessException))
                {
                    await Task.Delay(RetryDelay * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[" + tag + "] persist failed: " + ex.Message);
            return false;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void Quarantine(string path, string tag)
    {
        try { File.Copy(path, path + CorruptSuffix, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[" + tag + "] quarantine failed: " + ex.Message); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[PlutoJsonStore] tmp cleanup: " + ex.Message); }
    }
}
