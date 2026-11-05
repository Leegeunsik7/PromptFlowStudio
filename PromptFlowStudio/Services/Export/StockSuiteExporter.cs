using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Export;

/// <summary>StockSuite 내보내기 결과 — NeedsPathSetup=true 면 대상 경로 미설정/무효(환경설정 안내).</summary>
public sealed record StockSuiteExportResult(bool Ok, string? TargetPath, bool NeedsPathSetup, string Message);

/// <summary>💼 StockSuite 팩 일괄 내보내기 결과 — Exported = 넘버링된 대상 경로(순서 보존).</summary>
public sealed record StockSuiteBatchPackResult(IReadOnlyList<string> Exported, int Failed, bool NeedsPathSetup, string Message)
{
    public bool Ok => Exported.Count > 0;
}

/// <summary>StockSuite 내보내기 포맷 —MP4(기본 복사) · WebM VP9(HTS 저사양 최적화 트랜스코딩).</summary>
public enum StockSuiteExportFormat { Mp4, WebMVp9 }

/// <summary>StockSuite 실행 파일 경로 검증 상태.</summary>
public enum StockSuiteExeStatus { NotConfigured, NotFound, NotExecutable, Ok }

/// <summary>🚀 StockSuite 실행 결과 — AlreadyRunning=true 면 재실행 확인 필요.</summary>
public sealed record StockSuiteLaunchResult(bool Ok, bool AlreadyRunning, string Message);

/// <summary>🖥️ StockSuite 단일 내보내기 해상도 프로필 — 1080p(기본) · 4K UHD(3840×2160 Lanczos).</summary>
public enum StockSuiteResolution { Fhd1080, Uhd4K }

/// <summary>
/// 🧾 <c>.stocksuite_manifest.json</c> — 활성 에셋의 출처(렌더 ID · SHA-256 · 바이트) 및 직전 백업 출처.
/// 갤러리 역방향 치유는 이 매니페스트의 SHA-256/렌더 ID 로만 엄격 매칭(파일명 오탐 차단).
/// </summary>
public sealed record StockSuiteManifest(string ActiveAsset, string SourceRenderId, string SourceSha256, long SourceBytes, DateTimeOffset ExportedAt,
    string? BackupAsset = null, string? BackupRenderId = null, string? BackupSha256 = null, long BackupBytes = 0);

/// <summary>📦 HTS 데스크톱 울트라와이드 비율 — 16:9(1920×1080) · 21:9(2560×1080) · 32:9(3840×1080).</summary>
public enum HtsUltraAspect { Wide16x9, Ultra21x9, Super32x9 }

/// <summary>📦 올인원 멀티 패키지 옵션 — OutputDirectory null = 원본 옆 <c>{name}_package</c> · BgmPath 우선, 없으면 BgmPreset 합성.</summary>
public sealed record MultiExportOptions
{
    public HtsUltraAspect HtsAspect { get; init; } = HtsUltraAspect.Ultra21x9;
    public StockSuiteExportFormat HtsFormat { get; init; } = StockSuiteExportFormat.WebMVp9;
    public string? OutputDirectory { get; init; }
    public string? BgmPath { get; init; }
    public SnsBgmPreset BgmPreset { get; init; } = SnsBgmPreset.FintechSynthwave;
    /// <summary>⚡ HTS 규격에 60fps 모션 보간 결합.</summary>
    public bool Smooth60 { get; init; }
    /// <summary>🎨 HTS 규격 LUT 톤 매핑 번인(None = 원색).</summary>
    public LutProfile Lut { get; init; } = LutProfile.None;
    /// <summary>원본 길이(초) — ≤0 이면 Wan 기본 5초로 페이드아웃 시점 계산.</summary>
    public double DurationSec { get; init; }
}

/// <summary>패키지 단일 규격 — Key: <c>hts</c>|<c>mobile</c>|<c>shorts</c>.</summary>
public sealed record MultiExportProfile(string Key, string Label, int Width, int Height, string OutputPath, string[] Args);

/// <summary>📦 멀티 패키지 결과 — Generated = 성공 산출물 경로(규격 순서 보존) · [부분 구제] 실패분은 Failed 키.</summary>
public sealed record MultiExportResult(IReadOnlyList<string> Generated, IReadOnlyList<string> Failed, string? PackageDirectory, string Message)
{
    public bool Ok => Generated.Count > 0 && Failed.Count == 0;
}

/// <summary>
/// [PromptFlowStudio] 💼 StockSuite 원클릭 에셋 내보내기 — 선택 영상을 HTS/StockSuite 에셋 폴더에 표준 명칭으로 원자적 덮어쓰기.
///   ▸ MP4: ffmpeg <c>-c:v copy -an</c> 제로-오디오 스트립(부재/실패 시 원본 복사) → 대상 폴더 내 임시 파일(<c>.~pfs</c>) → <see cref="File.Move(string,string,bool)"/> 교체(동일 볼륨 원자적) → <c>bg_ambient.mp4</c>.
///   ▸ WebM: <c>ffmpeg -i in -c:v libvpx-vp9 -crf 32 -b:v 0 -an</c> → <c>bg_ambient.webm</c>. ffmpeg 부재/실패 시 MP4 복사로 폴백.
///   ▸ 적용 상태: 대상 옆 출처 마커(<c>.pfs-src</c>) 또는 MP4 SHA-256 일치 → <see cref="IsApplied"/>.
///   ▸ 경로·포맷은 환경설정 공급자 · 명시 인자 우선.
/// </summary>
public sealed partial class StockSuiteExporter
{
    public const string DefaultAssetName = "bg_ambient.mp4";
    public const string WebMAssetName = "bg_ambient.webm";
    public const string SourceMarkerSuffix = ".pfs-src";
    private const string TempExt = ".~pfs";

    private readonly Func<string?> _targetDir;
    private readonly Func<string?> _format;
    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;
    private readonly object _hashLock = new();
    private readonly Dictionary<string, bool> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _exePath;
    private readonly Func<bool> _sharpen;
    private readonly Func<bool> _smooth60;

    /// <summary>HTS 대화면 고선명 필터 — 1080p Lanczos 업스케일 + 언샵 마스크 샤프닝.</summary>
    public const string SharpenFilter = "scale=1920:1080:flags=lanczos,unsharp=5:5:0.8:3:3:0.4";
    /// <summary>⚡ 60fps 모션 보간(MCI · AOBMC · 양방향 ME) — 16~24fps AI 영상을 고주사율 모니터용으로 보간.</summary>
    public const string MotionInterpolateFilter = "minterpolate=fps=60:mi_mode=mci:mc_mode=aobmc:me_mode=bidir";
    /// <summary>⚡ 저사양 60fps — 프레임 복제(보간 없음).</summary>
    public const string Fps60LiteFilter = "fps=60";

    /// <summary>비디오 필터 체인 합성 — 샤프닝/스케일 뒤에 60fps 보간 결합. 둘 다 OFF 면 null(스트림 복사 경로).</summary>
    public static string? BuildVideoFilter(bool sharpen, bool smooth60, bool lowSpec = false)
    {
        var motion = lowSpec ? Fps60LiteFilter : MotionInterpolateFilter;
        if (sharpen) return smooth60 ? SharpenFilter + "," + motion : SharpenFilter;
        return smooth60 ? motion : null;
    }

    /// <summary>🖥️ 4K UHD 프로필 — 3840×2160 Lanczos 업스케일(샤프닝 ON 이면 Unsharp 결합).</summary>
    public const string Uhd4KScaleFilter = "scale=3840:2160:flags=lanczos";
    public const string Uhd4KSharpenFilter = Uhd4KScaleFilter + ",unsharp=5:5:0.8:3:3:0.4";
    /// <summary>핀테크 HUD 워터마크 문구(좌상단 · 우상단).</summary>
    public const string WatermarkLeftText = "ANG QUANT LAB · REALTIME STREAM";
    public const string WatermarkRightText = "● LIVE KST";

