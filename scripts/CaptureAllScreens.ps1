<#
.SYNOPSIS
  9대 메뉴 자동 투어 + DPI 보정 스크린샷 캡처 (artifacts/screenshots/01_workspace.png ~ 09_gallery.png).

.DESCRIPTION
  - 프로세스를 Per-Monitor V2 DPI 인식으로 선언(150% 배율 잘림 방지).
  - WebView2 원격 디버깅(CDP) 포트를 열어 앱을 기동 → Emulation 으로 1920x1080 @1x 뷰포트 고정 → DPI 배율과 무관한 전체 화면 캡처.
  - 사이드바 NavLink 클릭으로 Blazor 라우팅(NavigationManager) 순회, 화면별 사전 액션(예: 영감 모달 오픈) 후 Page.captureScreenshot.
  - 로그인 게이트는 빌드 출력의 appsettings.json 을 임시 패치(Auth:RequireLogin=false)하고 종료 시 원복.
  - 외부 모듈 0 (BCL ClientWebSocket 사용).

.EXAMPLE
  pwsh -File scripts/CaptureAllScreens.ps1
  pwsh -File scripts/CaptureAllScreens.ps1 -NoBuild -Width 1920 -Height 1080
#>
[CmdletBinding()]
param(
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Port = 9333,
    [int]$SettleMs = 1800,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'PromptFlowStudio/PromptFlowStudio.csproj'
$outDir = Join-Path $root 'artifacts/screenshots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ── 1. DPI 인식 선언(Per-Monitor V2 → 실패 시 SetProcessDpiAwareness(2) 폴백) ──
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PfsDpi {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    public static string Enable() {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return "PerMonitorV2"; } catch { }
        try { if (SetProcessDpiAwareness(2) == 0) return "PerMonitor"; } catch { }
        return "AlreadySet";
    }
}
'@
Write-Host "[dpi] $([PfsDpi]::Enable())"

# ── 2. Release 빌드 ──
if (-not $NoBuild) {
    & dotnet build $proj -c Release -warnaserror -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패 ($LASTEXITCODE)" }
}
$exe = Get-ChildItem -Path (Join-Path $root 'PromptFlowStudio/bin/Release') -Recurse -Filter 'PromptFlowStudio.exe' |
    Where-Object { $_.FullName -notmatch '\\publish\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'PromptFlowStudio.exe 를 찾을 수 없습니다.' }
$binDir = $exe.DirectoryName

# ── 3. 로그인 게이트 임시 해제(종료 시 원복) ──
$settings = Join-Path $binDir 'appsettings.json'
$settingsBackup = $null
if (Test-Path $settings) {
    $settingsBackup = [IO.File]::ReadAllBytes($settings)
    $txt = [Text.Encoding]::UTF8.GetString($settingsBackup)
    $txt = [regex]::Replace($txt, '"RequireLogin"\s*:\s*"true"', '"RequireLogin": "false"')
    [IO.File]::WriteAllText($settings, $txt, (New-Object Text.UTF8Encoding($false)))
}

# ── CDP 헬퍼 ──
$script:ws = $null
$script:msgId = 0
function Invoke-Cdp([string]$method, [hashtable]$params = @{}) {
    $script:msgId++
    $id = $script:msgId
    $json = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $script:ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $buf = New-Object byte[] 1048576
    while ($true) {
        $ms = New-Object IO.MemoryStream
        do {
            $r = $script:ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $ms.Write($buf, 0, $r.Count)
        } while (-not $r.EndOfMessage)
        $msg = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($msg.id -eq $id) {
            if ($msg.error) { throw "CDP $method 실패: $($msg.error.message)" }
            return $msg.result
        }
    }
}
function Invoke-Js([string]$expr) {
    $r = Invoke-Cdp 'Runtime.evaluate' @{ expression = $expr; awaitPromise = $true; returnByValue = $true }
    return $r.result.value
}

# 9대 라우트 — Pre: 캡처 전 실행할 JS(모달 오픈 등)
$tour = @(
    @{ Name = '01_workspace';    Route = 'workspace';       Pre = "[...document.querySelectorAll('button.ws-insp-open')].forEach(b=>b.click())" },
    @{ Name = '02_storyboard';   Route = 'storyboard';      Pre = $null },
    @{ Name = '03_batch';        Route = 'batch';           Pre = $null },
    @{ Name = '04_render_queue'; Route = 'queue';           Pre = $null },
    @{ Name = '05_live_studio';  Route = 'live-studio';     Pre = $null },
    @{ Name = '06_commercial';   Route = 'brand-studio';    Pre = $null },
    @{ Name = '07_shorts';       Route = 'ai-cutdown';      Pre = $null },
    @{ Name = '08_camera';       Route = 'wireless-camera'; Pre = $null },
    @{ Name = '09_gallery';      Route = 'gallery';         Pre = $null }
)

$proc = $null
try {
    # ── 4. 원격 디버깅 포트로 앱 기동 ──
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
    $proc = Start-Process -FilePath $exe.FullName -WorkingDirectory $binDir -PassThru
    Write-Host "[app] pid=$($proc.Id)"

    $target = $null
    for ($i = 0; $i -lt 60 -and -not $target; $i++) {
        Start-Sleep -Milliseconds 1000
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json" -TimeoutSec 2
            $target = $targets | Where-Object { $_.type -eq 'page' -and $_.url -like 'https://0.0.0.0/*' } | Select-Object -First 1
        } catch { }
    }
    if (-not $target) { throw "WebView2 CDP 타깃을 찾지 못했습니다(포트 $Port)." }

    $script:ws = New-Object Net.WebSockets.ClientWebSocket
    $script:ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    Invoke-Cdp 'Page.enable' | Out-Null

    # ── 5. 1920x1080 @1x 뷰포트 고정(OS 배율 무관) ──
    Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = $Width; height = $Height; deviceScaleFactor = 1; mobile = $false } | Out-Null

    # Blazor 셸(사이드바) 로드 대기
    for ($i = 0; $i -lt 40; $i++) {
        if (Invoke-Js "!!document.querySelector('a.nav-item[href=""workspace""]')") { break }
        Start-Sleep -Milliseconds 500
    }

    # ── 6. 순회 + 캡처 ──
    foreach ($t in $tour) {
        $clicked = Invoke-Js "(()=>{const a=document.querySelector('a.nav-item[href=""$($t.Route)""]');if(a){a.click();return true}return false})()"
        if (-not $clicked) { Invoke-Js "Blazor.navigateTo('/$($t.Route)')" | Out-Null }
        Start-Sleep -Milliseconds $SettleMs
        if ($t.Pre) { Invoke-Js $t.Pre | Out-Null; Start-Sleep -Milliseconds 700 }
        $active = Invoke-Js "(document.querySelector('a.nav-item.active')||{}).getAttribute?.('href')??''"
        $shot = Invoke-Cdp 'Page.captureScreenshot' @{ format = 'png'; captureBeyondViewport = $false }
        $file = Join-Path $outDir "$($t.Name).png"
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($shot.data))
        Write-Host ("[shot] {0,-16} route=/{1,-16} active={2}" -f $t.Name, $t.Route, $active)
    }
}
finally {
    if ($script:ws) { try { $script:ws.Dispose() } catch { } }
    if ($proc -and -not $proc.HasExited) { try { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(5000)) { $proc.Kill() } } catch { } }
    Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    if ($settingsBackup) { [IO.File]::WriteAllBytes($settings, $settingsBackup) }
}
Write-Host "[done] $outDir"
