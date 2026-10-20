#requires -Version 5.1
<#
.SYNOPSIS
  PromptFlow Studio — 원클릭 안드로이드 APK/AAB 빌드 & 서명 자동화.
.DESCRIPTION
  1) 안드로이드 SDK/빌드툴 자동 탐색  2) dotnet publish(net8.0-android · Release)
  3) Keystore 자동 생성/바인딩 서명 + zipalign  4) outputs/ 단일 디렉터리로 산출물 이관.
  ⚠ 전제: .NET MAUI 워크로드(`dotnet workload install maui-android`) + Android SDK + JDK(keytool).
.EXAMPLE
  pwsh ./scripts/build-android-apk.ps1
  pwsh ./scripts/build-android-apk.ps1 -Aab -Version 4.1.0 -StorePass "****" -KeyPass "****"
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version       = "4.1.0",
    [string]$Abi           = "arm64-v8a",
    [string]$ProjectPath   = "",
    [string]$KeystorePath  = "",
    [string]$KeyAlias      = "promptflowstudio",
    [string]$StorePass     = $env:PFS_KEYSTORE_PASS,   # 시크릿 하드코딩 금지 - 환경변수 또는 -StorePass
    [string]$KeyPass       = $env:PFS_KEY_PASS,        # 미지정 시 StorePass 사용
    [switch]$Aab
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($StorePass)) { Write-Host "[FAIL] 키스토어 비밀번호가 없습니다 - 환경변수 PFS_KEYSTORE_PASS 설정 또는 -StorePass 지정" -ForegroundColor Red; exit 1 }
if ([string]::IsNullOrEmpty($KeyPass)) { $KeyPass = $StorePass }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Info($m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "[FAIL] $m" -ForegroundColor Red; exit 1 }

# ── 경로 해석 ──────────────────────────────────────────────
$root = Split-Path -Parent $PSScriptRoot                        # scripts/ 상위 = 프로젝트 루트
if (-not $ProjectPath) {
    $candidates = @(
        (Join-Path $root "..\PromptFlowStudio.Mobile\PromptFlowStudio.Mobile.csproj"),
        (Join-Path $root "PromptFlowStudio.Mobile\PromptFlowStudio.Mobile.csproj")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $ProjectPath = (Resolve-Path $c).Path; break } }
}
if (-not $ProjectPath -or -not (Test-Path $ProjectPath)) { Die "MAUI 프로젝트(.csproj)를 찾을 수 없습니다. -ProjectPath 로 지정하세요." }
Info "MAUI 프로젝트: $ProjectPath"

$outputs = Join-Path $root "outputs"
New-Item -ItemType Directory -Force -Path $outputs | Out-Null

# ── 1) 안드로이드 SDK / 빌드툴 자동 탐색 ───────────────────
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = $env:ANDROID_SDK_ROOT }
if (-not $sdk) {
    $guess = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path $guess) { $sdk = $guess }
}
if (-not $sdk -or -not (Test-Path $sdk)) { Warn "Android SDK 미탐지 — dotnet 내장 SDK/서명에 의존합니다." }
else { Info "Android SDK: $sdk" }

$buildTools = ""
if ($sdk -and (Test-Path (Join-Path $sdk "build-tools"))) {
    $bt = Get-ChildItem (Join-Path $sdk "build-tools") -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($bt) { $buildTools = $bt.FullName; Info "build-tools: $($bt.Name)" }
}
function Tool($name) {
    if ($buildTools) { $p = Join-Path $buildTools ($name + ".exe"); if (Test-Path $p) { return $p } }
    return $name   # PATH 폴백
}