    /// <summary>스케일/샤프닝 단계 — 4K 는 샤프닝 여부와 무관하게 Lanczos 업스케일.</summary>
    public static string? BuildScaleFilter(bool sharpen, StockSuiteResolution resolution)
        => resolution == StockSuiteResolution.Uhd4K ? (sharpen ? Uhd4KSharpenFilter : Uhd4KScaleFilter) : sharpen ? SharpenFilter : null;

    /// <summary>drawtext 텍스트 이스케이프 — <c>\ ' : %</c> 를 필터그래프 규칙으로 보호.</summary>
    private static string EscapeDrawText(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "’", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal);

    /// <summary>
    /// 📺 핀테크 HUD 워터마크 drawtext 체인 — 좌상단 브랜드, 우상단 LIVE 배지. <paramref name="fontFile"/> null 이면 fontfile 생략(ffmpeg 기본 폰트).
    /// 4K 는 폰트/여백 2배.
    /// </summary>
    public static string BuildWatermarkFilter(string? fontFile, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080)
    {
        var k = resolution == StockSuiteResolution.Uhd4K ? 2 : 1;
        var font = string.IsNullOrWhiteSpace(fontFile) ? "" : "fontfile='" + fontFile.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal) + "':";
        var size = (24 * k).ToString(CultureInfo.InvariantCulture);
        var m = (40 * k).ToString(CultureInfo.InvariantCulture);
        var bw = (6 * k).ToString(CultureInfo.InvariantCulture);
        var style = ":fontsize=" + size + ":fontcolor=white@0.7:box=1:boxcolor=black@0.4:boxborderw=" + bw;
        return "drawtext=" + font + "text='" + EscapeDrawText(WatermarkLeftText) + "':x=" + m + ":y=" + m + style
            + ",drawtext=" + font + "text='" + EscapeDrawText(WatermarkRightText) + "':x=w-tw-" + m + ":y=" + m + style;
    }

    /// <summary>Windows 기본 폰트 탐색(맑은 고딕 → Arial · 미설치/비Windows → null = fontfile 생략 폴백).</summary>
    public static string? ResolveWatermarkFont()
    {
        try
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(fonts)) return null;
            foreach (var f in new[] { "malgun.ttf", "arial.ttf", "segoeui.ttf" })
            {
                var p = Path.Combine(fonts, f);
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Program.Log("StockSuite.Font", ex); }
        return null;
    }

    /// <summary>필터 체인 합성 — 스케일(1080p/4K) → 60fps → 🎨 LUT(eq) → 워터마크 순(워터마크 원색 유지). 전부 OFF 면 null.</summary>
    public static string? BuildVideoFilter(bool sharpen, bool smooth60, StockSuiteResolution resolution, bool watermark, string? fontFile, bool lowSpec = false, LutProfile lut = LutProfile.None)
    {
        var parts = new List<string>(4);
        if (BuildScaleFilter(sharpen, resolution) is { } s) parts.Add(s);
        if (smooth60) parts.Add(lowSpec ? Fps60LiteFilter : MotionInterpolateFilter);
        if (ColorGradingService.BuildFfmpegEqFilter(lut) is { } eq) parts.Add(eq);
        if (watermark) parts.Add(BuildWatermarkFilter(fontFile, resolution));
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    /// <summary>환경설정 60fps 모션 스무딩 기본값(공급자 예외 → OFF).</summary>
    public bool DefaultSmooth60
    {
        get
        {
            try { return _smooth60(); }
            catch (Exception ex) { Program.Log("StockSuite.Smooth60", ex); return false; }
        }
    }

    /// <summary>🎨 단건 내보내기 LUT 프로필(작업실 드롭다운 결선) — FFmpeg <c>eq</c> 체인 번인.</summary>
    public LutProfile Lut { get; set; } = LutProfile.None;

    /// <summary>🛡️ 대용량 인코딩(4K·60fps·듀얼 분할) 착수 전 디스크 Fail-Fast 센티넬.</summary>
    public PreFlightResourceSentinel PreFlight { get; set; } = new();

    /// <summary>대상 경로(null = 설정 경로)에 예상 산출 크기만큼 여유가 있는지 — 조회 불가 시 true.</summary>
    public bool CanSafelyEncode(long estimatedOutputSize, string? targetDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        return dir is null || PreFlight.CheckDiskSpace(dir, estimatedOutputSize).Ok;
    }

    public StockSuiteExporter() : this(null) { }

    /// <param name="targetDirProvider">설정된 StockSuite 에셋 경로 공급자(null/빈 값 = 미설정).</param>
    /// <param name="formatProvider">기본 포맷 공급자(<c>mp4</c>|<c>webm</c> · null = mp4).</param>
    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null = <see cref="FfmpegLocator"/>).</param>
    /// <param name="runner">ffmpeg 실행기(테스트 주입 · null = <see cref="Engines.ProcessRunner"/>).</param>
    /// <param name="executableProvider">StockSuite.exe 경로 공급자(🚀 원클릭 실행 · null = 미설정).</param>
    /// <param name="sharpenProvider">고선명 샤프닝 기본값 공급자(null = OFF · 앱 DI 는 환경설정 기본 ON).</param>
    /// <param name="smooth60Provider">⚡ 60fps 모션 스무딩 기본값 공급자(null = OFF).</param>
    public StockSuiteExporter(Func<string?>? targetDirProvider, Func<string?>? formatProvider = null, Func<string?>? resolveFfmpeg = null, FfmpegRunner? runner = null, Func<string?>? executableProvider = null, Func<bool>? sharpenProvider = null, Func<bool>? smooth60Provider = null)
    {
        _exePath = executableProvider ?? (static () => null);
        _sharpen = sharpenProvider ?? (static () => false);
        _smooth60 = smooth60Provider ?? (static () => false);
        _targetDir = targetDirProvider ?? (static () => null);
        _format = formatProvider ?? (static () => null);
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _run = runner ?? DefaultRunAsync;
    }

    public string? ConfiguredDirectory
    {
        get
        {
            try { var d = _targetDir(); return string.IsNullOrWhiteSpace(d) ? null : d.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Path", ex); return null; }
        }
    }

    public bool IsConfigured => ConfiguredDirectory is not null;

    /// <summary>환경설정 기본 포맷 — <c>webm</c> 이면 VP9, 그 외 MP4.</summary>
    public StockSuiteExportFormat DefaultFormat
    {
        get
        {
            try { return ParseFormat(_format()); }
            catch (Exception ex) { Program.Log("StockSuite.Format", ex); return StockSuiteExportFormat.Mp4; }
        }
    }

    public static StockSuiteExportFormat ParseFormat(string? value)
        => string.Equals(value?.Trim(), "webm", StringComparison.OrdinalIgnoreCase) ? StockSuiteExportFormat.WebMVp9 : StockSuiteExportFormat.Mp4;

    public static string FormatLabel(StockSuiteExportFormat f) => f == StockSuiteExportFormat.WebMVp9 ? "WebM VP9" : "MP4";

    /// <summary>환경설정 고선명 샤프닝 기본값(공급자 예외 → OFF).</summary>
    public bool DefaultSharpen
    {
        get
        {
            try { return _sharpen(); }
            catch (Exception ex) { Program.Log("StockSuite.Sharpen", ex); return false; }
        }
    }

    /// <summary>원본 → <c>{targetDir}\{assetName}</c> 원자적 덮어쓰기. 대상 폴더는 상위가 존재할 때만 생성. <paramref name="format"/>·<paramref name="sharpen"/> null = 환경설정 기본.</summary>
    public async Task<StockSuiteExportResult> ExportAsync(string? sourcePath, string? targetDir = null, string? assetName = null, CancellationToken ct = default, StockSuiteExportFormat? format = null, bool? sharpen = null, bool? smooth60 = null, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080, bool watermark = false)
        => await ExportAsync(sourcePath, targetDir, assetName, hotReload: true, ct, format, sharpen, smooth60, resolution, watermark).ConfigureAwait(false);

    /// <param name="hotReload">false = 핫리로드 신호 생략(스케줄러가 일괄 발행 후 1회 신호).</param>
    public async Task<StockSuiteExportResult> ExportAsync(string? sourcePath, string? targetDir, string? assetName, bool hotReload, CancellationToken ct = default, StockSuiteExportFormat? format = null, bool? sharpen = null, bool? smooth60 = null, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080, bool watermark = false)
    {
        var r = await ExportCoreAsync(sourcePath, targetDir, assetName, ct, format, sharpen, smooth60, resolution, watermark).ConfigureAwait(false);
        // 🔁 라이브 핫리로드 — 실제 교체가 일어난 경우에만 신호(동일 위치 no-op 제외).
        if (r.Ok && r.TargetPath is { } t && !string.Equals(Path.GetFullPath(sourcePath!), t, StringComparison.OrdinalIgnoreCase))
        {
            if (hotReload) WriteHotReloadTrigger(t, UtcNow());
            ExportMetadata.MarkExported(sourcePath!, new[] { t }, UtcNow());   // 📦 원본 메타 규격 통일
            WriteManifest(t, sourcePath!, UtcNow());                           // 🧾 엄격 매칭 매니페스트
        }
        return r;
    }

    // ══════════════════════ 🔁 원자적 세이프 스왑 & 핫리로드 브릿지 ══════════════════════

    /// <summary>StockSuite FileSystemWatcher 감시 대상 신호 파일 — <c>{ "updatedAt": ISO-8601 UTC, "file": "bg_ambient.mp4" }</c>.</summary>
    public const string HotReloadTriggerName = "hotreload.trigger";
    /// <summary>잠금 시 지연 재시도 횟수(최초 시도 제외).</summary>
    public const int SwapRetryCount = 3;
    /// <summary>재시도 기본 지연(선형 증가: 1×, 2×, 3×) — 테스트 단축 주입.</summary>
    public TimeSpan SwapRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>트리거 시각 공급자(테스트 주입).</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// 임시 파일 → 대상 원자적 교체. StockSuite 가 대상을 잠그고 있으면(공유 위반) 즉시 실패하지 않고
    /// <see cref="SwapRetryCount"/>회 지연 재시도. 최종 실패 시 임시 파일은 호출부 catch 가 정리.
    /// </summary>
    private async Task SwapIntoPlaceAsync(string temp, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < SwapRetryCount && ex is IOException or UnauthorizedAccessException)
            {
                Program.Log("StockSuite.SwapRetry", ex);
                await Task.Delay(SwapRetryDelay * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 재시도 소진 — 잠금(공유 위반)을 경로 설정 오류로 오인하지 않도록 IOException 으로 정규화.
                throw new IOException("대상 파일이 다른 프로세스(StockSuite)에 의해 잠겨 있습니다: " + Path.GetFileName(target), ex);
            }
        }
    }

    /// <summary>핫리로드 트리거 JSON 페이로드(정규 · camelCase · UTC ISO-8601).</summary>
    public static string BuildHotReloadTriggerJson(string fileName, DateTimeOffset updatedAt)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new System.Text.Json.Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("updatedAt", updatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("file", fileName);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>대상 폴더에 <see cref="HotReloadTriggerName"/> 원자적 기록(임시 → 교체). 실패는 격리(내보내기 결과 불변).</summary>
    private static void WriteHotReloadTrigger(string target, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir)) return;
        WriteHotReloadTrigger(dir, Path.GetFileName(target), now);
    }

    /// <summary><paramref name="dir"/>/<see cref="HotReloadTriggerName"/> 원자적 기록 — <paramref name="fileName"/> = 갱신 파일(예: <c>playlist.json</c>). 실패 격리.</summary>
    internal static void WriteHotReloadTrigger(string dir, string fileName, DateTimeOffset now)
    {
        var path = Path.Combine(dir, HotReloadTriggerName);
        var tmp = path + TempExt;
        try
        {
            File.WriteAllText(tmp, BuildHotReloadTriggerJson(fileName, now));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.HotReload", ex);
            TryDelete(tmp);
        }
    }

    private async Task<StockSuiteExportResult> ExportCoreAsync(string? sourcePath, string? targetDir, string? assetName, CancellationToken ct, StockSuiteExportFormat? format, bool? sharpen, bool? smooth60, StockSuiteResolution resolution, bool watermark)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        if (dir is null) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");

        var fmt = format ?? DefaultFormat;
        var webm = fmt == StockSuiteExportFormat.WebMVp9;
        var name = string.IsNullOrWhiteSpace(assetName) ? (webm ? WebMAssetName : DefaultAssetName) : Path.GetFileName(assetName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = webm ? WebMAssetName : DefaultAssetName;
        if (webm) name = Path.ChangeExtension(name, ".webm");

        string temp = "";
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
            var target = Path.Combine(fullDir, name);
            if (string.Equals(Path.GetFullPath(sourcePath), target, StringComparison.OrdinalIgnoreCase))
                return new(true, target, false, "이미 StockSuite 에셋 위치의 파일입니다.");
            if (resolution == StockSuiteResolution.Uhd4K || (smooth60 ?? DefaultSmooth60))
            {
                var pre = PreFlight.CheckDiskSpace(fullDir);   // 🛡️ 4K/60fps 대용량 인코딩 Fail-Fast(백업·인코딩 착수 전)
                if (!pre.Ok) return new(false, null, false, pre.Message!);
            }
            BackupActiveAsset(target, sourcePath);   // ↩️ 덮어쓰기 직전 이전 배경 보존

            var sharpenOn = sharpen ?? DefaultSharpen;
            var smoothOn = smooth60 ?? DefaultSmooth60;
            var uhd = resolution == StockSuiteResolution.Uhd4K;
            var lut = Lut;
            var filter = BuildVideoFilter(sharpenOn, smoothOn, resolution, watermark, watermark ? ResolveWatermarkFont() : null, lut: lut);
            // 워터마크 폰트/drawtext 실패 대비 — 워터마크 제외 체인으로 1회 재시도.
            var filterNoWm = watermark ? BuildVideoFilter(sharpenOn, smoothOn, resolution, false, null, lut: lut) : null;
            var tag = (uhd ? "🖥️ 4K UHD · " : sharpenOn ? "🔎 1080p 고선명 · " : "") + (smoothOn ? "⚡ 60fps 스무딩 · " : "") + (watermark ? "📺 HUD 워터마크 · " : "") + (lut != LutProfile.None ? "🎨 " + ColorGradingService.Label(lut) + " · " : "") + "🔇 무음";
            if (webm && (filter is not null || !Path.GetExtension(sourcePath).Equals(".webm", StringComparison.OrdinalIgnoreCase)))
            {
                temp = Path.Combine(fullDir, Path.GetFileNameWithoutExtension(name) + TempExt + ".webm");
                // 🔎 WebM 고선명/⚡60fps: 필터 체인(VP9 · 무음). 실패 → 일반 VP9 → MP4 복사 순 폴백.
                if (filter is not null)
                {
                    var sharpenedWebm = await TryFfmpegAsync(BuildWebMArgs(sourcePath, temp, filter), temp, ct).ConfigureAwait(false);
                    if (sharpenedWebm is not null && filterNoWm is not null)
                    {
                        TryDelete(temp);
                        sharpenedWebm = await TryFfmpegAsync(BuildWebMArgs(sourcePath, temp, filterNoWm), temp, ct).ConfigureAwait(false);
                        if (sharpenedWebm is null) tag = tag.Replace("📺 HUD 워터마크 · ", "⚠ 워터마크 생략 · ", StringComparison.Ordinal);
                    }
                    if (sharpenedWebm is null)
                    {
                        await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                        WriteSourceMarker(target, sourcePath);
                        return new(true, target, false, "💼 StockSuite WebM(VP9) 내보내기 완료(" + tag + ") → " + target);
                    }
                    TryDelete(temp);
                }
                var transcoded = await TryTranscodeWebMAsync(sourcePath, temp, ct).ConfigureAwait(false);
                if (transcoded is null)
                {
                    await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                    WriteSourceMarker(target, sourcePath);
                    return new(true, target, false, "💼 StockSuite WebM(VP9) 내보내기 완료 → " + target);
                }
                // [폴백] ffmpeg 부재/실패 → MP4 원본 복사로 대체 보존(동일 기본 이름 · .mp4)
                TryDelete(temp);
                var fb = await CopyAsync(sourcePath, Path.Combine(fullDir, Path.ChangeExtension(name, ".mp4")), ct).ConfigureAwait(false);
                return fb.Ok ? fb with { Message = "⚠ WebM 변환 실패(" + transcoded + ") — MP4 로 대체: " + fb.TargetPath } : fb;
            }

            // 🔇 HTS 제로-오디오: 비디오 스트림 무손실 copy + -an(오디오 드롭). ffmpeg 부재/실패 → 원본 복사 폴백.
            temp = Path.Combine(fullDir, Path.GetFileNameWithoutExtension(name) + TempExt + Path.GetExtension(name));
            // 🔎 HTS 대화면 고선명: 1080p Lanczos + Unsharp 재인코딩(무음). 실패 → 제로-오디오 스트림 복사 → 원본 복사 순 폴백.
            if (!webm && filter is not null)
            {
                // 🚀 1080p/4K · 60fps: 감지된 GPU 인코더(NVENC/QSV/AMF) 우선 → 실패 시 libx264 무중단 폴백.
                var gpu = await ResolveGpuVendorAsync(ct).ConfigureAwait(false);
                var sharpened = await TryEncodeMp4Async(sourcePath, temp, filter, gpu, ct).ConfigureAwait(false);
                if (sharpened is not null && filterNoWm is not null)
                {
                    TryDelete(temp);
                    sharpened = await TryEncodeMp4Async(sourcePath, temp, filterNoWm, gpu, ct).ConfigureAwait(false);
                    if (sharpened is null) tag = tag.Replace("📺 HUD 워터마크 · ", "⚠ 워터마크 생략 · ", StringComparison.Ordinal);
                }
                if (sharpened is null)
                {
                    await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                    WriteSourceMarker(target, sourcePath);
                    if (gpu != GpuEncoderVendor.Cpu && Accel?.Active == gpu) tag = "🚀 " + HardwareAccelDetector.Label(gpu) + " · " + tag;
                    return new(true, target, false, "💼 StockSuite 내보내기 완료(" + tag + ") → " + target);
                }
                TryDelete(temp);
            }
            var stripped =await TryFfmpegAsync(BuildZeroAudioArgs(sourcePath, temp), temp, ct).ConfigureAwait(false);
            if (stripped is null)
            {
                await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                WriteSourceMarker(target, sourcePath);
                return new(true, target, false, "💼 StockSuite 내보내기 완료(🔇 무음 HTS 최적화) → " + target);
            }
            TryDelete(temp);
            temp = "";
            return await CopyAsync(sourcePath, target, ct).ConfigureAwait(false);
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

    private async Task<StockSuiteExportResult> CopyAsync(string sourcePath, string target, CancellationToken ct)
    {
        var temp = target + TempExt;
        try
        {
            await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        var name = Path.GetFileName(target);
        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(sourcePath).Length)
            return new(false, null, false, "복사 검증 실패: " + name);
        WriteSourceMarker(target, sourcePath);
        return new(true, target, false, "💼 StockSuite 내보내기 완료 → " + target);
    }

    /// <summary>WebM 변환 — 성공 시 null, 실패 시 사유 문자열.</summary>
    private Task<string?> TryTranscodeWebMAsync(string source, string output, CancellationToken ct)
        => TryFfmpegAsync(BuildWebMArgs(source, output), output, ct);

    /// <summary>ffmpeg 실행 + 출력 무결성 검증 — 성공 시 null, 실패 시 사유 문자열.</summary>
    private async Task<string?> TryFfmpegAsync(string[] args, string output, CancellationToken ct)
    {
        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("StockSuite.Ffmpeg", ex); ffmpeg = null; }
        if (string.IsNullOrWhiteSpace(ffmpeg)) return "ffmpeg 미탐지";
        var ok = await _run(ffmpeg, args, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ok) return "ffmpeg 비정상 종료";
        return AmbientLoopService.IsValidOutput(output) ? null : "출력 파일 없음/손상";
    }

    /// <summary>🚀 GPU 가속 감지기(null = CPU 전용 · 앱 DI 는 <see cref="HardwareAccelDetector.Shared"/>).</summary>
    public HardwareAccelDetector? Accel { get; set; }

    private async Task<GpuEncoderVendor> ResolveGpuVendorAsync(CancellationToken ct)
    {
        var d = Accel;
        if (d is null) return GpuEncoderVendor.Cpu;
        try { return await d.DetectAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("StockSuite.HwAccel", ex); return GpuEncoderVendor.Cpu; }
    }

    /// <summary>MP4 재인코딩 — GPU 우선 시도, 실패 시 감지기 강등(<see cref="HardwareAccelDetector.MarkFailed"/>) 후 libx264 재시도. 성공 null.</summary>
    private async Task<string?> TryEncodeMp4Async(string source, string output, string filter, GpuEncoderVendor gpu, CancellationToken ct)
    {
        if (gpu != GpuEncoderVendor.Cpu && Accel?.Active == gpu)
        {
            var hw = await TryFfmpegAsync(BuildSharpenArgs(source, output, filter, gpu), output, ct).ConfigureAwait(false);
            if (hw is null) return null;
            Program.Log("StockSuite.HwAccel", new InvalidOperationException(HardwareAccelDetector.H264Encoder(gpu) + " 실패(" + hw + ") — libx264 폴백"));
            Accel?.MarkFailed();
            TryDelete(output);
        }
        return await TryFfmpegAsync(BuildSharpenArgs(source, output, filter), output, ct).ConfigureAwait(false);
    }

    /// <summary>HTS 배경용 VP9 인자 — <see cref="AmbientLoopService.BuildWebMArgs"/> 위임.</summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath) => AmbientLoopService.BuildWebMArgs(inputPath, outputPath);

    /// <summary>VP9 인자 — <paramref name="sharpen"/>=true 면 <see cref="SharpenFilter"/>(1080p Lanczos + Unsharp) 주입 · <c>-an</c> 무음 유지.</summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath, bool sharpen)
        => sharpen ? BuildWebMArgs(inputPath, outputPath, SharpenFilter) : AmbientLoopService.BuildWebMArgs(inputPath, outputPath);

    /// <summary>VP9 인자 — 임의 필터 체인(<see cref="BuildVideoFilter"/>) 주입 · <c>-an</c> 무음 유지.</summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath, string videoFilter)
        => ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", videoFilter, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-an", "-sn", "-dn", outputPath];

    /// <summary>팩 넘버링 이름 — <c>bg_ambient_01.mp4</c> / <c>.webm</c>(1-기반, 최소 2자리).</summary>
    public static string BatchPackAssetName(int index, StockSuiteExportFormat format)
        => "bg_ambient_" + index.ToString("00", CultureInfo.InvariantCulture) + (format == StockSuiteExportFormat.WebMVp9 ? ".webm" : ".mp4");

    /// <summary>
    /// 💼 대량생산 StockSuite 팩 일괄 내보내기 — 유효 영상을 입력 순서대로 <c>bg_ambient_01</c>, <c>02</c>… 로 정규화 변환/복사.
    /// 개별 실패는 격리([부분 구제] — 성공분 보존), 중복 경로는 1회만. <paramref name="targetDirectory"/> 빈 값 = 환경설정 경로.
    /// </summary>
    public async Task<StockSuiteBatchPackResult> ExportBatchPackAsync(IEnumerable<string> videoPaths, string targetDirectory, bool sharpen, CancellationToken ct = default, StockSuiteExportFormat? format = null, bool? smooth60 = null)
    {
        ArgumentNullException.ThrowIfNull(videoPaths);
        var fmt = format ?? DefaultFormat;
        var exported = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = 0;
        var index = 0;
        string? lastError = null;
        foreach (var p in videoPaths)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { failed++; continue; }
            string full;
            try { full = Path.GetFullPath(p); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { failed++; continue; }
            if (!seen.Add(full)) continue;   // alreadyRegistered 중복 차단
            index++;
            var r = await ExportAsync(full, targetDirectory, BatchPackAssetName(index, fmt), ct, fmt, sharpen, smooth60).ConfigureAwait(false);
            if (r.Ok && r.TargetPath is not null) exported.Add(r.TargetPath);
            else
            {
                failed++;
                lastError = r.Message;
                if (r.NeedsPathSetup) return new(exported, failed, true, r.Message);
            }
        }
        if (ct.IsCancellationRequested) return new(exported, failed, false, "취소됨 — " + exported.Count.ToString(CultureInfo.InvariantCulture) + "개 보존");
        var msg = exported.Count == 0
            ? "⚠ 내보낸 영상이 없습니다" + (lastError is null ? "." : " — " + lastError)
            : "💼 StockSuite 팩 " + exported.Count.ToString(CultureInfo.InvariantCulture) + "개 내보내기 완료" + (failed > 0 ? " (실패 " + failed.ToString(CultureInfo.InvariantCulture) + ")" : "") + " → " + Path.GetDirectoryName(exported[0]);
        return new(exported, failed, false, msg);
    }

    // ── 📦 올인원 멀티 타겟 패키지 ──
    /// <summary>Wan 기본 컷 길이 — 길이 미상 시 쇼츠 페이드아웃 시점 계산용.</summary>
    public const double DefaultClipSeconds = 5.0;
    /// <summary>9:16 세로형 스마트 중앙 크롭(모바일/쇼츠 공용).</summary>
    public const string VerticalCropFilter = "crop=ih*9/16:ih,scale=1080:1920:flags=lanczos";

    /// <summary>쇼츠 BGM 해석기(프리셋, 길이) → WAV 경로. 기본 = <see cref="AudioMuxService.EnsurePresetBgm"/>(로컬 합성).</summary>
    public Func<SnsBgmPreset, double, string?> BgmResolver { get; set; } = static (p, d) => new AudioMuxService().EnsurePresetBgm(p, d);

    public static (int W, int H) HtsResolution(HtsUltraAspect a) => a switch
    {
        HtsUltraAspect.Wide16x9 => (1920, 1080),
        HtsUltraAspect.Super32x9 => (3840, 1080),
        _ => (2560, 1080),
    };

    public static string HtsAspectKey(HtsUltraAspect a) => a switch
    {
        HtsUltraAspect.Wide16x9 => "16x9",
        HtsUltraAspect.Super32x9 => "32x9",
        _ => "21x9",
    };

    public static HtsUltraAspect ParseHtsAspect(string? v) => (v ?? "").Trim() switch
    {
        "16x9" or "16:9" => HtsUltraAspect.Wide16x9,
        "32x9" or "32:9" => HtsUltraAspect.Super32x9,
        _ => HtsUltraAspect.Ultra21x9,
    };

    /// <summary>HTS 중앙 스마트 크롭 — 원본 비율과 무관하게 목표 비율 최대 영역을 중앙 절취 → Lanczos 스케일 + 언샵.</summary>
    public static string BuildHtsFilter(HtsUltraAspect a, bool smooth60) => smooth60 ? BuildHtsFilter(a) + "," + MotionInterpolateFilter : BuildHtsFilter(a);

    public static string BuildHtsFilter(HtsUltraAspect a)
    {
        var (w, h) = HtsResolution(a);
        var inv = CultureInfo.InvariantCulture;
        var ws = w.ToString(inv);
        var hs = h.ToString(inv);
        return "crop=min(iw\\,ih*" + ws + "/" + hs + "):min(ih\\,iw*" + hs + "/" + ws + "),scale=" + ws + ":" + hs + ":flags=lanczos,unsharp=5:5:0.8:3:3:0.4";
    }

    /// <summary>3대 규격 프로파일(HTS · 모바일 · 쇼츠) — 인자/해상도 순수 계산(실행 없음).</summary>
    public static IReadOnlyList<MultiExportProfile> BuildMultiTargetProfiles(string sourceVideoPath, MultiExportOptions options, string packageDirectory, string? bgmPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVideoPath);
        ArgumentNullException.ThrowIfNull(options);
        var inv = CultureInfo.InvariantCulture;
        var name = Path.GetFileNameWithoutExtension(sourceVideoPath);
        var (hw, hh) = HtsResolution(options.HtsAspect);
        var webm = options.HtsFormat == StockSuiteExportFormat.WebMVp9;
        var htsVf = ColorGradingService.AppendTo(BuildHtsFilter(options.HtsAspect, options.Smooth60), options.Lut)!;
        var htsOut = Path.Combine(packageDirectory, name + "_hts_" + HtsAspectKey(options.HtsAspect) + (webm ? ".webm" : ".mp4"));
        string[] htsArgs = webm
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", sourceVideoPath, "-map", "0:v:0", "-vf", htsVf, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-an", "-sn", "-dn", htsOut]
            : ["-y", "-hide_banner", "-loglevel", "error", "-i", sourceVideoPath, "-map", "0:v:0", "-vf", htsVf, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", "-movflags", "+faststart", htsOut];

        var mobileOut = Path.Combine(packageDirectory, name + "_mobile_9x16.mp4");
        string[] mobileArgs = ["-y", "-hide_banner", "-loglevel", "error", "-i", sourceVideoPath, "-map", "0:v:0", "-vf", VerticalCropFilter,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", "-movflags", "+faststart", mobileOut];

        var shortsOut = Path.Combine(packageDirectory, name + "_shorts_9x16.mp4");
        var dur = options.DurationSec > 0 ? options.DurationSec : DefaultClipSeconds;
        var shorts = new List<string>(40) { "-y", "-hide_banner", "-loglevel", "error", "-i", sourceVideoPath };
        if (!string.IsNullOrWhiteSpace(bgmPath))
        {
            shorts.Add("-stream_loop"); shorts.Add("-1"); shorts.Add("-i"); shorts.Add(bgmPath);
            shorts.Add("-map"); shorts.Add("0:v:0"); shorts.Add("-map"); shorts.Add("1:a:0");
        }
        else { shorts.Add("-map"); shorts.Add("0:v:0"); }
        shorts.Add("-vf"); shorts.Add(VerticalCropFilter);
        shorts.Add("-c:v"); shorts.Add("libx264"); shorts.Add("-preset"); shorts.Add("medium"); shorts.Add("-crf"); shorts.Add("20"); shorts.Add("-pix_fmt"); shorts.Add("yuv420p");
        if (!string.IsNullOrWhiteSpace(bgmPath))
        {
            shorts.Add("-c:a"); shorts.Add("aac"); shorts.Add("-b:a"); shorts.Add(AudioMuxService.AudioBitrate);
            shorts.Add("-af"); shorts.Add("afade=t=out:st=" + AudioMuxService.FadeStart(dur).ToString("0.###", inv) + ":d=" + AudioMuxService.FadeOutSec.ToString("0.###", inv));
            shorts.Add("-shortest"); shorts.Add("-t"); shorts.Add(dur.ToString("0.###", inv));
        }
        else shorts.Add("-an");
        shorts.Add("-movflags"); shorts.Add("+faststart"); shorts.Add(shortsOut);

        return
        [
            new("hts", "🖥 HTS " + HtsAspectKey(options.HtsAspect).Replace('x', ':'), hw, hh, htsOut, htsArgs),
            new("mobile", "📱 StockSuite 모바일 9:16", 1080, 1920, mobileOut, mobileArgs),
            new("shorts", "🎬 SNS 쇼츠 9:16" + (string.IsNullOrWhiteSpace(bgmPath) ? " (무음)" : " + BGM"), 1080, 1920, shortsOut, shorts.ToArray()),
        ];
    }

    /// <summary>
    /// 📦 원클릭 멀티 타겟 패키지 — 단일 원본 → HTS 울트라와이드 · 모바일 9:16 · 쇼츠 9:16+BGM 동시 생성.
    /// 규격별 실패 격리([부분 구제]) · 산출물 옆 <c>.meta.json</c>(isExported) 기록 → 갤러리 📦 필터 노출. 절대 throw 하지 않음(인자 오류 제외).
    /// </summary>
    public async Task<MultiExportResult> ExportMultiTargetPackageAsync(string sourceVideoPath, MultiExportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var none = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(sourceVideoPath) || !File.Exists(sourceVideoPath))
            return new(none, none, null, "⚠ 원본 영상을 찾을 수 없습니다.");
        string src, pkg;
        try
        {
            src = Path.GetFullPath(sourceVideoPath);
            pkg = string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? Path.Combine(Path.GetDirectoryName(src) ?? ".", Path.GetFileNameWithoutExtension(src) + "_package")
                : Path.GetFullPath(options.OutputDirectory);
            Directory.CreateDirectory(pkg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.MultiPkg", ex);
            return new(none, none, null, "⚠ 패키지 폴더 생성 실패: " + ex.Message);
        }

        var dur = options.DurationSec > 0 ? options.DurationSec : DefaultClipSeconds;
        string? bgm = options.BgmPath?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(bgm) || !File.Exists(bgm))
        {
            bgm = null;
            if (options.BgmPreset is not (SnsBgmPreset.None or SnsBgmPreset.CustomFile))
            {
                try { bgm = BgmResolver(options.BgmPreset, dur); }
                catch (Exception ex) { Program.Log("StockSuite.MultiPkg.Bgm", ex); bgm = null; }   // BGM 실패 → 무음 쇼츠 폴백
            }
        }

        var generated = new List<string>(3);
        var failed = new List<string>(3);
        string? lastError = null;
        var now = UtcNow();
        foreach (var p in BuildMultiTargetProfiles(src, options, pkg, bgm))
        {
            if (ct.IsCancellationRequested) break;
            string? err;
            try { err = await TryFfmpegAsync(p.Args, p.OutputPath, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { TryDelete(p.OutputPath); break; }
            catch (Exception ex) { Program.Log("StockSuite.MultiPkg." + p.Key, ex); err = ex.Message; }
            if (err is null)
            {
                generated.Add(p.OutputPath);
                WriteExportMeta(p, src, now);
            }
            else
            {
                TryDelete(p.OutputPath);
                failed.Add(p.Key);
                lastError = p.Label + ": " + err;
            }
        }

        if (generated.Count > 0) ExportMetadata.MarkExported(src, generated, now);   // 📦 원본 메타 규격 통일
        var n = generated.Count.ToString(CultureInfo.InvariantCulture);
        string msg;
        if (ct.IsCancellationRequested) msg = "취소됨 — " + n + "개 보존";
        else if (generated.Count == 0) msg = "⚠ 멀티 패키지 생성 실패 — " + (lastError ?? "알 수 없는 오류");
        else if (failed.Count > 0) msg = "📦 멀티 패키지 " + n + "/3 생성 (실패: " + string.Join(", ", failed) + ") → " + pkg;
        else msg = "📦 올인원 멀티 패키지 3종 생성 완료 → " + pkg;
        return new(generated, failed, pkg, msg);
    }

    private static void WriteExportMeta(MultiExportProfile p, string sourcePath, DateTimeOffset now)
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var res = p.Width.ToString(inv) + "x" + p.Height.ToString(inv);
            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["title"] = Path.GetFileNameWithoutExtension(sourcePath) + " · " + p.Label,
                ["resolution"] = res,
                ["isExported"] = true,
                ["exportedAt"] = ExportMetadata.FormatTimestamp(now),
                ["exportedTargets"] = new[] { p.OutputPath },
                ["exportProfile"] = p.Key,
                ["sourcePath"] = sourcePath,
                ["createdAt"] = DateTime.Now.ToString("o", inv),
            });
            File.WriteAllText(Path.ChangeExtension(p.OutputPath, null) + ".meta.json", json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { Program.Log("StockSuite.MultiPkg.Meta", ex); }
    }

    /// <summary>
    /// HTS 제로-오디오 인자 — 첫 비디오 스트림만 무손실 copy, 오디오/자막/데이터 스트림 드롭(<c>-an -sn -dn</c>).
    /// HTS 백그라운드 상시 재생 시 오디오 디코더 구동·사운드 장치 점유를 원천 차단. MP4 는 <c>+faststart</c>.
    /// </summary>
    public static string[] BuildZeroAudioArgs(string inputPath, string outputPath)
        => outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", "-dn", "-movflags", "+faststart", outputPath]
            : ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", "-dn", outputPath];

    /// <summary>
    /// HTS 고선명 인자 — <see cref="SharpenFilter"/>(1080p Lanczos + Unsharp) 적용 후 H.264 재인코딩, 오디오/자막/데이터 드롭(<c>-an -sn -dn</c>).
    /// MP4 는 <c>+faststart</c>.
    /// </summary>
    public static string[] BuildSharpenArgs(string inputPath, string outputPath) => BuildSharpenArgs(inputPath, outputPath, SharpenFilter);

    /// <summary>🚀 H.264 재인코딩 인자 — <paramref name="vendor"/> 하드웨어 인코더 바인딩(CPU = libx264 기존 인자). GPU 는 <c>-pix_fmt yuv420p</c> 유지.</summary>
    public static string[] BuildSharpenArgs(string inputPath, string outputPath, string videoFilter, GpuEncoderVendor vendor)
    {
        if (vendor == GpuEncoderVendor.Cpu) return BuildSharpenArgs(inputPath, outputPath, videoFilter);
        var codec = HardwareAccelDetector.VideoCodecArgs(vendor);
        var a = new List<string>(32) { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", videoFilter };
        a.AddRange(codec);
        a.Add("-pix_fmt"); a.Add("yuv420p");
        a.Add("-an"); a.Add("-sn"); a.Add("-dn");
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { a.Add("-movflags"); a.Add("+faststart"); }
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>H.264 재인코딩 인자 — 임의 필터 체인(<see cref="BuildVideoFilter"/> · 샤프닝/60fps 보간) 주입.</summary>
    public static string[] BuildSharpenArgs(string inputPath, string outputPath, string videoFilter)
        => outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", videoFilter, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", "-movflags", "+faststart", outputPath]
            : ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", videoFilter, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", outputPath];

    /// <summary>
    /// 🟢 StockSuite 적용 중 판정 — 설정 폴더의 <c>bg_ambient.mp4/.webm</c> 출처 마커가 원본(경로·크기·수정시각)과 일치하거나,
    /// MP4 대상이 동일 크기 + SHA-256 일치. 결과는 (경로·크기·시각) 키로 캐시.
    /// </summary>
    public bool IsApplied(string? sourcePath)
    {
        var dir = ConfiguredDirectory;
        if (dir is null || string.IsNullOrWhiteSpace(sourcePath)) return false;
        try
        {
            var src = new FileInfo(sourcePath);
            if (!src.Exists) return false;
            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir)) return false;
            var mp4 = Path.Combine(fullDir, DefaultAssetName);
            var webm = Path.Combine(fullDir, WebMAssetName);
            if (MarkerMatches(mp4, src) || MarkerMatches(webm, src)) return true;
            return HashMatches(src, new FileInfo(mp4));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string MarkerPayload(FileInfo src)
        => src.FullName + "|" + src.Length.ToString(CultureInfo.InvariantCulture) + "|" + src.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private static void WriteSourceMarker(string target, string sourcePath)
    {
        try { File.WriteAllText(target + SourceMarkerSuffix, MarkerPayload(new FileInfo(Path.GetFullPath(sourcePath)))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StockSuite.Marker", ex); }
    }

    private static bool MarkerMatches(string target, FileInfo src)
    {
        var marker = target + SourceMarkerSuffix;
        if (!File.Exists(target) || !File.Exists(marker)) return false;
        // 마커가 대상보다 오래되면(외부에서 대상 교체) 무효
        if (File.GetLastWriteTimeUtc(marker) < File.GetLastWriteTimeUtc(target).AddSeconds(-2)) return false;
        return string.Equals(File.ReadAllText(marker).Trim(), MarkerPayload(src), StringComparison.OrdinalIgnoreCase);
    }

    private bool HashMatches(FileInfo src, FileInfo target)
    {
        if (!target.Exists || target.Length != src.Length) return false;
        var key = MarkerPayload(src) + "#" + target.Length.ToString(CultureInfo.InvariantCulture) + "|" + target.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        lock (_hashLock) if (_hashCache.TryGetValue(key, out var cached)) return cached;
        bool eq;
        using (var a = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
        using (var b = new FileStream(target.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
            eq = CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
        lock (_hashLock)
        {
            if (_hashCache.Count > 256) _hashCache.Clear();
            _hashCache[key] = eq;
        }
        return eq;
    }

    // ══════════════════════ 🧾 매니페스트 & ↩️ 원클릭 롤백 ══════════════════════

    public const string ManifestName = ".stocksuite_manifest.json";
    public const string BackupInfix = ".backup";

    /// <summary><c>bg_ambient.mp4</c> → <c>bg_ambient.backup.mp4</c>.</summary>
    public static string BackupPathFor(string target)
        => Path.Combine(Path.GetDirectoryName(target) ?? "", Path.GetFileNameWithoutExtension(target) + BackupInfix + Path.GetExtension(target));

    /// <summary>백업 산출물 여부(갤러리 색인 제외용).</summary>
    public static bool IsBackupAsset(string path)
        => Path.GetFileNameWithoutExtension(path).EndsWith(BackupInfix, StringComparison.OrdinalIgnoreCase);

    /// <summary>파일 SHA-256 소문자 hex(실패 → 빈 문자열).</summary>
    public static string ComputeSha256Hex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Sha256", ex);
            return "";
        }
    }

    /// <summary>매니페스트 JSON(camelCase · UTC ISO-8601).</summary>
    public static string BuildManifestJson(StockSuiteManifest m)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var w = new System.Text.Json.Utf8JsonWriter(buf, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("activeAsset", m.ActiveAsset);
            w.WriteString("sourceRenderId", m.SourceRenderId);
            w.WriteString("sourceSha256", m.SourceSha256);
            w.WriteNumber("sourceBytes", m.SourceBytes);
            w.WriteString("exportedAt", m.ExportedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (m.BackupAsset is { Length: > 0 } ba)
            {
                w.WriteString("backupAsset", ba);
                w.WriteString("backupRenderId", m.BackupRenderId ?? "");
                w.WriteString("backupSha256", m.BackupSha256 ?? "");
                w.WriteNumber("backupBytes", m.BackupBytes);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary><paramref name="dir"/>의 매니페스트 읽기 — 부재/손상/스키마 불일치 → null.</summary>
    public static StockSuiteManifest? TryReadManifest(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var p = Path.Combine(dir, ManifestName);
            if (!File.Exists(p)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(p));
            var r = doc.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var active = Str(r, "activeAsset");
            if (active.Length == 0 || active.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var backup = Str(r, "backupAsset");
            if (backup.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) backup = "";
            DateTimeOffset.TryParse(Str(r, "exportedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at);
            return new StockSuiteManifest(active, Str(r, "sourceRenderId"), Str(r, "sourceSha256").ToLowerInvariant(), Num(r, "sourceBytes"), at,
                backup.Length == 0 ? null : backup, Str(r, "backupRenderId"), Str(r, "backupSha256").ToLowerInvariant(), Num(r, "backupBytes"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            Program.Log("StockSuite.ManifestRead", ex);
            return null;
        }

        static string Str(System.Text.Json.JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        static long Num(System.Text.Json.JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    }

    private static void WriteManifestFile(string dir, StockSuiteManifest m)
    {
        var path = Path.Combine(dir, ManifestName);
        var tmp = path + TempExt;
        try
        {
            File.WriteAllText(tmp, BuildManifestJson(m));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.ManifestWrite", ex);
            TryDelete(tmp);
        }
    }

    /// <summary>내보내기 성공 후 매니페스트 기록 — 직전 매니페스트의 활성 출처는 백업 출처로 승계.</summary>
    private static void WriteManifest(string target, string sourcePath, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir)) return;
        var prev = TryReadManifest(dir);
        var backup = BackupPathFor(target);
        var hasBackup = File.Exists(backup);
        long bytes;
        try { bytes = new FileInfo(sourcePath).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bytes = 0; }
        // 백업 출처 — 직전 활성 출처가 같은 에셋이었고 지금 백업으로 이동했다면 승계, 재내보내기(동일 출처)면 기존 백업 출처 유지.
        var renderId = Path.GetFileNameWithoutExtension(sourcePath);
        var sha = ComputeSha256Hex(sourcePath);
        string? bId = null, bSha = null; long bBytes = 0;
        if (hasBackup && prev is not null)
        {
            var sameAsset = string.Equals(prev.ActiveAsset, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
            if (sameAsset && !string.Equals(prev.SourceSha256, sha, StringComparison.Ordinal)) { bId = prev.SourceRenderId; bSha = prev.SourceSha256; bBytes = prev.SourceBytes; }
            else { bId = prev.BackupRenderId; bSha = prev.BackupSha256; bBytes = prev.BackupBytes; }
        }
        WriteManifestFile(dir, new StockSuiteManifest(Path.GetFileName(target), renderId, sha, bytes, now,
            hasBackup ? Path.GetFileName(backup) : null, bId, bSha, bBytes));
    }

    /// <summary>덮어쓰기 직전 활성 파일 → <c>*.backup.*</c> 복사 보존. 동일 출처 재내보내기는 백업 생략(진짜 이전 배경 유지). 실패 격리.</summary>
    private static void BackupActiveAsset(string target, string sourcePath)
    {
        try
        {
            if (!File.Exists(target)) return;
            if (MarkerMatches(target, new FileInfo(Path.GetFullPath(sourcePath)))) return;
            var backup = BackupPathFor(target);
            var tmp = backup + TempExt;
            File.Copy(target, tmp, overwrite: true);
            File.Move(tmp, backup, overwrite: true);
            var marker = target + SourceMarkerSuffix;
            if (File.Exists(marker)) File.Copy(marker, backup + SourceMarkerSuffix, overwrite: true);
            else TryDelete(backup + SourceMarkerSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Backup", ex);
        }
    }

    /// <summary>↩️ 롤백 가능 여부 — 매니페스트 백업(또는 기본 이름 백업) 실존.</summary>
    public bool CanRollback(string? targetDir = null) => ResolveRollbackPair(targetDir) is not null;

    private (string Dir, string Active, string Backup)? ResolveRollbackPair(string? targetDir)
    {
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        if (dir is null) return null;
        try
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full)) return null;
            var m = TryReadManifest(full);
            if (m?.BackupAsset is { } ba && File.Exists(Path.Combine(full, ba)))
                return (full, Path.Combine(full, m.ActiveAsset), Path.Combine(full, ba));
            foreach (var name in new[] { DefaultAssetName, WebMAssetName })
            {
                var a = Path.Combine(full, name);
                var b = BackupPathFor(a);
                if (File.Exists(b)) return (full, a, b);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Program.Log("StockSuite.RollbackProbe", ex); }
        return null;
    }

    /// <summary>
    /// ↩️ 이전 배경으로 복원 — 활성 ↔ 백업 원자적 맞교환(재실행 시 되돌리기 가능) + 출처 마커·매니페스트 교환 + 핫리로드 신호.
    /// </summary>
    public StockSuiteExportResult RollbackPrevious(string? targetDir = null)
    {
        var pair = ResolveRollbackPair(targetDir);
        if (pair is null) return new(false, null, !IsConfigured && string.IsNullOrWhiteSpace(targetDir), "복원할 이전 배경(백업)이 없습니다.");
        var (dir, active, backup) = pair.Value;
        var swap = active + TempExt + ".swap";
        try
        {
            var hadActive = File.Exists(active);
            if (hadActive) File.Move(active, swap, overwrite: true);
            try { File.Move(backup, active, overwrite: true); }
            catch
            {
                if (hadActive) File.Move(swap, active, overwrite: true);   // 원상 복구
                throw;
            }
            if (hadActive) File.Move(swap, backup, overwrite: true);
            SwapFiles(active + SourceMarkerSuffix, backup + SourceMarkerSuffix);
            // 마커 시각이 대상보다 오래되면 무효 처리되므로 교환 후 갱신.
            foreach (var mk in new[] { active + SourceMarkerSuffix, backup + SourceMarkerSuffix })
                if (File.Exists(mk)) File.SetLastWriteTimeUtc(mk, DateTime.UtcNow);

            var now = UtcNow();
            if (TryReadManifest(dir) is { } m)
                WriteManifestFile(dir, new StockSuiteManifest(Path.GetFileName(active), m.BackupRenderId ?? "", m.BackupSha256 ?? "", m.BackupBytes, now,
                    hadActive ? Path.GetFileName(backup) : null, m.SourceRenderId, m.SourceSha256, m.SourceBytes));
            WriteHotReloadTrigger(active, now);
            lock (_hashLock) _hashCache.Clear();
            return new(true, active, false, "↩️ 이전 배경으로 복원 완료 → " + Path.GetFileName(active));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.Rollback", ex);   // swap 임시본은 유일 사본일 수 있어 삭제하지 않음
            return new(false, null, false, "↩️ 롤백 실패: " + ex.Message);
        }
    }

    private static void SwapFiles(string a, string b)
    {
        var ea = File.Exists(a); var eb = File.Exists(b);
        if (!ea && !eb) return;
        var t = a + TempExt;
        if (ea) File.Move(a, t, overwrite: true);
        if (eb) File.Move(b, a, overwrite: true);
        if (ea) File.Move(t, b, overwrite: true);
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await Engines.ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ══════════════════════ 🚀 StockSuite 원클릭 런처 ══════════════════════

    /// <summary>실행 중 프로세스 수 조회(테스트 주입 · 기본 = 프로세스 이름 일치).</summary>
    public Func<string, int> RunningProbe { get; set; } = CountRunning;
    /// <summary>프로세스 시작기(테스트 주입 · 기본 = <see cref="IProcessRunner.LaunchExternal"/> 단일 관문).</summary>
    public Func<ProcessStartInfo, bool> Starter { get; set; } = DefaultStart;
    /// <summary>기존 프로세스 종료기(재실행 · 테스트 주입). 반환 = 종료 성공 여부.</summary>
    public Func<string, bool> Terminator { get; set; } = TerminateAll;

    public string? ConfiguredExecutable
    {
        get
        {
            try { var p = _exePath(); return string.IsNullOrWhiteSpace(p) ? null : p.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Exe", ex); return null; }
        }
    }

    public bool CanLaunch => ValidateExecutablePath(ConfiguredExecutable) == StockSuiteExeStatus.Ok;

    /// <summary>실행 파일 경로 검증 — 비어 있음 / 부재 / .exe 아님 / OK.</summary>
    public static StockSuiteExeStatus ValidateExecutablePath(string? path)
    {
        var p = (path ?? "").Trim().Trim('"');
        if (p.Length == 0) return StockSuiteExeStatus.NotConfigured;
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return StockSuiteExeStatus.NotFound;
        if (!Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return StockSuiteExeStatus.NotExecutable;
        return File.Exists(p) ? StockSuiteExeStatus.Ok : StockSuiteExeStatus.NotFound;
    }

    public static string DescribeExeStatus(StockSuiteExeStatus s) => s switch
    {
        StockSuiteExeStatus.Ok => "✅ 실행 파일을 확인했습니다.",
        StockSuiteExeStatus.NotExecutable => "❌ .exe 실행 파일이 아닙니다.",
        StockSuiteExeStatus.NotFound => "❌ 실행 파일이 존재하지 않습니다.",
        _ => "ℹ 비어 있음 — 환경설정에서 StockSuite.exe 경로를 지정하세요.",
    };

    /// <summary>
    /// 🚀 StockSuite 실행. 이미 실행 중이면 <paramref name="restartIfRunning"/>=false → AlreadyRunning(경고),
    /// true → 기존 프로세스 종료 후 재실행. 절대 throw 하지 않음.
    /// </summary>
    public StockSuiteLaunchResult Launch(bool restartIfRunning = false)
    {
        var exe = ConfiguredExecutable;
        var st = ValidateExecutablePath(exe);
        if (st != StockSuiteExeStatus.Ok) return new(false, false, DescribeExeStatus(st));
        var full = Path.GetFullPath(exe!);
        var name = Path.GetFileNameWithoutExtension(full);
        try
        {
            if (RunningProbe(name) > 0)
            {
                if (!restartIfRunning) return new(false, true, "⚠ StockSuite 가 이미 실행 중입니다 — 재실행하면 변경된 에셋이 즉시 반영됩니다.");
                if (!Terminator(name)) return new(false, true, "⚠ 실행 중인 StockSuite 를 종료하지 못했습니다 — 수동으로 닫은 뒤 다시 시도하세요.");
            }
            var psi = new ProcessStartInfo(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full) ?? "" };
            return Starter(psi)
                ? new(true, false, restartIfRunning ? "🔄 StockSuite 재실행 완료" : "🚀 StockSuite 실행 완료")
                : new(false, false, "⚠ StockSuite 실행에 실패했습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.Log("StockSuite.Launch", ex);
            return new(false, false, "⚠ StockSuite 실행 실패: " + ex.Message);
        }
    }

    private static int CountRunning(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    private static bool DefaultStart(ProcessStartInfo psi)
    {
        return PromptFlowStudio.Services.Engines.SystemProcessRunner.Gateway.LaunchExternal(psi.FileName, psi.Arguments, psi.UseShellExecute);   // 🚀 작업 디렉터리 = exe 폴더(자동)
    }

    private static bool TerminateAll(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        var ok = true;
        foreach (var p in ps)
        {
            try
            {
                if (!p.HasExited && (!p.CloseMainWindow() || !p.WaitForExit(3000))) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                ok &= p.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Program.Log("StockSuite.Terminate", ex);
                ok = false;
            }
            finally { p.Dispose(); }
        }
        return ok;
    }
}
