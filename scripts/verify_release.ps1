<#
.SYNOPSIS
  PromptFlow Studio v16.4.0 — 정식 출시 아티팩트 최종 검인(Golden Master Release Verification).

.DESCRIPTION
  build_titan_release.ps1 -BuildInstaller 산출물(setup.exe + <setup>.release.json BOM + 퍼블리시 폴더)을 재산출·대조한다.
    [SETUP]    PE 구조 · VersionInfo(제품명/버전) · BOM SHA-256 일치 · Authenticode(setup.exe · PromptFlowStudio.exe — 미서명은 경고, BOM authenticode=true 또는 -RequireAuthenticode 시 실패)
    [MANIFEST] pfi1.manifest 형식(PFI1 집계 헤더 · SIG = RSA-2048 256B) · BOM 해시 · PromptFlowStudio.exe --pfi1-verify(내장 공개키 RSA 검증)
    [PE]       매니페스트 등재 바이너리 + PromptFlowStudio.exe 의 PE32+ / AMD64 헤더
    [FFMPEG]   서명 대상 등재 · PE32+ x64 · BOM 해시(미동봉: -RequireFfmpeg 시 실패, 아니면 경고)
    [WEBVIEW2] BOM 기록 redist 의 실물 해시 · Microsoft Authenticode(미동봉: -RequireWebView2Redist 시 실패, 아니면 경고)
    [CYCLE]    -InstallCycle: 임시 폴더 사일런트 설치 → 설치본 RSA 검증 → 사일런트 제거 → %LocalAppData%\PromptFlowStudio
               (Config\vault.enc · 설정 JSON 등, logs\ 제외) 바이트 단위 보존 확인. 사용자 데이터가 없으면 픽스처를 심고 종료 시 제거.
               ⚠ 실제 설치/제거를 수행(현재 사용자 레지스트리·ACL 변경) — CI 일회용 러너 전용 권장.
  결과는 <setup>.verify.json 으로 기록. 종료 코드: 0=전 항목 PASS(경고 허용) · 1=FAIL 존재 · 2=입력 산출물 없음
#>
[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$PublishDir,
    [switch]$RequireFfmpeg,
    [switch]$RequireWebView2Redist,
    [switch]$RequireAuthenticode,
    [switch]$InstallCycle,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$area, [string]$check, [string]$status, [string]$detail) {
    $results.Add([pscustomobject]@{ area = $area; check = $check; status = $status; detail = $detail })
    $color = switch ($status) { 'PASS' { 'Green' } 'WARN' { 'Yellow' } default { 'Red' } }
    Write-Host ("[{0}] {1,-9} {2} — {3}" -f $status, $area, $check, $detail) -ForegroundColor $color
}
function Assert-That([string]$area, [string]$check, [bool]$ok, [string]$detail) { Add-Result $area $check $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail }
function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

# PE 헤더 판독: 'PE32+/AMD64' · 'PE32/I386' 등 문자열, 비PE 는 $null
function Get-PeKind([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 4096
        $n = $fs.Read($buf, 0, $buf.Length)
        if ($n -lt 0x40 -or $buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { return $null }
        $pe = [System.BitConverter]::ToInt32($buf, 0x3C)
        if ($pe -lt 0x40 -or ($pe + 26) -gt $n) { return $null }
        if ($buf[$pe] -ne 0x50 -or $buf[$pe + 1] -ne 0x45 -or $buf[$pe + 2] -ne 0 -or $buf[$pe + 3] -ne 0) { return $null }
        $machine = switch ([System.BitConverter]::ToUInt16($buf, $pe + 4)) { 0x8664 { 'AMD64' } 0x014C { 'I386' } 0xAA64 { 'ARM64' } default { 'OTHER' } }
        $magic = switch ([System.BitConverter]::ToUInt16($buf, $pe + 24)) { 0x20B { 'PE32+' } 0x10B { 'PE32' } default { 'PE?' } }
        return "$magic/$machine"
    }
    finally { $fs.Dispose() }
}

function Invoke-Pfi1Verify([string]$exe, [string]$dir) {
    $out = Join-Path ([System.IO.Path]::GetTempPath()) ('pfi1-verify-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $p = Start-Process -FilePath $exe -ArgumentList @('--pfi1-verify', ('"' + $dir + '"')) -WorkingDirectory $dir -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out
        $msg = if (Test-Path -LiteralPath $out) { (Get-Content -Raw -LiteralPath $out).Trim() } else { '' }
        return [pscustomobject]@{ Code = $p.ExitCode; Message = $msg }
    }
    finally { if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force } }
}

