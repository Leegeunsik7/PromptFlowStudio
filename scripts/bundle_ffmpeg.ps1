<#
.SYNOPSIS
  PromptFlow Studio v16.1.0 ???뺤떇 FFmpeg(win-x64) 諛붿씠?덈━ 寃利????쇰툝由ъ떆 ?곗텧臾쇰줈 ?숇큺.

.DESCRIPTION
  1) ?먮낯 ?뺣낫: -FfmpegPath ??PromptFlowStudio\ffmpeg.exe ??猷⑦듃 ffmpeg.exe ??tools\ffmpeg\ffmpeg.exe ??PATH
     (-DownloadUrl 吏????zip ??諛쏆븘 -ExpectedArchiveSha256 濡?寃利???tools\ffmpeg\ ??罹먯떆 ???댁떆 ?녿뒗 ?ㅼ슫濡쒕뱶??嫄곕?)
  2) 寃利? PE32+ / AMD64 ?ㅽ뻾 ?뚯씪 ?ㅻ뜑 쨌 (?좏깮) -ExpectedSha256 怨좎젙 ?댁떆 쨌 `ffmpeg -version` 諛곕꼫 ?꾨줈釉?
  3) ?숇큺: <PublishDir>\ffmpeg.exe 濡?蹂듭궗 ???댁떆 ?щ?議?
  ?숇큺??ffmpeg.exe ??AppIntegrityGuard.OptionalFiles ???깅줉???덉뼱 ?댄썑 `--pfi1-sign` ?쒕챸 ??곸뿉 ?먮룞 ?ы븿?쒕떎.

  醫낅즺 肄붾뱶: 0=?깃났 쨌 10=?먮낯 ?놁쓬 쨌 11=PE x64 ?꾨떂 쨌 12=?댁떆 遺덉씪移?쨌 13=踰꾩쟾 ?꾨줈釉??ㅽ뙣 쨌 14=蹂듭궗 寃利??ㅽ뙣 쨌 15=?쇰툝由ъ떆 ?대뜑 ?놁쓬 쨌 16=?ㅼ슫濡쒕뱶 ?ㅽ뙣
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
function Fail([string]$msg, [int]$code) { Write-Host "[FFmpeg] ?ㅽ뙣: $msg" -ForegroundColor Red; exit $code }

function Get-Sha256([string]$path) {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                return [System.Convert]::ToHexString($sha.ComputeHash($fs))
            }
            finally { $fs.Dispose() }
        } catch { Start-Sleep -Milliseconds 150 }
    }
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}
            finally { $fs.Dispose() }
        } catch { Start-Sleep -Milliseconds 150 }
    }
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

# PE ?ㅻ뜑 寃?? MZ ??e_lfanew ??'PE\0\0' 쨌 Machine=AMD64(0x8664) 쨌 OptionalHeader Magic=PE32+(0x20B) 쨌 EXECUTABLE_IMAGE ?대ŉ DLL ?꾨떂
function Test-PeX64([string]$path) {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
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
        } catch { Start-Sleep -Milliseconds 150 }
    }
    return $false
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

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) { Fail "?쇰툝由ъ떆 ?대뜑媛 ?놁뒿?덈떎: $PublishDir" 15 }
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$cacheDir = Join-Path $root 'tools\ffmpeg'

# ?? 0) (?좏깮) 怨듭떇 諛고룷 zip ?ㅼ슫濡쒕뱶 ???꾩뭅?대툕 ?댁떆 怨좎젙 ?꾩닔 ??
if ($DownloadUrl) {
    if (-not $ExpectedArchiveSha256) { Fail '-DownloadUrl ?먮뒗 -ExpectedArchiveSha256 媛 ?꾩슂?⑸땲??臾닿껐??誘멸?利??ㅼ슫濡쒕뱶 湲덉?).' 16 }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) ('ffmpeg-' + [guid]::NewGuid().ToString('N') + '.zip')
    $ok = $false
    for ($i = 0; $i -lt 3 -and -not $ok; $i++) {
        if ($i -gt 0) { Start-Sleep -Milliseconds (500 * [math]::Pow(2, $i - 1)) }
        try { Invoke-WebRequest -Uri $DownloadUrl -OutFile $zip -UseBasicParsing -TimeoutSec 300; $ok = $true } catch { Write-Host "[FFmpeg] ?ㅼ슫濡쒕뱶 ?ъ떆??$($i + 1)/3): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if (-not $ok) { Fail "?ㅼ슫濡쒕뱶 ?ㅽ뙣: $DownloadUrl" 16 }
    try {
        if ((Get-Sha256 $zip) -ne $ExpectedArchiveSha256.Trim().ToUpperInvariant()) { Fail '?꾩뭅?대툕 SHA-256 遺덉씪移? 12 }
        $extract = Join-Path ([System.IO.Path]::GetTempPath()) ('ffmpeg-x-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
        $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
        if (-not $found) { Fail '?꾩뭅?대툕??ffmpeg.exe 媛 ?놁뒿?덈떎.' 10 }
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $cacheDir 'ffmpeg.exe') -Force
        Remove-Item -LiteralPath $extract -Recurse -Force
    }
    finally { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force } }
    if (-not $FfmpegPath) { $FfmpegPath = Join-Path $cacheDir 'ffmpeg.exe' }
}

# ?? 1) ?먮낯 ?먯깋 ??
$src = $null
if ($FfmpegPath -and -not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) { Fail "吏?뺥븳 ffmpeg.exe 媛 ?놁뒿?덈떎: $FfmpegPath" 10 }   # 紐낆떆 寃쎈줈???대갚?섏? ?딆쓬
foreach ($c in @($FfmpegPath, (Join-Path $root 'PromptFlowStudio\ffmpeg.exe'), (Join-Path $root 'ffmpeg.exe'), (Join-Path $cacheDir 'ffmpeg.exe'))) {
    if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { $src = (Resolve-Path -LiteralPath $c).Path; break }
}
if (-not $src -and -not $FfmpegPath) {
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { $src = $cmd.Source }
}
if (-not $src) { Fail 'ffmpeg.exe ?먮낯??李얠쓣 ???놁뒿?덈떎(-FfmpegPath ?먮뒗 -DownloadUrl).' 10 }

# ?? 2) 寃利???
if (-not (Test-PeX64 $src)) { Fail "PE32+ x64 ?ㅽ뻾 ?뚯씪???꾨떃?덈떎: $src" 11 }
$hash = Get-Sha256 $src
if ($ExpectedSha256 -and $hash -ne $ExpectedSha256.Trim().ToUpperInvariant()) { Fail "SHA-256 遺덉씪移? $hash" 12 }
if (-not $SkipVersionProbe) {
    $banner = Get-FfmpegBanner $src
    if (-not $banner -or $banner -notmatch '^ffmpeg version ') { Fail "ffmpeg -version ?꾨줈釉??ㅽ뙣: $src" 13 }
    Write-Step $banner
}

# ?? 3) ?숇큺 + ?댁떆 ?щ?議???
$dst = Join-Path $PublishDir 'ffmpeg.exe'
if ($src -ne $dst) { Copy-Item -LiteralPath $src -Destination $dst -Force }
if ((Get-Sha256 $dst) -ne $hash) { Fail '蹂듭궗蹂??댁떆 遺덉씪移? 14 }
Write-Step "?숇큺 ?꾨즺: $dst (SHA-256 $hash) ??pfi1.manifest ?쒕챸 ???OptionalFiles) ?먮룞 ?ы븿"
exit 0
