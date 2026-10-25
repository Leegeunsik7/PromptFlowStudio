<#
.SYNOPSIS
  PromptFlow Studio v16.4.0 릴리스 파이프라인 — publish(R2R) → ffmpeg 검증·동봉 → RSA-2048 매니페스트(pfi1.manifest) 서명 → (선택) WebView2 redist + Inno Setup 인스톨러.
  로컬·GitHub Actions(.github/workflows/release.yml) 공용 진입점.

.DESCRIPTION
  1) dotnet publish -c Release -r win-x64 -p:PublishReadyToRun=true
  2) scripts\bundle_ffmpeg.ps1 — 정식 FFmpeg(PE32+ x64 · 버전 프로브 · 선택 해시 고정) 검증 후 동봉(OptionalFiles → 서명 대상 자동 포함)
  3) PromptFlowStudio.exe --pfi1-sign <dir> → AppIntegrityGuard.SignManifest 로 서명 후 내장 공개키로 재검증
  4) -BuildInstaller: (-FetchWebView2 시 scripts\fetch_webview2.ps1 로 installer\redist\ 확보) ISCC 로 installer\promptflow_studio.iss 컴파일 → installer\Output\
     -RequireWebView2Redist: redist 미동봉 시 ISCC 컴파일 거부(CI 정식 릴리스용)
  Authenticode(선택): -AuthenticodePfxPath(암호 환경변수 PROMPTFLOW_PFX_PASSWORD) 또는 -AuthenticodeThumbprint(EV/HSM) 지정 시
     scripts\sign_authenticode.ps1 로 PromptFlowStudio.exe(퍼블리시 직후) · setup.exe(ISCC 직후 · BOM 산출 전) 서명. -RequireAuthenticode 시 미제공 실패.
  FFmpeg 원본은 -FfmpegPath 또는 -FfmpegUrl + -FfmpegArchiveSha256(해시 고정 zip) 로 지정.

  개인키(base64 PKCS#8 또는 PEM)는 저장소에 두지 않는다. 우선순위:
    환경변수 PROMPTFLOW_SIGNING_KEY → -PrivateKeyPath(기본 %USERPROFILE%\.promptflow\pfi1_signing.key)
  키는 서명 자식 프로세스 환경변수로만 전달(명령줄 노출 없음)하고 종료 시 원복한다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build_titan_release.ps1 -FfmpegPath C:\tools\ffmpeg.exe -BuildInstaller
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$FfmpegPath,
    [string]$FfmpegSha256,
    [string]$FfmpegUrl,
    [string]$FfmpegArchiveSha256,
    [string]$PrivateKeyPath,
    [string]$AppVersion = '16.4.0',
    [string]$AuthenticodePfxPath,
    [string]$AuthenticodeThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireAuthenticode,
    [switch]$BuildInstaller,
    [switch]$FetchWebView2,
    [ValidateSet('Bootstrapper', 'Standalone')][string]$WebView2Mode = 'Bootstrapper',
    [switch]$RequireWebView2Redist,
    [switch]$AllowMissingFfmpeg
)

Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputDir) { $OutputDir = Join-Path $root ('publish\v' + $AppVersion) }
if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path $env:USERPROFILE '.promptflow\pfi1_signing.key' }
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "[Titan] $msg" -ForegroundColor Cyan }
function Fail([string]$msg, [int]$code) { Write-Host "[Titan] 실패: $msg" -ForegroundColor Red; exit $code }

$project = Join-Path $root 'PromptFlowStudio\PromptFlowStudio.csproj'
if (-not (Test-Path $project)) { Fail "프로젝트를 찾을 수 없습니다: $project" 10 }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir).TrimEnd('\')

# ── 0) 개인키 확보(서명 불가 상태로 퍼블리시하지 않도록 선검사) ──
$signingKey = $env:PROMPTFLOW_SIGNING_KEY
if ([string]::IsNullOrWhiteSpace($signingKey)) {
    if (-not (Test-Path $PrivateKeyPath)) { Fail "서명 개인키가 없습니다(PROMPTFLOW_SIGNING_KEY 또는 $PrivateKeyPath)." 11 }
    $signingKey = (Get-Content -Raw -LiteralPath $PrivateKeyPath).Trim()
}

# ── 1) 인스톨러 컴파일러 선검사 ──
$iscc = $null
if ($BuildInstaller) {
    foreach ($c in @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))) {
        if ($c -and (Test-Path -LiteralPath $c)) { $iscc = $c; break }
    }
    if (-not $iscc) { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
    if (-not $iscc) { Fail 'Inno Setup 6(ISCC.exe)을 찾을 수 없습니다.' 13 }
    if ($FetchWebView2) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\fetch_webview2.ps1') -Mode $WebView2Mode
        if ($LASTEXITCODE -ne 0) { Fail "WebView2 redist 확보 실패(fetch_webview2.ps1 종료 코드 $LASTEXITCODE)" 14 }
    }
}