# ── 2) Keystore 자동 생성/바인딩 ───────────────────────────
if (-not $KeystorePath) { $KeystorePath = Join-Path $root "keystore\promptflowstudio.keystore" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $KeystorePath) | Out-Null
if (-not (Test-Path $KeystorePath)) {
    Warn "Keystore 없음 — 자동 생성합니다: $KeystorePath (배포 전 안전한 비밀번호로 재발급 권장)"
    $keytool = "keytool"
    & $keytool -genkeypair -v -keystore $KeystorePath -alias $KeyAlias `
        -keyalg RSA -keysize 2048 -validity 10950 `
        -storepass $StorePass -keypass $KeyPass `
        -dname "CN=PromptFlow Studio, O=ANG Technology, C=KR"
    if ($LASTEXITCODE -ne 0) { Die "keytool keystore 생성 실패(JDK 설치/PATH 확인)." }
    Ok "Keystore 생성 완료"
} else { Info "Keystore 바인딩: $KeystorePath" }

# ── 3) dotnet publish (서명·정렬 포함) ─────────────────────
$fmt = if ($Aab) { "aab" } else { "apk" }
Info "publish 시작 — net8.0-android / $Configuration / $fmt"
$publishArgs = @(
    "publish", $ProjectPath,
    "-f", "net8.0-android",
    "-c", $Configuration,
    "-p:ApplicationDisplayVersion=$Version",
    "-p:AndroidPackageFormat=$fmt",
    "-p:RuntimeIdentifier=android-arm64",
    "-p:AndroidKeyStore=true",
    "-p:AndroidSigningKeyStore=$KeystorePath",
    "-p:AndroidSigningKeyAlias=$KeyAlias",
    "-p:AndroidSigningStorePass=$StorePass",
    "-p:AndroidSigningKeyPass=$KeyPass"
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Die "dotnet publish 실패." }
Ok "publish 완료"

# ── 산출물 탐색(서명본 우선) ───────────────────────────────
$binDir = Join-Path (Split-Path -Parent $ProjectPath) "bin\$Configuration\net8.0-android"
$pattern = if ($Aab) { "*-Signed.aab", "*.aab" } else { "*-Signed.apk", "*.apk" }
$artifact = $null
foreach ($pat in $pattern) {
    $found = Get-ChildItem -Path $binDir -Recurse -Filter $pat -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { $artifact = $found.FullName; break }
}
if (-not $artifact) { Die "산출물($fmt)을 찾을 수 없습니다: $binDir" }
Info "산출물: $artifact"

# ── 4) zipalign 검증(APK) + 서명 검증 + outputs 이관 ───────
if (-not $Aab -and $buildTools) {
    $zipalign = Tool "zipalign"
    & $zipalign -c 4 $artifact 2>$null
    if ($LASTEXITCODE -eq 0) { Ok "zipalign 정렬 검증 통과(4바이트)" } else { Warn "zipalign 미정렬 — 재정렬 시도"; $aligned = [System.IO.Path]::ChangeExtension($artifact, ".aligned.apk"); & $zipalign -f 4 $artifact $aligned; if ($LASTEXITCODE -eq 0) { $artifact = $aligned; Ok "재정렬 완료" } }
    $apksigner = Tool "apksigner"
    try { & $apksigner verify --print-certs $artifact | Out-Null; if ($LASTEXITCODE -eq 0) { Ok "apksigner 서명 검증 통과" } else { Warn "apksigner 서명 검증 경고" } } catch { Warn "apksigner 미탐지 — dotnet 서명 결과 사용" }
}

$finalName = "PromptFlowStudio-v$Version-$Abi.$fmt"
$finalPath = Join-Path $outputs $finalName
Copy-Item -Path $artifact -Destination $finalPath -Force
$size = [Math]::Round((Get-Item $finalPath).Length / 1MB, 2)
Ok "산출물 이관 완료 → $finalPath ($size MB)"
Write-Host ""
Write-Host "════════════════════════════════════════════════" -ForegroundColor DarkCyan
Ok "빌드 성공: $finalName"
Write-Host "  경로: $finalPath" -ForegroundColor Gray
Write-Host "  설치: adb install -r `"$finalPath`"" -ForegroundColor Gray
Write-Host "════════════════════════════════════════════════" -ForegroundColor DarkCyan