function Get-DataSnapshot([string]$dataRoot) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $dataRoot)) { return $map }
    $prefix = $dataRoot.TrimEnd('\').Length + 1
    Get-ChildItem -LiteralPath $dataRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName.Substring($prefix) -notlike 'logs\*' } |
        ForEach-Object { $map[$_.FullName.Substring($prefix)] = Get-Sha256 $_.FullName }
    return $map
}

# ── 입력 산출물 해석 ──
if (-not $SetupPath) {
    $latest = Get-ChildItem -Path (Join-Path $root 'installer\Output') -Filter 'PromptFlowStudio-*-win-x64-setup.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($latest) { $SetupPath = $latest.FullName }
}
if (-not $SetupPath -or -not (Test-Path -LiteralPath $SetupPath)) { Write-Host '[FAIL] 인스톨러(PromptFlowStudio-*-win-x64-setup.exe)를 찾을 수 없습니다.' -ForegroundColor Red; exit 2 }
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$bomPath = $SetupPath + '.release.json'
if (-not (Test-Path -LiteralPath $bomPath)) { Write-Host "[FAIL] 릴리스 BOM 이 없습니다: $bomPath" -ForegroundColor Red; exit 2 }
$bom = Get-Content -Raw -LiteralPath $bomPath | ConvertFrom-Json
if (-not $PublishDir) { $PublishDir = Join-Path $root ('publish\v' + $bom.appVersion) }
if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'pfi1.manifest'))) { Write-Host "[FAIL] 퍼블리시 폴더/매니페스트가 없습니다: $PublishDir" -ForegroundColor Red; exit 2 }
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$manifest = Join-Path $PublishDir 'pfi1.manifest'
$appExe = Join-Path $PublishDir 'PromptFlowStudio.exe'

# ── [SETUP] ──
Assert-That 'SETUP' 'BOM 스키마' ($bom.schema -eq 'pfs-release-bom/1') $bom.schema
Assert-That 'SETUP' 'PE 구조' ($null -ne (Get-PeKind $SetupPath)) ("" + (Get-PeKind $SetupPath))
$setupHash = Get-Sha256 $SetupPath
Assert-That 'SETUP' 'SHA-256 = BOM' ($setupHash -eq $bom.setup.sha256) $setupHash
$vi = (Get-Item -LiteralPath $SetupPath).VersionInfo
# Inno VersionInfo 문자열은 공백/NUL 패딩이 붙을 수 있다 → 정규화 후 비교
$viName = ("" + $vi.ProductName) -replace '[\s\x00]+$', ''
$viVer = ("" + $vi.ProductVersion) -replace '[\s\x00]+$', ''
Assert-That 'SETUP' 'VersionInfo' (($viName -eq 'PromptFlow Studio') -and ($viVer -like ($bom.appVersion + '*'))) ("{0} {1}" -f $viName, $viVer)
# Authenticode: setup.exe + PromptFlowStudio.exe. BOM 이 서명됨(authenticode=true)이라 기록했거나 -RequireAuthenticode 면 미서명은 FAIL.
$bomSigned = ($bom.PSObject.Properties.Name -contains 'authenticode') -and [bool]$bom.authenticode
foreach ($target in @(@('SETUP', $SetupPath), @('SETUP', $appExe))) {
    $auth = Get-AuthenticodeSignature -LiteralPath $target[1]
    $label = 'Authenticode ' + [System.IO.Path]::GetFileName($target[1])
    if ($auth.Status -eq 'Valid') { Add-Result $target[0] $label 'PASS' $auth.SignerCertificate.Subject }
    elseif ($RequireAuthenticode -or $bomSigned -or $auth.Status -ne 'NotSigned') { Add-Result $target[0] $label 'FAIL' ("서명 상태 $($auth.Status)") }
    else { Add-Result $target[0] $label 'WARN' "코드 서명 없음(NotSigned) — 배포 무결성은 pfi1.manifest RSA + SHA256SUMS 로 보장" }
}

