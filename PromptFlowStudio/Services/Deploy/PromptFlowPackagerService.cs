using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // SmtpConfigService.ExportDeploySeed(배포 씨앗)

namespace PromptFlowStudio.Services.Deploy;

/// <summary>
/// [PromptFlow Studio] 보안 배포 · 올인원 인스톨러 &amp; 단일 EXE 패키징 엔진(싱글턴).
///   관리자 설정 › 보안·배포 탭의 버튼 클릭 시 스레드풀에서 3단계를 <see cref="SemaphoreSlim"/> 로 직렬 집행한다:
///     [1/3] dotnet 런타임 내장 <b>단일 EXE</b> 퍼블리시(self-contained · PublishSingleFile) — 핵심 산출물
///     [2/3] 페이로드 번들링 — PromptFlowStudio.exe + appsettings.json + app.ico + <b>wwwroot 통째</b> + runtimes
///           (⚠ Program.cs 가 appsettings 를 optional 로 읽으므로 <b>반드시 동봉</b>해야 License 검증/SMTP 폴백이 동작한다)
///     [3/3] Inno Setup(ISCC.exe) 동적 스크립트 생성 + 컴파일 → 바탕화면 단일 인스톨러(Setup_PromptFlowStudio_*.exe · WebView2 런타임 자동 보장)
///   모니터링 오토메이션(SoundStack) PackagerEngineService → SiteMatic 계보의 <b>순수 .NET</b> 이식본. 외부 NuGet 0 · 완전 오프라인.
///   ISCC 미설치/컴파일 실패 시 크래시 없이 [폴백] 단일 EXE 폴더 드롭(바탕화면)으로 안전 완료한다(전 구간 예외 가드).
///   실시간 표준출력/에러는 <see cref="OnLogReceived"/> 로 스트리밍(임의 스레드 → 구독 UI 가 InvokeAsync 마샬링).
/// </summary>
public sealed class PromptFlowPackagerService
{
    private const string ProjectFileName = "PromptFlowStudio.csproj";
    private const string AppExeName = "PromptFlowStudio.exe";
    private const string PublishRid = "win-x64";
    private const string Tfm = "net8.0-windows";

    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>실시간 빌드 로그 스트림(임의 스레드에서 호출됨 — 구독 UI 가 InvokeAsync 로 마샬링).</summary>
    public event Action<string>? OnLogReceived;

    /// <summary>마지막 산출물 경로(인스톨러 EXE 또는 단일 EXE 폴더). 화면 표시용.</summary>
    public string? LastOutputPath { get; private set; }

    /// <summary>패키징 진행 중 여부(중복 클릭 방어의 UI 표시용).</summary>
    public bool IsBuilding => _buildGate.CurrentCount == 0;

