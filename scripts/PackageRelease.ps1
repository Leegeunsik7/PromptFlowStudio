<#
.SYNOPSIS
  PromptFlow Studio v19.0.0 — 원스톱 배포 패키저(publish → 로컬 키셋 주입 → ffmpeg 동봉 → ZIP).

.DESCRIPTION
  1) dotnet publish PromptFlowStudio.csproj -c Release -o dist\PromptFlowStudio_v{Version}
  2) 키 주입: 배포 폴더의 appsettings.json(Git 추적본은 빈 키) 에만 병합 — 저장소 파일은 절대 수정하지 않음.
       우선순위: 환경변수 → -SecretsFile(JSON · .gitignore 대상 로컬 파일)
         Wan:ApiKey      ← PFS_DASHSCOPE_API_KEY | DASHSCOPE_API_KEY | secrets.DashScopeApiKey
         Gemini:ApiKey   ← PFS_GEMINI_API_KEY | GEMINI_API_KEY      | secrets.GeminiApiKey
         ComfyUI:BaseUrl ← PFS_COMFY_URL                             | secrets.ComfyUiUrl
       ※ PC 머신바운드(DPAPI/AES-GCM) 저장 키는 다른 PC 에서 복호화되지 않으므로 대상 외 — 위 경로로 명시 공급.
       키 값은 로그에 출력하지 않는다(설정 여부만).
  3) tools\ffmpeg\bin\ffmpeg.exe(+ffprobe.exe) 가 있으면 배포 폴더 tools\ffmpeg\bin 으로 복사.
  4) 필수 폴더(_renders · _uploads · _gallery · tools\ffmpeg\bin · presets) 생성 후 dist\PromptFlowStudio_v{Version}_ReadyToRun.zip 압축.
  5) .gitignore 에 dist/ 가 없으면 중단(시크릿 포함 산출물 커밋 차단).

  종료 코드: 0=성공 · 20=dist/ 미등록 · 21=publish 실패 · 22=appsettings.json 없음 · 23=키 주입 실패 · 24=압축 실패
#>
[CmdletBinding()]
param(
    [string]$Version = '19.0.0',
    [string]$SecretsFile,
    [switch]$SkipPublish,
    [switch]$AllowEmptyKeys
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Write-Step([string]$msg) { Write-Host "[Package] $msg" -ForegroundColor Cyan }
function Fail([string]$msg, [int]$code) { Write-Host "[Package] 실패: $msg" -ForegroundColor Red; exit $code }

function First-NonEmpty([object[]]$values) {
    foreach ($v in $values) { if ($v -is [string] -and $v.Trim().Length -gt 0) { return $v.Trim() } }
    return $null
}

function Set-JsonValue($obj, [string]$section, [string]$name, [string]$value) {
    if (-not ($obj.PSObject.Properties.Name -contains $section)) {
        $obj | Add-Member -NotePropertyName $section -NotePropertyValue ([pscustomobject]@{})
    }
    $sec = $obj.$section
    if ($sec.PSObject.Properties.Name -contains $name) { $sec.$name = $value }
    else { $sec | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

# ── 0) 저장소 보안 가드 ─────────────────────────────────────────────
$gitignore = Join-Path $root '.gitignore'
if (-not (Test-Path $gitignore) -or -not (Select-String -Path $gitignore -Pattern '^\s*/?dist/?\s*$' -Quiet)) {
    Fail '.gitignore 에 dist/ 미등록 — 시크릿 포함 산출물 커밋 위험' 20
}

$dist = Join-Path $root 'dist'
$out = Join-Path $dist "PromptFlowStudio_v$Version"
$zip = Join-Path $dist "PromptFlowStudio_v${Version}_ReadyToRun.zip"

# ── 1) publish ──────────────────────────────────────────────────────
if (-not $SkipPublish) {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Step "dotnet publish → $out"
    & dotnet publish (Join-Path $root 'PromptFlowStudio\PromptFlowStudio.csproj') -c Release -o $out
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 종료 코드 $LASTEXITCODE" 21 }
}

# ── 2) 키셋 주입(배포 사본에만) ────────────────────────────────────
$settingsPath = Join-Path $out 'appsettings.json'
if (-not (Test-Path $settingsPath)) { Fail "배포 폴더에 appsettings.json 없음: $settingsPath" 22 }

$secrets = $null
if ($SecretsFile) {
    if (-not (Test-Path $SecretsFile)) { Fail "SecretsFile 없음: $SecretsFile" 23 }
    $secrets = Get-Content $SecretsFile -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Secret([string]$name) {
    if ($null -ne $secrets -and ($secrets.PSObject.Properties.Name -contains $name)) { return [string]$secrets.$name }
    return $null
}

$dashscope = First-NonEmpty @($env:PFS_DASHSCOPE_API_KEY, $env:DASHSCOPE_API_KEY, (Secret 'DashScopeApiKey'))
$gemini    = First-NonEmpty @($env:PFS_GEMINI_API_KEY, $env:GEMINI_API_KEY, (Secret 'GeminiApiKey'))
$comfy     = First-NonEmpty @($env:PFS_COMFY_URL, (Secret 'ComfyUiUrl'))

if (-not $AllowEmptyKeys -and -not $dashscope -and -not $gemini) {
    Fail 'DashScope/Gemini 키 모두 미공급(환경변수 또는 -SecretsFile). 키 없는 패키지는 -AllowEmptyKeys 로 명시.' 23
}

try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($dashscope) { Set-JsonValue $json 'Wan' 'ApiKey' $dashscope }
    if ($gemini)    { Set-JsonValue $json 'Gemini' 'ApiKey' $gemini }
    if ($comfy)     { Set-JsonValue $json 'ComfyUI' 'BaseUrl' $comfy.TrimEnd('/') }
    $tmp = "$settingsPath.tmp"
    $json | ConvertTo-Json -Depth 32 | Set-Content -Path $tmp -Encoding UTF8
    Move-Item -Path $tmp -Destination $settingsPath -Force
}
catch { Fail "appsettings.json 병합 실패: $($_.Exception.Message)" 23 }

$state = { param($v) if ($v) { '설정됨' } else { '미설정' } }
Write-Step ("키 주입 — DashScope: {0} · Gemini: {1} · ComfyUI URL: {2}" -f (& $state $dashscope), (& $state $gemini), (& $state $comfy))

# ── 3) ffmpeg 동봉 · 필수 폴더 ─────────────────────────────────────
$binDst = Join-Path $out 'tools\ffmpeg\bin'
foreach ($d in @('_renders', '_uploads', '_gallery', 'tools\ffmpeg\bin', 'presets')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $out $d) | Out-Null
}
$binSrc = Join-Path $root 'tools\ffmpeg\bin'
$copied = 0
foreach ($exe in @('ffmpeg.exe', 'ffprobe.exe')) {
    $src = Join-Path $binSrc $exe
    if (Test-Path $src) { Copy-Item $src (Join-Path $binDst $exe) -Force; $copied++ }
}
if ($copied -gt 0) { Write-Step "ffmpeg 동봉 $copied 개 → tools\ffmpeg\bin" } else { Write-Step 'tools\ffmpeg\bin\ffmpeg.exe 없음 — 동봉 생략(대상 PC PATH 탐색)' }

# ── 4) ZIP ──────────────────────────────────────────────────────────
try {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}
catch { Fail "압축 실패: $($_.Exception.Message)" 24 }

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Step "완료 → $zip ($sizeMb MB)"
Write-Host '[Package] ⚠ ZIP 에 API 키가 포함됨 — 신뢰 채널로만 배포.' -ForegroundColor Yellow
exit 0
