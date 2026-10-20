using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Platform;

// v5.3: IAutoUpdateService 계약은 PromptFlowStudio.Shared/Platform/PlatformInterfaces.cs 로 이관(모바일 디커플링). 데스크톱(GitHub Releases) 구현만 유지.

/// <summary>
/// [PromptFlowStudio] 자체 자동 업데이트 — 외부 NuGet 0(BCL HttpClient/System.Text.Json/Process).
///   ▸ 확인: GitHub API(releases/latest)에서 tag_name·.exe 자산 URL 파싱 → 현재 어셈블리 버전과 비교(revision 무시 정규화).
///   ▸ 다운로드: 기존 스트리밍 패턴(ResponseHeadersRead + ArrayPool 펌프 + IProgress&lt;double&gt;)로 %Temp% 에 저장.
///   ▸ 설치: Setup.exe /SILENT 실행(UseShellExecute=true → installer admin 매니페스트로 UAC 승격) 후 Application.Exit.
///     인스톨러는 CloseApplications/RestartApplications 로 파일 잠금을 정리하고 설치 후 앱을 재시작한다.
///   ⚠ GitHub API 는 User-Agent 헤더가 필수(없으면 403).
/// </summary>
public sealed class AutoUpdateService : IAutoUpdateService
{
    // 큰 자산(150MB+) 다운로드 대비 무한 타임아웃 — 실제 제한은 CancellationToken(및 API 는 링크드 CTS 20초).
    private static readonly HttpClient Http = BuildClient();

    private readonly IConfiguration _cfg;
    private readonly Version _current;
    private UpdateInfo? _pending;
    private bool _checkedOnce;

    public AutoUpdateService(IConfiguration cfg)
    {
        _cfg = cfg;
        _current = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    }

    public event Action? OnChange;

    public bool Enabled => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repo);
    public bool UpdateAvailable { get; private set; }
    public string CurrentVersion => "v" + _current.ToString(3);
    public string? LatestVersion { get; private set; }
    public string? LatestReleaseNotes { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public int DownloadPercent { get; private set; }
    public string? Status { get; private set; }

    private string Owner => _cfg["Update:Owner"] ?? "";
    private string Repo => _cfg["Update:Repo"] ?? "";

    // ══ 최신 릴리스 확인 ═══════════════════════════════════════════════════
    public async Task CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!Enabled || IsChecking || IsDownloading) return;
        if (_checkedOnce && !force) return;
        _checkedOnce = true;

        IsChecking = true; Status = "업데이트 확인 중…"; Raise();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));   // API 는 짧게 제한

            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var body = await Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = ParseTag(tag);
            if (latest is null) { Status = "릴리스 버전을 해석할 수 없습니다."; return; }
            LatestVersion = "v" + latest.ToString(3);

            // 릴리스 설명(body · 마크다운). ⚠ 원격 데이터 → 모달에서 '텍스트로만' 렌더(HTML 주입 금지).
            LatestReleaseNotes = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString() : null;

            // .exe 자산 URL(이름에 setup 포함이면 우선).
            string? dl = null, asset = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var u = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                    if (u is null) continue;
                    dl = u; asset = name;
                    if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) break;
                }

            if (latest > _current && dl is not null && asset is not null)
            {
                _pending = new UpdateInfo(latest, tag!, dl, asset);
                UpdateAvailable = true;
                Status = $"새 버전 {LatestVersion} 사용 가능";
            }
            else
            {
                UpdateAvailable = false;
                Status = "최신 버전을 사용 중입니다.";
            }
        }
        catch (OperationCanceledException) { Status = "업데이트 확인 시간 초과/취소."; }
        catch (Exception ex) { Status = "업데이트 확인 실패: " + ex.Message; Program.Log("AutoUpdate.Check", ex); }
        finally { IsChecking = false; Raise(); }
    }

    // ══ 다운로드 + 무음 설치 ═══════════════════════════════════════════════
    public async Task DownloadAndInstallAsync(CancellationToken ct = default)
    {
        if (_pending is null || IsDownloading) return;

        IsDownloading = true; DownloadPercent = 0; Status = "새 버전 다운로드 중…"; Raise();
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "PromptFlowStudio-Update");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, _pending.AssetName);
            var tmp = dest + ".part";

            using (var resp = await Http.GetAsync(_pending.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await PumpAsync(net, file, total, new CallbackProgress(p =>
                {
                    DownloadPercent = (int)p; Status = $"다운로드 중… {DownloadPercent}%"; Raise();
                }), ct).ConfigureAwait(false);
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);

            DownloadPercent = 100; Status = "설치 프로그램 실행 — 앱을 종료합니다…"; Raise();
            InstallAndRestart(dest);
        }
        catch (OperationCanceledException) { Status = "다운로드가 취소되었습니다."; }
        catch (Exception ex) { Status = "업데이트 실패: " + ex.Message; Program.Log("AutoUpdate.Download", ex); }
        finally { IsDownloading = false; Raise(); }
    }

    /// <summary>Setup.exe 무음 실행 + 현재 앱 Graceful 종료(인스톨러가 설치 후 재시작).</summary>
    private static void InstallAndRestart(string setupPath)
    {
        if (!File.Exists(setupPath)) throw new FileNotFoundException("설치 파일을 찾을 수 없습니다.", setupPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/SILENT",     // Inno 무음(진행바만) — 파일 잠금은 CloseApplications 로 정리
            UseShellExecute = true,    // ⚠ true 여야 installer admin 매니페스트로 UAC 승격 프롬프트가 뜬다
        });

        // 파일 잠금 해제를 위해 즉시 종료(설치 후 인스톨러가 앱을 재시작).
        try { Application.Exit(); } catch { Environment.Exit(0); }
    }

    // ── 스트리밍 펌프(ArrayPool · 진행률) — LocalMediaService 와 동일 패턴 ──
    private static async Task PumpAsync(Stream src, Stream dst, long? total, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long done = 0; int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (progress is not null && total is > 0)
                {
                    var pct = (int)(done * 100L / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress.Report(pct); }
                }
            }
            if (progress is not null && total is not > 0) progress.Report(100);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static HttpClient BuildClient()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PromptFlowStudio-Updater/1.0");   // ⚠ GitHub API 필수
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>"v1.2.3(-beta)" → Version(정규화). 실패 시 null.</summary>
    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var dash = s.IndexOf('-'); if (dash >= 0) s = s[..dash];   // -beta 등 접미 제거
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>revision 차이로 인한 오탐 방지 — major.minor.build 3요소로 정규화.</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private void Raise() => OnChange?.Invoke();

    private sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string AssetName);

    private sealed class CallbackProgress(Action<double> cb) : IProgress<double>
    {
        public void Report(double value) => cb(value);
    }
}
