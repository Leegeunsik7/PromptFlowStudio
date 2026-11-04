using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Export;

/// <summary>StockSuite 내보내기 결과 — NeedsPathSetup=true 면 대상 경로 미설정/무효(환경설정 안내).</summary>
public sealed record StockSuiteExportResult(bool Ok, string? TargetPath, bool NeedsPathSetup, string Message);

/// <summary>
/// [PromptFlowStudio] 💼 StockSuite 원클릭 에셋 내보내기 — 선택 MP4 를 HTS/StockSuite 에셋 폴더에 표준 명칭(<c>bg_ambient.mp4</c>)으로 원자적 덮어쓰기.
///   ▸ 대상 폴더 내 임시 파일(<c>.~pfs</c>)로 복사 → <see cref="File.Move(string,string,bool)"/> 교체(동일 볼륨 원자적).
///   ▸ 경로는 환경설정(<c>UserPreferenceService.StockSuiteAssetPath</c>) 공급자 · 명시 인자 우선.
/// </summary>
public sealed class StockSuiteExporter
{
    public const string DefaultAssetName = "bg_ambient.mp4";
    private const string TempExt = ".~pfs";

    private readonly Func<string?> _targetDir;

    public StockSuiteExporter() : this(null) { }

    /// <param name="targetDirProvider">설정된 StockSuite 에셋 경로 공급자(null/빈 값 = 미설정).</param>
    public StockSuiteExporter(Func<string?>? targetDirProvider) => _targetDir = targetDirProvider ?? (static () => null);

    public string? ConfiguredDirectory
    {
        get
        {
            try { var d = _targetDir(); return string.IsNullOrWhiteSpace(d) ? null : d.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Path", ex); return null; }
        }
    }

    public bool IsConfigured => ConfiguredDirectory is not null;

    /// <summary>원본 MP4 → <c>{targetDir}\{assetName}</c> 원자적 덮어쓰기. 대상 폴더는 상위가 존재할 때만 생성.</summary>
    public async Task<StockSuiteExportResult> ExportAsync(string? sourcePath, string? targetDir = null, string? assetName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        if (dir is null) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");

        var name = string.IsNullOrWhiteSpace(assetName) ? DefaultAssetName : Path.GetFileName(assetName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = DefaultAssetName;

        string target, temp = "";
        try
        {
            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir))
            {
                var parent = Path.GetDirectoryName(fullDir);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullDir + " — 환경설정에서 경로를 재설정하세요.");
                Directory.CreateDirectory(fullDir);
            }
            target = Path.Combine(fullDir, name);
            if (string.Equals(Path.GetFullPath(sourcePath), target, StringComparison.OrdinalIgnoreCase))
                return new(true, target, false, "이미 StockSuite 에셋 위치의 파일입니다.");

            temp = target + TempExt;
            await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, target, overwrite: true);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(sourcePath).Length)
                return new(false, null, false, "복사 검증 실패: " + name);
            return new(true, target, false, "💼 StockSuite 내보내기 완료 → " + target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return new(false, null, false, "취소됨");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Export", ex);
            TryDelete(temp);
            return new(false, null, ex is UnauthorizedAccessException or ArgumentException or NotSupportedException, "StockSuite 내보내기 실패: " + ex.Message + " — 경로를 확인하세요.");
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
