<#
.SYNOPSIS
  PromptFlow Studio v16.2.0 — WebView2 런타임 재배포 파일을 installer\redist\ 로 확보(오프라인 번들링).

.DESCRIPTION
  -Mode Bootstrapper(기본): Evergreen Bootstrapper(MicrosoftEdgeWebview2Setup.exe · ~2MB · 설치 시 온라인)
  -Mode Standalone       : Evergreen Standalone x64(MicrosoftEdgeWebView2RuntimeInstallerX64.exe · 완전 오프라인)
  다운로드는 Microsoft 공식 fwlink 만 사용하고, 지수 백오프 3회 재시도 후 Authenticode 서명(Valid · 발급 대상 Microsoft Corporation)을
  검증한다 — 서명 불일치 파일은 즉시 삭제. 이미 유효한 파일이 있으면 재다운로드하지 않는다(-Force 로 강제).
  promptflow_studio.iss 가 redist 를 자동 감지(Standalone 우선)해 PrepareToInstall 에서 /silent /install 로 실행한다.

  종료 코드: 0=성공 · 20=다운로드 실패 · 21=Authenticode 검증 실패
#>
[CmdletBinding()]
param(
    [ValidateSet('Bootstrapper', 'Standalone')][string]$Mode = 'Bootstrapper',
    [string]$RedistDir,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $RedistDir) { $RedistDir = Join-Path $root 'installer\redist' }

function Write-Step([string]$msg) { Write-Host "[WebView2] $msg" -ForegroundColor Cyan }
function Fail([string]$msg, [int]$code) { Write-Host "[WebView2] 실패: $msg" -ForegroundColor Red; exit $code }

$targets = @{
    Bootstrapper = @{ Name = 'MicrosoftEdgeWebview2Setup.exe'; Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' }
    Standalone   = @{ Name = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'; Url = 'https://go.microsoft.com/fwlink/?linkid=2124701' }
}
$t = $targets[$Mode]
$dst = Join-Path $RedistDir $t.Name

function Test-MicrosoftSigned([string]$path) {
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    return ($sig.Status -eq 'Valid') -and $sig.SignerCertificate -and ($sig.SignerCertificate.Subject -match 'O=Microsoft Corporation')
}

New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null
if (-not $Force -and (Test-Path -LiteralPath $dst) -and (Test-MicrosoftSigned $dst)) {
    Write-Step "이미 확보됨(서명 유효): $dst"
    exit 0
}

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$tmp = $dst + '.download'
$ok = $false
for ($i = 0; $i -lt 3 -and -not $ok; $i++) {
    if ($i -gt 0) { Start-Sleep -Milliseconds (500 * [math]::Pow(2, $i - 1)) }
    try { Invoke-WebRequest -Uri $t.Url -OutFile $tmp -UseBasicParsing -TimeoutSec 600; $ok = $true }
    catch { Write-Host "[WebView2] 다운로드 재시도($($i + 1)/3): $($_.Exception.Message)" -ForegroundColor Yellow }
}
if (-not $ok) { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }; Fail "다운로드 실패: $($t.Url)" 20 }

if (-not (Test-MicrosoftSigned $tmp)) { Remove-Item -LiteralPath $tmp -Force; Fail 'Authenticode 서명 검증 실패(Microsoft Corporation 서명 아님)' 21 }
Move-Item -LiteralPath $tmp -Destination $dst -Force
$hash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
Write-Step "확보 완료: $dst (SHA-256 $hash)"
exit 0