# ── [MANIFEST] ──
$lines = [System.IO.File]::ReadAllLines($manifest)
$count = 0
$headerOk = ($lines.Length -ge 3) -and ($lines[0] -match '^PFI1 (\d+)$') -and ([int]$Matches[1] -eq $lines.Length - 2)
if ($headerOk) { $count = $lines.Length - 2 }
Assert-That 'MANIFEST' 'PFI1 집계 헤더' $headerOk ("{0}개 항목" -f $count)
$sigLen = 0
if ($lines.Length -gt 0 -and $lines[-1].StartsWith('SIG ')) { try { $sigLen = [Convert]::FromBase64String($lines[-1].Substring(4)).Length } catch { $sigLen = -1 } }
Assert-That 'MANIFEST' 'RSA-2048 서명 길이' ($sigLen -eq 256) ("{0}B" -f $sigLen)
$mHash = Get-Sha256 $manifest
Assert-That 'MANIFEST' 'SHA-256 = BOM' ($mHash -eq $bom.manifestSha256) $mHash
$v = Invoke-Pfi1Verify $appExe $PublishDir
Assert-That 'MANIFEST' 'RSA 검증(내장 공개키)' ($v.Code -eq 0) ("exit {0} · {1}" -f $v.Code, $v.Message)

# ── [PE] ──
$listed = @()
for ($i = 1; $i -le $count; $i++) { $listed += $lines[$i].Substring(65) }
foreach ($name in @('PromptFlowStudio.exe') + $listed) {
    $kind = Get-PeKind (Join-Path $PublishDir $name)
    Assert-That 'PE' $name ($kind -eq 'PE32+/AMD64') ("" + $kind)
}

# ── [FFMPEG] ──
$ff = Join-Path $PublishDir 'ffmpeg.exe'
if (Test-Path -LiteralPath $ff) {
    Assert-That 'FFMPEG' '서명 대상 등재' ($listed -contains 'ffmpeg.exe') 'pfi1.manifest'
    Assert-That 'FFMPEG' 'BOM 해시' ($bom.ffmpegBundled -and ((Get-Sha256 $ff) -eq $bom.ffmpegSha256)) ("" + $bom.ffmpegSha256)
} elseif ($RequireFfmpeg) { Add-Result 'FFMPEG' '동봉' 'FAIL' 'ffmpeg.exe 미동봉' }
else { Add-Result 'FFMPEG' '동봉' 'WARN' 'ffmpeg.exe 미동봉(-RequireFfmpeg 미지정)' }

