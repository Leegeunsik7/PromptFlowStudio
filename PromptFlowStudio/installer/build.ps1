# ============================================================================
#  PromptFlow Studio — 릴리스 빌드 & 인스톨러 원클릭 생성
#  실행: 우클릭 > PowerShell 실행  또는  pwsh installer\build.ps1
#  전제: .NET 8 SDK + Inno Setup 6(ISCC.exe), 프로젝트 루트에 ffmpeg.exe
#  ★ 번들 CLI 엔진: 프로젝트 루트 Tools\{engine}\ (Real-ESRGAN/Real-CUGAN/Kokoro 등)에 실행 파일을 두면
#     csproj CopyToolsToPublish 타깃이 publish 출력의 Tools\ 로 자동 복사 → .iss recursesubdirs 로 설치본에 포함.
#     런타임에 CliPathResolver 가 {app}\Tools\{engine}\ 를 자동 탐색(Zero-Config). Tools\ 가 없어도 앱은 Graceful.
# ============================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # 프로젝트 폴더(installer 의 상위)
$proj = Join-Path $root 'PromptFlowStudio.csproj'

Write-Host '== [1/2] dotnet publish (self-contained · single file) ==' -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }

# 번들 CLI 엔진 포함 여부 안내(진단용 · 없어도 계속).
$toolsDir = Join-Path $root 'Tools'
if (Test-Path $toolsDir) {
  $n = (Get-ChildItem $toolsDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
  Write-Host "   Tools\ 번들 CLI 엔진 $n개 파일 포함(Zero-Config 자동 탐색)" -ForegroundColor DarkGray
} else {
  Write-Host '   Tools\ 번들 없음 → 로컬 엔진 기능은 배포본에서 Graceful 비활성(오류 아님)' -ForegroundColor DarkGray
}

# Inno Setup 컴파일러(ISCC.exe) 탐색: PATH → 기본 설치 경로
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                   "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $p) { $iscc = $p; break }
  }
}
if (-not $iscc) { throw 'ISCC.exe(Inno Setup 6)를 찾을 수 없습니다 → https://jrsoftware.org/isdl.php' }

Write-Host "== [2/2] Inno Setup 컴파일: $iscc ==" -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot 'PromptFlowStudio.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC 실패' }

Write-Host '✅ 완료 → installer\Output\PromptFlow Studio Setup.exe' -ForegroundColor Green