# ── 2) 퍼블리시 ──
Write-Step "dotnet publish ($Runtime · ReadyToRun) → $OutputDir"
if (Test-Path -LiteralPath $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
& dotnet publish $project -c Release -r $Runtime -p:PublishReadyToRun=true -o $OutputDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 종료 코드 $LASTEXITCODE" 20 }

$exe = Join-Path $OutputDir 'PromptFlowStudio.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail "퍼블리시 산출물에 PromptFlowStudio.exe 가 없습니다." 21 }

# Authenticode(선택) — scripts\sign_authenticode.ps1 위임. 인증서 미제공이면 0(건너뜀), -RequireAuthenticode 면 실패.
$authenticodeSigned = $false
function Invoke-Authenticode([string[]]$targets) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'scripts\sign_authenticode.ps1'), '-Files') + $targets + @('-TimestampUrl', $TimestampUrl)
    if ($AuthenticodeThumbprint) { $a += @('-CertThumbprint', $AuthenticodeThumbprint) }
    elseif ($AuthenticodePfxPath) { $a += @('-PfxPath', $AuthenticodePfxPath) }
    if ($RequireAuthenticode) { $a += '-Require' }
    & powershell.exe @a
    if ($LASTEXITCODE -ne 0) { Fail "Authenticode 서명 실패(sign_authenticode.ps1 종료 코드 $LASTEXITCODE)" 50 }
}
$wantAuthenticode = [bool]($AuthenticodePfxPath -or $AuthenticodeThumbprint)
if ($wantAuthenticode -or $RequireAuthenticode) { Invoke-Authenticode @($exe); $authenticodeSigned = $wantAuthenticode }

# ── 3) ffmpeg 검증·동봉(scripts\bundle_ffmpeg.ps1) ──
$ffmpegBundled = $false
$bundleArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'scripts\bundle_ffmpeg.ps1'), '-PublishDir', $OutputDir)
if ($FfmpegPath) { $bundleArgs += @('-FfmpegPath', $FfmpegPath) }
if ($FfmpegSha256) { $bundleArgs += @('-ExpectedSha256', $FfmpegSha256) }
if ($FfmpegUrl) { $bundleArgs += @('-DownloadUrl', $FfmpegUrl, '-ExpectedArchiveSha256', $FfmpegArchiveSha256) }
& powershell.exe @bundleArgs
if ($LASTEXITCODE -eq 0) { $ffmpegBundled = $true }
elseif ($LASTEXITCODE -eq 10 -and $AllowMissingFfmpeg) { Write-Host '[Titan] 경고: ffmpeg.exe 미동봉(-AllowMissingFfmpeg) — 정식 릴리스에는 사용하지 마세요.' -ForegroundColor Yellow }
else { Fail "ffmpeg 동봉 실패(bundle_ffmpeg.ps1 종료 코드 $LASTEXITCODE)" 22 }