    /// <summary>
    /// 보안 배포 패키징 파이프라인 집행. 성공(인스톨러 또는 단일 EXE 산출) 시 true.
    ///   패키징은 <b>소스 + .NET SDK 가 있는 발급자(개발) PC</b> 전용 기능이다 — 배포 수령 PC 에는 소스가 없어 스킵된다.
    /// </summary>
    public async Task<bool> ExecuteSecurePublishAsync(CancellationToken ct = default)
    {
        // [중복 클릭 방어] 이미 빌드 중이면 즉시 스킵(비차단 획득).
        if (!await _buildGate.WaitAsync(0, ct))
        {
            Log("⚠️ 이미 패키징이 진행 중입니다 — 중복 실행을 건너뜁니다.");
            return false;
        }
        try
        {
            Log("🚀 [PromptFlow Studio] 올인원 인스톨러 & 단일 EXE 보안 패키징 파이프라인 가동…");

            // ── [환경 가드] 소스(csproj) 위치 해석 — 없으면 발급자 PC 가 아니므로 안전 종료.
            var csproj = ResolveProjectFile();
            if (csproj is null)
            {
                Log($"   [WARN] {ProjectFileName} 를 찾지 못했습니다. 패키징은 소스가 있는 발급자(개발) PC 에서만 가능합니다.");
                return false;
            }
            var projDir = Path.GetDirectoryName(csproj)!;
            Log($"   › 프로젝트: {csproj}");

            // ── [1/3] 런타임 내장 단일 EXE 퍼블리시 — 핵심 산출물(실패 시에만 파이프라인 실패).
            Log("📦 [1/3] 런타임 내장 단일 EXE 퍼블리시(self-contained · PublishSingleFile)…");
            var args = $"publish \"{csproj}\" -c Release -r {PublishRid} "
                     + "/p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained true";
            if (!await RunCommandAsync("dotnet", args, projDir, ct))
            {
                Log("❌ 단일 EXE 퍼블리시 실패 — .NET SDK 설치 및 소스 상태를 확인하세요.");
                return false;
            }

            var publishDir = Path.Combine(projDir, "bin", "Release", Tfm, PublishRid, "publish");
            var publishedExe = Path.Combine(publishDir, AppExeName);
            if (!File.Exists(publishedExe))
            {
                Log($"❌ 퍼블리시 EXE 를 찾지 못했습니다: {publishedExe}");
                return false;
            }
            var exeMb = new FileInfo(publishedExe).Length / (1024.0 * 1024.0);
            Log($"   ↳ 단일 EXE 산출 완료: {AppExeName} ({exeMb:F1} MB)");

            // [안전 폴백] ISCC 미설치/번들·컴파일 실패 시 단일 EXE 폴더 드롭으로 안전 완료하는 로컬 함수.
            bool SingleExeFallback()
            {
                var drop = DropSingleExeToDesktop(publishDir);
                if (drop is not null)
                {
                    LastOutputPath = drop;
                    Log($"✨ [SUCCESS · 폴백] 단일 EXE 배포본 산출 완료 → {drop}");
                    Log("   수령자에게 이 폴더를 전달하면 됩니다(PromptFlowStudio.exe 실행 · 설치 불필요 · 완전 오프라인).");
                    return true;
                }
                Log("❌ 인스톨러·단일 EXE 산출이 모두 실패했습니다 — publish 폴더를 확인하세요.");
                return false;
            }

            // ── [환경 가드] Inno Setup 컴파일러(ISCC.exe) 사전 탐지 — 없으면 무거운 번들링 없이 즉시 안전 폴백.
            var iscc = FindIsccPath();
            if (iscc is null)
            {
                Log("   [WARN] Inno Setup(ISCC.exe) 미설치 → 단일 EXE 폴더 드롭으로 자동 폴백합니다.");
                Log("          (인스톨러가 필요하면 https://jrsoftware.org 의 Inno Setup 6 설치 후 다시 실행하세요.)");
                return SingleExeFallback();
            }
            Log($"   › Inno Setup 컴파일러 확인: {iscc}");

            // ── [2/3] 페이로드 번들링 — PromptFlowStudio.exe + appsettings.json(+아이콘+wwwroot).
            Log("🎁 [2/3] 페이로드 번들링(PromptFlowStudio.exe + appsettings.json + 아이콘 + wwwroot 정적자산)…");
            var payload = BundlePayload(publishDir, projDir);
            if (payload is null)
            {
                Log("   ⚠️ 페이로드 번들링 미완료 → 단일 EXE 폴더 드롭으로 폴백합니다.");
                return SingleExeFallback();
            }

            // ── [3/3] Inno Setup 컴파일 — 동적 .iss 생성 후 ISCC 호출 → 바탕화면 단일 인스톨러 산출.
            Log("🔮 [3/3] Inno Setup 인스톨러 컴파일(setup_script.iss 동적 생성 → ISCC)…");
            var installer = await CompileInnoSetupAsync(iscc, payload, publishedExe, projDir, ct);
            if (installer is null)
            {
                Log("   ⚠️ 인스톨러 컴파일 실패 → 단일 EXE 폴더 드롭으로 폴백합니다.");
                return SingleExeFallback();
            }

            var mb = new FileInfo(installer).Length / (1024.0 * 1024.0);
            LastOutputPath = installer;
            Log($"✨ [SUCCESS] 올인원 인스톨러 산출 완료 → {installer} ({mb:F1} MB)");
            Log("   수령자는 이 인스톨러 실행 한 번으로 설치(WebView2 런타임 자동 보장)되고, 완료 후 자동 기동됩니다.");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("⏹ 패키징이 취소되었습니다.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"❌ 패키징 예외: {ex.Message}");
            return false;
        }
        finally { _buildGate.Release(); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  소스(csproj) 위치 해석 — 실행 파일 위치에서 상위로 거슬러 올라가며 PromptFlowStudio.csproj 탐색.
    //   개발 실행(bin\Debug\net8.0-windows\)에서는 3~4단계 위가 프로젝트 루트다.
    // ─────────────────────────────────────────────────────────────────────────
    private static string? ResolveProjectFile()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ProjectFileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* 경로 접근 실패 → null */ }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  [2/3] 페이로드 번들링 — 인스톨러가 {app} 에 설치할 파일 구성.
    //   payload\ PromptFlowStudio.exe · appsettings.json · app.ico · <wwwroot 통째★> · runtimes · 잔여 DLL
    //   ⚠ wwwroot 를 빼면 설치본이 'There is no content at' 로 죽는다(핵심).
    // ─────────────────────────────────────────────────────────────────────────
    private string? BundlePayload(string publishDir, string projDir)
    {
        try
        {
            var payload = Path.Combine(Path.GetTempPath(), "promptflow_installer_payload");
            if (Directory.Exists(payload)) Directory.Delete(payload, recursive: true);
            Directory.CreateDirectory(payload);

            // (a) 메인 단일 EXE
            File.Copy(Path.Combine(publishDir, AppExeName), Path.Combine(payload, AppExeName), overwrite: true);
            Log("   › [바이너리] PromptFlowStudio.exe 편입");

            // (b) appsettings.json — ⚠ 필수 동봉(License · Smtp:*). publish 우선, 없으면 소스.
            var appsettings = new[]
            {
                Path.Combine(publishDir, "appsettings.json"),
                Path.Combine(projDir, "appsettings.json"),
            }.FirstOrDefault(File.Exists);
            if (appsettings is not null)
            {
                File.Copy(appsettings, Path.Combine(payload, "appsettings.json"), overwrite: true);
                Log("   › [설정] appsettings.json 편입(라이선스 공개키 · SMTP 폴백)");
            }
            else
                Log("   ⚠️ appsettings.json 미발견 — 라이선스 검증이 배포본에서 동작하지 않을 수 있습니다.");

            // (b2) 배포 씨앗 — 환경설정 UI 로 저장한 SMTP 설정은 머신·사용자 바운드 암호화라 그대로는
            //   타 PC 에서 못 연다. 이를 portable 씨앗(deploy_smtp.dat)으로 재암호화해 exe 옆에 동봉하면,
            //   운영자 PC <최초 실행 시> 자동으로 이 PC 의 머신바운드 저장본으로 재봉인되어 메일 발송이 된다.
            //   ※ UI 저장본이 없으면 appsettings.json 의 Smtp:* 가 이미 동봉되므로 씨앗을 생략한다.
            if (SmtpConfigService.ExportDeploySeed(payload))
                Log("   › [메일] 배포 씨앗(deploy_smtp.dat) 편입 — 운영자 PC 첫 실행 시 메일설정 자동 provisioning");
            else
                Log("   › [메일] UI 저장 SMTP 설정 없음 → appsettings.json Smtp:* 폴백 사용(씨앗 생략)");

            // (c) 앱 아이콘(바로가기/인스톨러/제거목록용) — ★반드시 편입해야 SetupIconFile/UninstallDisplayIcon 이 동작.
            var ico = Path.Combine(projDir, "app.ico");
            if (File.Exists(ico)) { File.Copy(ico, Path.Combine(payload, "app.ico"), overwrite: true); Log("   › [자원] app.ico 편입"); }

            // (d) 방어적 — self-contained 단일파일이 추출하지 못한 잔여 네이티브 DLL 이 있으면 함께 편입(보통 없음).
            foreach (var dll in Directory.EnumerateFiles(publishDir, "*.dll"))
                File.Copy(dll, Path.Combine(payload, Path.GetFileName(dll)), overwrite: true);

            // (e) ★★ wwwroot — <반드시> 통째로 편입한다.
            //   ⚠⚠ PublishSingleFile=true 는 관리 어셈블리만 exe 에 묶는다. BlazorWebView 의 정적 자산
            //     (wwwroot/index.html · css · js · img)은 <단일 EXE 에 들어가지 않고> publish 폴더에 그대로
            //     남는다. 이걸 빼면 설치본이 "There is no content at ..." 로 죽는다 — 앱은 뜨지만 흰 화면.
            //   ⚠ MainForm.cs 의 HostPage="wwwroot/index.html" 은 <exe 옆의 물리 wwwroot>를 찾는다.
            var srcWww = Path.Combine(publishDir, "wwwroot");
            if (Directory.Exists(srcWww))
            {
                var dstWww = Path.Combine(payload, "wwwroot");
                CopyDirectoryRecursive(srcWww, dstWww);
                var htmlOk = File.Exists(Path.Combine(dstWww, "index.html"));
                Log(htmlOk
                    ? "   › [정적자산] wwwroot 통째 편입(index.html · css · js · img) ✓"
                    : "   ⚠️ wwwroot 는 편입했으나 index.html 이 없습니다 — 설치본이 흰 화면이 됩니다.");
            }
            else
            {
                // ⚠ 여기 오면 절대 배포하면 안 된다. publish 가 wwwroot 를 안 만든 것이다.
                Log("   ❌ publish 폴더에 wwwroot 가 없습니다 — 설치본이 'There is no content at' 로 죽습니다. 패키징을 중단합니다.");
                Directory.Delete(payload, recursive: true);
                return null;
            }

            // (f) 방어적 — 네이티브 런타임(WebView2Loader 등)이 loose 로 남아 있으면 폴더째 편입.
            //   단일파일 self-extract 가 보통 처리하지만, 안 되는 환경을 위한 안전망.
            var srcRuntimes = Path.Combine(publishDir, "runtimes");
            if (Directory.Exists(srcRuntimes))
            {
                CopyDirectoryRecursive(srcRuntimes, Path.Combine(payload, "runtimes"));
                Log("   › [네이티브] runtimes 폴더 편입(WebView2Loader 등)");
            }

            // (g) ffmpeg — 있으면 함께 편입(영상 병합/썸네일). 없어도 앱은 Graceful 동작.
            var ff = Path.Combine(projDir, "ffmpeg.exe");
            if (File.Exists(ff)) { File.Copy(ff, Path.Combine(payload, "ffmpeg.exe"), overwrite: true); Log("   › [미디어] ffmpeg.exe 편입(영상 병합/썸네일)"); }

            // (h) ★ Tools\ — 번들 CLI 엔진(Real-ESRGAN/Real-CUGAN/Kokoro/ffmpeg 등) 편입.
            //   런타임에 CliPathResolver 가 {app}\Tools\{engine}\ 를 자동 탐색 → 사용자가 경로 지정 없이 즉시 작동(Zero-Config).
            //   ⚠⚠ 반드시 <publish 출력의 Tools\> 만 사용한다(소스 루트 Tools\ 아님). csproj CopyToolsToPublish 타깃이
            //      PromptFlowKeyGen(발급자 RSA 개인키 내장)·소스·bin/obj 를 제외하고 <순수 엔진 바이너리>만 publish\Tools 에 넣어 둔다.
            //      소스 Tools\ 를 직접 복사하면 개인 서명키가 고객 배포본으로 유출되므로 절대 폴백하지 않는다.
            var srcTools = Path.Combine(publishDir, "Tools");
            if (Directory.Exists(srcTools))
            {
                CopyDirectoryRecursive(srcTools, Path.Combine(payload, "Tools"));
                Log("   › [엔진] Tools\\ 번들 CLI 엔진 편입(publish 출력 · 개인키/소스 제외) — Zero-Config 자동 탐색");
            }
            else
                Log("   › [엔진] Tools\\ 번들 없음 → 로컬 엔진 기능은 배포본에서 Graceful 비활성화(오류 아님)");

            Log($"   ↳ 페이로드 준비 완료: {payload}");
            return payload;
        }
        catch (Exception ex)
        {
            Log($"   [ERR] 페이로드 번들링 예외: {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  디렉터리 재귀 복사 — payload 에 wwwroot / runtimes 를 통째로 옮길 때 쓴다.
    //   ⚠ 하위 폴더·파일을 <구조 그대로> 복사한다. flat 복사하면 wwwroot/img 가 루트로 흩어져 로고가 깨진다.
    // ─────────────────────────────────────────────────────────────────────────
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  [3/3] Inno Setup 컴파일
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<string?> CompileInnoSetupAsync(string isccPath, string payloadDir, string publishedExe, string projDir, CancellationToken ct)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var version = ResolveVersion(publishedExe);
            var outputBase = $"Setup_PromptFlowStudio_v{version}_{stamp}";
            var icon = Path.Combine(payloadDir, "app.ico");
            var iconArg = File.Exists(icon) ? icon : null;

            var iss = GenerateInnoSetupScript(payloadDir, desktop, outputBase, version, iconArg);
            var issPath = Path.Combine(Path.GetTempPath(), "promptflow_setup_script.iss");
            // Inno Setup 6(유니코드)은 BOM 있는 UTF-8 스크립트를 정확히 해석 → 한글 문구 보존.
            await File.WriteAllTextAsync(issPath, iss, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
            Log($"   › setup_script.iss 동적 생성({issPath})");

            if (!await RunCommandAsync(isccPath, $"\"{issPath}\"", projDir, ct))
            {
                Log("   [ERR] ISCC 컴파일이 0이 아닌 코드로 종료되었습니다.");
                return null;
            }

            var installer = Path.Combine(desktop, outputBase + ".exe");
            if (File.Exists(installer)) return installer;

            Log("   [ERR] 컴파일은 완료됐으나 산출 인스톨러 파일을 찾지 못했습니다.");
            return null;
        }
        catch (Exception ex)
        {
            Log($"   [ERR] Inno Setup 컴파일 예외: {ex.Message}");
            return null;
        }
    }

    // 게시 EXE 의 파일 버전(예: "1.1.0") — 인스톨러 AppVersion 에 사용. 실패 시 "1.0.0".
    private static string ResolveVersion(string exePath)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            if (!string.IsNullOrWhiteSpace(v))
            {
                var parts = v.Split('.');
                return parts.Length >= 3 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : v;
            }
        }
        catch { /* 무시 */ }
        return "1.0.0";
    }

    // 동적 ISS 제너레이터 — 패키징 시점에 인메모리로 Inno Setup 스크립트를 작성한다.
    //  · {autopf} 설치(관리자) + CloseApplications(실행 중 앱 안전 자동 종료 · Restart Manager)
    //  · [InstallDelete] 구버전 잔존 런타임/자산 선삭제(클린 업그레이드 · 구 DLL 잔류/부분갱신 차단)
    //  · [Tasks]/[Icons] 바탕화면·시작메뉴 바로가기 + [Run] 설치 후 자동 기동 체크박스
    //  · [Code] WebView2 런타임 자동 보장(미설치 PC 에서만 MS 공식 부트스트래퍼 무인 설치)
    private static string GenerateInnoSetupScript(string payloadDir, string outputDir, string outputBaseFilename,
                                                  string version, string? setupIconFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ===========================================================================");
        sb.AppendLine(";  [자동 생성] PromptFlow Studio — 올인원 턴키 인스톨러 스크립트");
        sb.AppendLine(";  PromptFlowPackagerService.GenerateInnoSetupScript() 가 패키징 시점에 동적 작성.");
        sb.AppendLine("; ===========================================================================");
        sb.AppendLine();
        sb.AppendLine("[Setup]");
        sb.AppendLine("AppId=PromptFlowStudio-ANGTEC");   // 업그레이드 식별자 — 고정(변경 시 나란히 설치되어 갱신 실패)
        sb.AppendLine("AppName=PromptFlow Studio");
        sb.AppendLine($"AppVersion={version}");
        sb.AppendLine("AppPublisher=ANG Technology");
        sb.AppendLine("DefaultDirName={autopf}\\PromptFlow Studio");
        sb.AppendLine("DefaultGroupName=PromptFlow Studio");
        sb.AppendLine("DisableProgramGroupPage=yes");
        sb.AppendLine($"OutputDir={outputDir}");
        sb.AppendLine($"OutputBaseFilename={outputBaseFilename}");
        sb.AppendLine("Compression=lzma2");
        sb.AppendLine("SolidCompression=yes");
        sb.AppendLine("ArchitecturesInstallIn64BitMode=x64compatible");   // 비-deprecated · x64 + ARM64(x64 에뮬) 허용
        sb.AppendLine("PrivilegesRequired=admin");
        sb.AppendLine("WizardStyle=modern");
        // [설치/업데이트 안전] 실행 중이면 Restart Manager 로 안전 종료.
        sb.AppendLine("CloseApplications=yes");
        sb.AppendLine("RestartApplications=no");
        if (setupIconFile is not null)
            sb.AppendLine($"SetupIconFile={setupIconFile}");   // 설치 마법사(Setup.exe) 아이콘
        // ★ "프로그램 및 기능"(제어판 제거/변경) 목록의 아이콘 — 설치된 exe 의 임베디드 아이콘(app.ico)을 쓴다.
        //   이게 없으면 제어판 프로그램 목록에 기본(무명) 아이콘으로 뜬다.
        sb.AppendLine("UninstallDisplayIcon={app}\\PromptFlowStudio.exe");
        sb.AppendLine($"UninstallDisplayName=PromptFlow Studio {version}");
        sb.AppendLine();
        // [클린 업그레이드] 이전 버전의 런타임/정적자산을 먼저 정리 → 구 DLL 잔류로 인한 오작동·부분갱신 차단.
        //   Inno 자체 언인스톨러(unins*)와 사용자 데이터(%LocalAppData%)는 건드리지 않는다.
        sb.AppendLine("[InstallDelete]");
        sb.AppendLine("Type: filesandordirs; Name: \"{app}\\runtimes\"");
        sb.AppendLine("Type: filesandordirs; Name: \"{app}\\wwwroot\"");
        sb.AppendLine("Type: files; Name: \"{app}\\*.dll\"");
        sb.AppendLine("Type: files; Name: \"{app}\\*.pdb\"");
        sb.AppendLine();
        sb.AppendLine("[Tasks]");
        sb.AppendLine("Name: \"desktopicon\"; Description: \"바탕화면에 바로가기 아이콘 생성\"; GroupDescription: \"추가 아이콘:\"");
        sb.AppendLine();
        sb.AppendLine("[Files]");
        // 페이로드 전량 → {app}.
        sb.AppendLine($"Source: \"{payloadDir}\\*\"; DestDir: \"{{app}}\"; Flags: ignoreversion recursesubdirs createallsubdirs");
        sb.AppendLine();
        sb.AppendLine("[Icons]");
        sb.AppendLine("Name: \"{group}\\PromptFlow Studio\"; Filename: \"{app}\\PromptFlowStudio.exe\"; IconFilename: \"{app}\\PromptFlowStudio.exe\"");
        sb.AppendLine("Name: \"{group}\\PromptFlow Studio 제거\"; Filename: \"{uninstallexe}\"");
        sb.AppendLine("Name: \"{autodesktop}\\PromptFlow Studio\"; Filename: \"{app}\\PromptFlowStudio.exe\"; IconFilename: \"{app}\\PromptFlowStudio.exe\"; Tasks: desktopicon");
        sb.AppendLine();
        sb.AppendLine("[Run]");
        sb.AppendLine("Filename: \"{app}\\PromptFlowStudio.exe\"; Description: \"설치 완료 후 지금 실행\"; Flags: nowait postinstall skipifsilent");
        // WebView2 런타임 자동 보장 — 런타임이 없는 PC(LTSC/Server 등)에서만 MS 공식 부트스트래퍼를 내려받아 무인 설치.
        //   이미 설치된 대부분의 Win10/Win11 은 레지스트리 검사에서 스킵. 다운로드/설치 실패는 '비차단'.
        sb.Append(
"""

[Code]
var
  WV2DownloadPage: TDownloadWizardPage;

function IsWebView2RuntimeInstalled: Boolean;
var
  V: String;
begin
  Result := False;
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Result := (V <> '') and (V <> '0.0.0.0');
  if (not Result) and RegQueryStringValue(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Result := (V <> '') and (V <> '0.0.0.0');
end;

function OnWV2DownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  WV2DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnWV2DownloadProgress);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsWebView2RuntimeInstalled then
    Exit;
  WV2DownloadPage.Clear;
  WV2DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
  WV2DownloadPage.Show;
  try
    try
      WV2DownloadPage.Download;
      Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
    except
      { 비차단: 다운로드/설치 실패해도 앱 설치는 계속 }
    end;
  finally
    WV2DownloadPage.Hide;
  end;
end;
""");
        return sb.ToString();
    }

    // Inno Setup 컴파일러(ISCC.exe) 위치 해석 — 1차 시스템 PATH → 2차 표준 절대경로(시스템·사용자 · Inno 6/5).
    private static string? FindIsccPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ISCC.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* 잘못된 PATH 성분 무시 */ }
        }
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(pf86, "Inno Setup 6", "ISCC.exe"),
            Path.Combine(pf,   "Inno Setup 6", "ISCC.exe"),
            Path.Combine(lad, "Programs", "Inno Setup 6", "ISCC.exe"),   // winget 사용자 설치 기본
            Path.Combine(pf86, "Inno Setup 5", "ISCC.exe"),
            Path.Combine(pf,   "Inno Setup 5", "ISCC.exe"),
            Path.Combine(lad, "Programs", "Inno Setup 5", "ISCC.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  [폴백] 퍼블리시 산출물을 바탕화면 폴더로 드롭 — PromptFlowStudio.exe + appsettings.json + wwwroot.
    //   self-contained 단일파일이라 폴더 하나(EXE + 설정 + 정적자산)로 완결된다.
    // ─────────────────────────────────────────────────────────────────────────
    private string? DropSingleExeToDesktop(string publishDir)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var dropDir = Path.Combine(desktop, $"PromptFlowStudio_배포_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(dropDir);

            // .pdb(디버그 심볼) 제외하고 산출물 전량 복사(EXE + appsettings + 잔여 파일).
            foreach (var f in Directory.EnumerateFiles(publishDir))
            {
                if (f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(dropDir, Path.GetFileName(f)), overwrite: true);
            }
            // ★ wwwroot(정적자산) 통째 — 빠지면 흰 화면. runtimes·Tools 도 함께.
            var www = Path.Combine(publishDir, "wwwroot");
            if (Directory.Exists(www)) CopyDirectoryRecursive(www, Path.Combine(dropDir, "wwwroot"));
            var rt = Path.Combine(publishDir, "runtimes");
            if (Directory.Exists(rt)) CopyDirectoryRecursive(rt, Path.Combine(dropDir, "runtimes"));
            var tools = Path.Combine(publishDir, "Tools");   // 번들 CLI 엔진(Zero-Config 자동 탐색)
            if (Directory.Exists(tools)) CopyDirectoryRecursive(tools, Path.Combine(dropDir, "Tools"));

            var mainExe = Path.Combine(dropDir, AppExeName);
            if (!File.Exists(mainExe)) { Log("   ⚠️ 드롭 폴더에 PromptFlowStudio.exe 가 없습니다."); return null; }
            var mb = new FileInfo(mainExe).Length / (1024.0 * 1024.0);
            Log($"   › 바탕화면 드롭 완료 → {dropDir} (PromptFlowStudio.exe {mb:F1} MB)");
            return dropDir;
        }
        catch (Exception ex)
        {
            Log($"   ⚠️ 바탕화면 드롭 실패: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> RunCommandAsync(string fileName, string arguments, string workingDir, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // [로그 깨짐 방지] 자식 출력/에러를 UTF-8 로 디코드 + dotnet CLI 는 영어(ASCII)로 강제해 코드페이지 불일치 원천 차단.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"   › {e.Data}"); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(ClassifyStderr(e.Data)); };
                if (!proc.StartCompat()) throw new PlatformNotSupportedException("외부 프로세스 미지원 플랫폼");
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                if (proc.ExitCode != 0) Log($"   ↳ 종료 코드 {proc.ExitCode} (실패)");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log($"   [ERR] '{fileName}' 실행 불가: {ex.Message} (해당 도구 설치/PATH 확인)");
                return false;
            }
        }, ct);
    }

    // stderr 라인 분류 — 다수 CLI 는 INFO 도 stderr 로 출력하므로, 실제 오류 키워드만 [ERR] 로 표기.
    private static string ClassifyStderr(string line)
    {
        bool isError = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("exception", StringComparison.OrdinalIgnoreCase);
        return isError ? $"   [ERR] {line}" : $"   › {line}";
    }

    private void Log(string message) => OnLogReceived?.Invoke(message);
}
