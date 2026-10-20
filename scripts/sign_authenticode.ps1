<#
.SYNOPSIS
  PromptFlow Studio v16.4.0 — Windows Authenticode 코드 서명(선택 단계) · SignTool SHA-256 + RFC3161 타임스탬프.

.DESCRIPTION
  인증서 공급 방식(우선순위):
    1) -CertThumbprint : 인증서 저장소(CurrentUser\My, 없으면 LocalMachine\My)의 인증서 — EV/HSM 토큰(USB·Cloud KSP) 용
    2) -PfxPath        : PFX 파일. 암호는 환경변수(-PfxPasswordEnv, 기본 PROMPTFLOW_PFX_PASSWORD)로만 받는다.
                         SignTool /p(명령줄 노출) 대신 CurrentUser\My 에 임시 등록 → /sha1 로 서명 → finally 에서 제거.
  인증서가 없으면 서명을 건너뛰고 0 반환(선택 단계) — -Require 지정 시 실패.
  서명 후 `signtool verify /pa` 로 정책 검증(-AllowUntrustedRoot 는 테스트 인증서 전용 · 서명 존재/무결성만 확인).

  종료 코드: 0=서명 완료 또는 건너뜀 · 20=인증서 미제공(-Require) · 21=PFX/인증서 오류 · 22=SignTool 없음 · 23=서명 실패 · 24=검증 실패 · 25=대상 파일 없음
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$PfxPath,
    [string]$PfxPasswordEnv = 'PROMPTFLOW_PFX_PASSWORD',
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Description = 'PromptFlow Studio',
    [string]$SignToolPath,
    [switch]$Require,
    [switch]$AllowUntrustedRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "[Authenticode] $msg" -ForegroundColor Cyan }
function Fail([string]$msg, [int]$code) { Write-Host "[Authenticode] 실패: $msg" -ForegroundColor Red; exit $code }

# ── 0) 인증서 공급 여부 — 없으면 선택 단계로 건너뜀 ──
if (-not $CertThumbprint -and -not $PfxPath) {
    if ($Require) { Fail '인증서 미제공(-CertThumbprint 또는 -PfxPath) — -Require 지정됨' 20 }
    Write-Step '인증서 미제공 → 코드 서명 건너뜀(선택 단계)'
    exit 0
}
foreach ($f in $Files) { if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Fail "서명 대상 파일이 없습니다: $f" 25 } }

# ── 1) SignTool 탐색: -SignToolPath → Windows SDK(최신 x64) → PATH ──
$signtool = $null
if ($SignToolPath -and (Test-Path -LiteralPath $SignToolPath)) { $signtool = $SignToolPath }
if (-not $signtool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $signtool = Get-ChildItem -LiteralPath $kits -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $signtool) { $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue; if ($cmd) { $signtool = $cmd.Source } }
if (-not $signtool) { Fail 'signtool.exe 를 찾을 수 없습니다(Windows SDK 설치 또는 -SignToolPath).' 22 }

# ── 2) 인증서 해석 ──
$imported = $null
$store = $null
$cert = $null
try {
    if ($CertThumbprint) {
        $tp = ($CertThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $tp } | Select-Object -First 1
        if (-not $cert) { Fail "저장소에 인증서가 없습니다: $tp" 21 }
    } else {
        if (-not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) { Fail "PFX 파일이 없습니다: $PfxPath" 21 }
        $pfxPass = [Environment]::GetEnvironmentVariable($PfxPasswordEnv)
        # PersistKeySet 미지정 — 키 컨테이너는 $cert 수명 동안만 존재(Dispose 시 삭제)
        $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
        try { $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2((Resolve-Path -LiteralPath $PfxPath).Path, $pfxPass, $flags) }
        catch { Fail "PFX 를 열 수 없습니다(암호: 환경변수 $PfxPasswordEnv) — $($_.Exception.Message)" 21 }
        finally { $pfxPass = $null }
        if (-not $cert.HasPrivateKey) { Fail 'PFX 에 개인키가 없습니다.' 21 }
        $tp = $cert.Thumbprint
    }

    # 코드 서명 EKU(1.3.6.1.5.5.7.3.3) · 유효기간 확인 — 저장소 등록 전에 거부(부적격 인증서는 저장소를 건드리지 않음)
    $eku = @($cert.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if ($eku.Count -gt 0 -and -not ($eku -contains '1.3.6.1.5.5.7.3.3')) { Fail '코드 서명(EKU 1.3.6.1.5.5.7.3.3) 인증서가 아닙니다.' 21 }
    $now = Get-Date
    if ($cert.NotAfter -lt $now -or $cert.NotBefore -gt $now) { Fail "인증서 유효기간 밖: $($cert.NotBefore) ~ $($cert.NotAfter)" 21 }

    if ($PfxPath) {
        # SignTool /p(명령줄 암호 노출) 회피 — CurrentUser\My 임시 등록 후 /sha1 로 서명, finally 에서 제거
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
        $store.Open('ReadWrite')
        if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $tp })) { $store.Add($cert); $imported = $cert }
    }
    Write-Step ("인증서: {0} ({1})" -f $cert.Subject, $tp)

    # ── 3) 서명 + 검증(타임스탬프 서버 일시 장애 대비 지수 백오프 3회) ──
    foreach ($f in $Files) {
        $ok = $false
        for ($i = 0; $i -lt 3 -and -not $ok; $i++) {
            if ($i -gt 0) { Start-Sleep -Milliseconds (1000 * [math]::Pow(2, $i - 1)) }
            & $signtool sign /sha1 $tp /s My /fd SHA256 /tr $TimestampUrl /td SHA256 /d $Description /q $f
            $ok = ($LASTEXITCODE -eq 0)
        }
        if (-not $ok) { Fail "서명 실패: $f" 23 }

        if ($AllowUntrustedRoot) {
            $sig = Get-AuthenticodeSignature -LiteralPath $f
            if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $tp -or $sig.Status -eq 'HashMismatch' -or $sig.Status -eq 'NotSigned') { Fail "서명 검증 실패: $f ($($sig.Status))" 24 }
        } else {
            & $signtool verify /pa /q $f
            if ($LASTEXITCODE -ne 0) { Fail "서명 검증 실패(signtool verify /pa): $f" 24 }
        }
        Write-Step "서명 완료: $f"
    }
}
finally {
    if ($imported -and $store) { try { $store.Remove($imported) } catch { } }
    if ($store) { $store.Close() }
    if ($PfxPath -and $cert) { $cert.Dispose() }
}
exit 0