# ── [WEBVIEW2] ──
if ($null -ne $bom.webView2Redist) {
    $rp = Join-Path $root ('installer\redist\' + $bom.webView2Redist.name)
    $exists = Test-Path -LiteralPath $rp
    Assert-That 'WEBVIEW2' 'redist 해시 = BOM' ($exists -and ((Get-Sha256 $rp) -eq $bom.webView2Redist.sha256)) $bom.webView2Redist.name
    if ($exists) {
        $wsig = Get-AuthenticodeSignature -LiteralPath $rp
        Assert-That 'WEBVIEW2' 'Microsoft Authenticode' (($wsig.Status -eq 'Valid') -and ($wsig.SignerCertificate.Subject -match 'O=Microsoft Corporation')) ("" + $wsig.Status)
    }
} elseif ($RequireWebView2Redist) { Add-Result 'WEBVIEW2' '동봉' 'FAIL' 'BOM 에 redist 기록 없음' }
else { Add-Result 'WEBVIEW2' '동봉' 'WARN' 'redist 미동봉 — 미설치 PC 는 안내 메시지' }

# ── [CYCLE] 설치/제거 + 사용자 데이터 보존 ──
if ($InstallCycle) {
    $dataRoot = Join-Path $env:LOCALAPPDATA 'PromptFlowStudio'
    $seeded = @()
    foreach ($rel in @('Config\vault.enc', 'engines.json')) {
        $p = Join-Path $dataRoot $rel
        if (-not (Test-Path -LiteralPath $p)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null
            $fixture = New-Object byte[] 512
            $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
            try { $rng.GetBytes($fixture) } finally { $rng.Dispose() }
            [System.IO.File]::WriteAllBytes($p, $fixture)
            $seeded += $p
        }
    }
    $before = Get-DataSnapshot $dataRoot
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('pfs-cycle-' + [guid]::NewGuid().ToString('N'))
    try {
        $inst = Start-Process -FilePath $SetupPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/TASKS=""', ('/DIR="' + $dir + '"')) -Wait -PassThru
        Assert-That 'CYCLE' '사일런트 설치' (($inst.ExitCode -eq 0) -and (Test-Path -LiteralPath (Join-Path $dir 'PromptFlowStudio.exe'))) ("exit {0}" -f $inst.ExitCode)
        if ($inst.ExitCode -eq 0) {
            $iv = Invoke-Pfi1Verify (Join-Path $dir 'PromptFlowStudio.exe') $dir
            Assert-That 'CYCLE' '설치본 RSA 검증' ($iv.Code -eq 0) ("exit {0} · {1}" -f $iv.Code, $iv.Message)
            $uninst = Get-ChildItem -LiteralPath $dir -Filter 'unins*.exe' | Select-Object -First 1
            Assert-That 'CYCLE' '제거 프로그램' ($null -ne $uninst) ("" + $(if ($uninst) { $uninst.Name }))
            if ($uninst) {
                Start-Process -FilePath $uninst.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds(120)
                while ((Test-Path -LiteralPath (Join-Path $dir 'PromptFlowStudio.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
                Assert-That 'CYCLE' '사일런트 제거' (-not (Test-Path -LiteralPath (Join-Path $dir 'PromptFlowStudio.exe'))) $dir
            }
        }
        $after = Get-DataSnapshot $dataRoot
        $lost = @($before.Keys | Where-Object { -not $after.ContainsKey($_) -or $after[$_] -ne $before[$_] })
        Assert-That 'CYCLE' '사용자 데이터 보존' (($lost.Count -eq 0) -and $after.ContainsKey('Config\vault.enc')) ("{0}개 파일 · 변경/소실 {1}" -f $before.Count, $lost.Count)
    }
    finally {
        foreach ($p in $seeded) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ── 보고서 ──
$fails = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$warns = @($results | Where-Object { $_.status -eq 'WARN' }).Count
if (-not $ReportPath) { $ReportPath = $SetupPath + '.verify.json' }
$report = [ordered]@{
    schema     = 'pfs-release-verify/1'
    appVersion = $bom.appVersion
    setup      = [System.IO.Path]::GetFileName($SetupPath)
    verdict    = $(if ($fails -eq 0) { 'GOLDEN_MASTER' } else { 'REJECTED' })
    pass       = @($results | Where-Object { $_.status -eq 'PASS' }).Count
    warn       = $warns
    fail       = $fails
    results    = $results
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("[VERIFY] {0} — PASS {1} · WARN {2} · FAIL {3} → {4}" -f $report.verdict, $report.pass, $warns, $fails, $ReportPath) -ForegroundColor $(if ($fails -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fails -eq 0) { 0 } else { 1 })