# ── 4) RSA-2048 매니페스트 서명(AppIntegrityGuard.SignManifest) ──
Write-Step 'pfi1.manifest RSA-2048 서명'
$stdout = Join-Path ([System.IO.Path]::GetTempPath()) ('pfi1-sign-' + [guid]::NewGuid().ToString('N') + '.txt')
$prevKey = $env:PROMPTFLOW_SIGNING_KEY
try {
    $env:PROMPTFLOW_SIGNING_KEY = $signingKey
    $p = Start-Process -FilePath $exe -ArgumentList @('--pfi1-sign', ('"' + $OutputDir + '"')) `
        -WorkingDirectory $OutputDir -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout
    $signCode = $p.ExitCode
}
finally {
    $env:PROMPTFLOW_SIGNING_KEY = $prevKey
    $signingKey = $null
}
$signMsg = if (Test-Path -LiteralPath $stdout) { (Get-Content -Raw -LiteralPath $stdout).Trim(); Remove-Item -LiteralPath $stdout -Force } else { '' }
switch ($signCode) {
    0 { Write-Step $signMsg }
    2 { Fail "서명 키 미전달: $signMsg" 30 }
    4 { Fail "개인키가 앱 내장 공개키와 쌍이 아닙니다: $signMsg" 31 }
    default { Fail "서명 실패(코드 $signCode): $signMsg" 32 }
}

$manifest = Join-Path $OutputDir 'pfi1.manifest'
if (-not (Test-Path -LiteralPath $manifest)) { Fail 'pfi1.manifest 가 생성되지 않았습니다.' 33 }
Get-Content -LiteralPath $manifest | Where-Object { $_ -notlike 'SIG *' } | ForEach-Object { Write-Host "  $_" }
if ($ffmpegBundled -and -not (Select-String -LiteralPath $manifest -Pattern ' ffmpeg\.exe$' -Quiet)) { Fail 'ffmpeg.exe 가 서명 대상에 포함되지 않았습니다.' 34 }

# ── 4.5) v18.3.0 헤드리스 스모크(--smoke-test · 5초 가드) + 배포 번들 SHA-256 매니페스트(release-checksums.sha256 · 최종 바이트 기준) ──
Write-Step '헤드리스 스모크(--smoke-test)'
$smoke = Start-Process -FilePath $exe -ArgumentList @('--smoke-test') -WorkingDirectory $OutputDir -NoNewWindow -PassThru
if (-not $smoke.WaitForExit(5000)) { try { $smoke.Kill() } catch { }; Fail '스모크 타임아웃(5초) — UI 기동 또는 부트스트랩 교착' 35 }
if ($smoke.ExitCode -ne 0) { Fail "스모크 실패(종료 코드 $($smoke.ExitCode)) — 자세한 내용: %LOCALAPPDATA%\PromptFlowStudio\logs\shell.log" 35 }
Write-Step 'release-checksums.sha256 기록'
$sum = Start-Process -FilePath $exe -ArgumentList @('--release-checksums', ('"' + $OutputDir + '"')) -WorkingDirectory $OutputDir -NoNewWindow -PassThru -Wait
if ($sum.ExitCode -ne 0) { Fail "체크섬 매니페스트 기록 실패(종료 코드 $($sum.ExitCode))" 36 }

# ── 5) Inno Setup 인스톨러 ──
if ($BuildInstaller) {
    Write-Step "Inno Setup 컴파일 ($AppVersion)"
    $isccArgs = @("/DPublishDir=$OutputDir", "/DAppVersion=$AppVersion")
    if (-not $ffmpegBundled) { $isccArgs += '/DAllowMissingFfmpeg' }
    if ($RequireWebView2Redist) { $isccArgs += '/DRequireWebView2Redist' }
    $isccArgs += (Join-Path $root 'installer\promptflow_studio.iss')
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { Fail "ISCC 종료 코드 $LASTEXITCODE" 40 }

    # 릴리스 BOM(<setup>.release.json) — scripts\verify_release.ps1 가 해시를 재산출해 대조. 인스톨러 감지 순서와 동일(Standalone 우선).
    $setup = Join-Path $root "installer\Output\PromptFlowStudio-$AppVersion-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) { Fail "인스톨러 산출물이 없습니다: $setup" 41 }
    if ($authenticodeSigned) { Invoke-Authenticode @($setup) }   # BOM 해시는 서명된 최종 setup.exe 기준
    $redist = $null
    foreach ($n in @('MicrosoftEdgeWebView2RuntimeInstallerX64.exe', 'MicrosoftEdgeWebview2Setup.exe')) {
        $rp = Join-Path $root "installer\redist\$n"
        if (Test-Path -LiteralPath $rp) { $redist = [ordered]@{ name = $n; sha256 = (Get-FileHash -LiteralPath $rp -Algorithm SHA256).Hash }; break }
    }
    $ff = Join-Path $OutputDir 'ffmpeg.exe'
    $bom = [ordered]@{
        schema         = 'pfs-release-bom/1'
        appVersion     = $AppVersion
        setup          = [ordered]@{ name = [System.IO.Path]::GetFileName($setup); sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash; size = (Get-Item -LiteralPath $setup).Length }
        manifestSha256 = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
        ffmpegBundled  = $ffmpegBundled
        ffmpegSha256   = $(if ($ffmpegBundled) { (Get-FileHash -LiteralPath $ff -Algorithm SHA256).Hash } else { $null })
        webView2Redist = $redist
        authenticode   = $authenticodeSigned
    }
    [System.IO.File]::WriteAllText($setup + '.release.json', ($bom | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Write-Step "릴리스 BOM: $setup.release.json"
}

Write-Step "완료: $OutputDir"
exit 0
