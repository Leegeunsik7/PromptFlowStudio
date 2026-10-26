<#
.SYNOPSIS
  PromptFlow Studio v16.1.0 — 정식 FFmpeg(win-x64) 바이너리 검증 후 퍼블리시 산출물로 동봉.

.DESCRIPTION
  1) 원본 확보: -FfmpegPath → PromptFlowStudio\ffmpeg.exe → 루트 ffmpeg.exe → tools\ffmpeg\ffmpeg.exe → PATH
     (-DownloadUrl 지정 시 zip 을 받아 -ExpectedArchiveSha256 로 검증 후 tools\ffmpeg\ 에 캐시 — 해시 없는 다운로드는 거부)
  2) 검증: PE32+ / AMD64 실행 파일 헤더 · (선택) -ExpectedSha256 고정 해시 · `ffmpeg -version` 배너 프로브
  3) 동봉: <PublishDir>\ffmpeg.exe 로 복사 후 해시 재대조
  동봉된 ffmpeg.exe 는 AppIntegrityGuard.OptionalFiles 에 등록돼 있어 이후 `--pfi1-sign` 서명 대상에 자동 포함된다.

  종료 코드: 0=성공 · 10=원본 없음 · 11=PE x64 아님 · 12=해시 불일치 · 13=버전 프로브 실패 · 14=복사 검증 실패 · 15=퍼블리시 폴더 없음 · 16=다운로드 실패
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$FfmpegPath,
    [string]$ExpectedSha256,
    [string]$DownloadUrl,
    [string]$ExpectedArchiveSha256,
    [switch]$SkipVersionProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Write-Step([string]$msg) { Write-Host "[FFmpeg] $msg" -ForegroundColor Cyan }
function Fail([string]$msg, [int]$code) { Write-Host "[FFmpeg] 실패: $msg" -ForegroundColor Red; exit $code }

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

# PE 헤더 검사: MZ → e_lfanew → 'PE\0\0' · Machine=AMD64(0x8664) · OptionalHeader Magic=PE32+(0x20B) · EXECUTABLE_IMAGE 이며 DLL 아님
function Test-PeX64([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 4096
        $n = $fs.Read($buf, 0, $buf.Length)
        if ($n -lt 0x40 -or $buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { return $false }
        $pe = [System.BitConverter]::ToInt32($buf, 0x3C)
        if ($pe -lt 0x40 -or ($pe + 26) -gt $n) { return $false }
        if ($buf[$pe] -ne 0x50 -or $buf[$pe + 1] -ne 0x45 -or $buf[$pe + 2] -ne 0 -or $buf[$pe + 3] -ne 0) { return $false }
        $machine = [System.BitConverter]::ToUInt16($buf, $pe + 4)
        $chars = [System.BitConverter]::ToUInt16($buf, $pe + 22)
        $magic = [System.BitConverter]::ToUInt16($buf, $pe + 24)
        return ($machine -eq 0x8664) -and ($magic -eq 0x20B) -and (($chars -band 0x0002) -ne 0) -and (($chars -band 0x2000) -eq 0)
    }
    finally { $fs.Dispose() }
}

function Get-FfmpegBanner([string]$path) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $path
    $psi.Arguments = '-hide_banner -version'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $out = $p.StandardOutput.ReadToEndAsync()
        if (-not $p.WaitForExit(15000)) { try { $p.Kill() } catch { }; return $null }
        if ($p.ExitCode -ne 0) { return $null }
        return ($out.Result -split "`r?`n")[0]
    }
    finally { $p.Dispose() }
}

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) { Fail "퍼블리시 폴더가 없습니다: $PublishDir" 15 }
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$cacheDir = Join-Path $root 'tools\ffmpeg'

# ── 0) (선택) 공식 배포 zip 다운로드 — 아카이브 해시 고정 필수 ──
if ($DownloadUrl) {
    if (-not $ExpectedArchiveSha256) { Fail '-DownloadUrl 에는 -ExpectedArchiveSha256 가 필요합니다(무결성 미검증 다운로드 금지).' 16 }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) ('ffmpeg-' + [guid]::NewGuid().ToString('N') + '.zip')
    $ok = $false
    for ($i = 0; $i -lt 3 -and -not $ok; $i++) {
        if ($i -gt 0) { Start-Sleep -Milliseconds (500 * [math]::Pow(2, $i - 1)) }
        try { Invoke-WebRequest -Uri $DownloadUrl -OutFile $zip -UseBasicParsing -TimeoutSec 300; $ok = $true } catch { Write-Host "[FFmpeg] 다운로드 재시도($($i + 1)/3): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if (-not $ok) { Fail "다운로드 실패: $DownloadUrl" 16 }
    try {
        if ((Get-Sha256 $zip) -ne $ExpectedArchiveSha256.Trim().ToUpperInvariant()) { Fail '아카이브 SHA-256 불일치' 12 }
        $extract = Join-Path ([System.IO.Path]::GetTempPath()) ('ffmpeg-x-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
        $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
        if (-not $found) { Fail '아카이브에 ffmpeg.exe 가 없습니다.' 10 }
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $cacheDir 'ffmpeg.exe') -Force
        Remove-Item -LiteralPath $extract -Recurse -Force
    }
    finally { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force } }
    if (-not $FfmpegPath) { $FfmpegPath = Join-Path $cacheDir 'ffmpeg.exe' }
}

# ── 1) 원본 탐색 ──
$src = $null
if ($FfmpegPath -and -not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) { Fail "지정한 ffmpeg.exe 가 없습니다: $FfmpegPath" 10 }   # 명시 경로는 폴백하지 않음
foreach ($c in @($FfmpegPath, (Join-Path $root 'PromptFlowStudio\ffmpeg.exe'), (Join-Path $root 'ffmpeg.exe'), (Join-Path $cacheDir 'ffmpeg.exe'))) {
    if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { $src = (Resolve-Path -LiteralPath $c).Path; break }
}
if (-not $src -and -not $FfmpegPath) {
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { $src = $cmd.Source }
}
if (-not $src) { Fail 'ffmpeg.exe 원본을 찾을 수 없습니다(-FfmpegPath 또는 -DownloadUrl).' 10 }

# ── 2) 검증 ──
if (-not (Test-PeX64 $src)) { Fail "PE32+ x64 실행 파일이 아닙니다: $src" 11 }
$hash = Get-Sha256 $src
if ($ExpectedSha256 -and $hash -ne $ExpectedSha256.Trim().ToUpperInvariant()) { Fail "SHA-256 불일치: $hash" 12 }
if (-not $SkipVersionProbe) {
    $banner = Get-FfmpegBanner $src
    if (-not $banner -or $banner -notmatch '^ffmpeg version ') { Fail "ffmpeg -version 프로브 실패: $src" 13 }
    Write-Step $banner
}

# ── 3) 동봉 + 해시 재대조 ──
$dst = Join-Path $PublishDir 'ffmpeg.exe'
if ($src -ne $dst) { Copy-Item -LiteralPath $src -Destination $dst -Force }
if ((Get-Sha256 $dst) -ne $hash) { Fail '복사본 해시 불일치' 14 }
Write-Step "동봉 완료: $dst (SHA-256 $hash) — pfi1.manifest 서명 대상(OptionalFiles) 자동 포함"
exit 0
