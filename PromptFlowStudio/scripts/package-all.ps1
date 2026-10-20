#requires -Version 5.1
<#
.SYNOPSIS
  PromptFlow Studio v4.4.0 — 멀티플랫폼 원클릭 패키징(Win EXE / Android APK) + iOS 는 package-all.sh 체이닝.
.EXAMPLE
  pwsh scripts/package-all.ps1 -Target all -Version 4.4.0 -Out outputs
#>
[CmdletBinding()]
param(
    [ValidateSet('win','android','ios','all')][string]$Target = 'all',
    [string]$Version = '4.4.0',
    [string]$Out = 'outputs'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root $Out
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
function Info($m){ Write-Host "[PKG] $m" -ForegroundColor Cyan }

# ── 💻 Win EXE (dotnet publish 단일파일 + Inno Setup 있으면 인스톨러) ──
if ($Target -in 'win','all') {
    Info 'Win EXE 패키징…'
    $desktop = Join-Path $root 'PromptFlowStudio.csproj'
    & dotnet publish $desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $outDir 'win')
    if ($LASTEXITCODE -ne 0) { throw 'Win publish 실패' }
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue)
    if ($iscc) { & iscc "/DMyAppVersion=$Version" (Join-Path $root 'installer\promptflow.iss'); Info 'Inno Setup 인스톨러 생성' }
    else { Info 'Inno Setup(iscc) 미설치 — 단일 EXE 출력만(설치본 생략)' }
    Copy-Item (Join-Path $outDir 'win\PromptFlowStudio.exe') (Join-Path $outDir "PromptFlowStudio-v$Version-win-x64.exe") -Force -ErrorAction SilentlyContinue
}

# ── 🤖 Android APK ──
if ($Target -in 'android','all') {
    Info 'Android APK 패키징…'
    $mobile = Join-Path $root '..\PromptFlowStudio.Mobile\PromptFlowStudio.Mobile.csproj'
    & dotnet publish $mobile -f net8.0-android -c Release -p:AndroidPackageFormat=apk -p:ApplicationDisplayVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw 'Android publish 실패' }
    $apk = Get-ChildItem -Path (Split-Path $mobile) -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($apk) { Copy-Item $apk.FullName (Join-Path $outDir "PromptFlowStudio-v$Version-arm64-v8a.apk") -Force }
}

# ── 🍎 iOS IPA (Bash 체이닝 · macOS 전용) ──
if ($Target -in 'ios','all') {
    $bash = (Get-Command bash -ErrorAction SilentlyContinue)
    if ($bash) { Info 'iOS IPA — package-all.sh 체이닝'; & bash (Join-Path $PSScriptRoot 'package-all.sh') --target ios --version $Version --out $Out }
    else { Info 'bash 미탐지 — iOS 는 macOS 에서 package-all.sh 로 빌드하세요(Graceful 건너뜀)' }
}

Info "완료 → $outDir"
Get-ChildItem $outDir -File | ForEach-Object { Write-Host ("  - " + $_.Name) -ForegroundColor Green }
