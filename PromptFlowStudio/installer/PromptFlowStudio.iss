; ============================================================================
;  PromptFlow Studio — Inno Setup 스크립트
;  빌드 전제: 아래 PublishDir 에 `dotnet publish` 결과물(단일 exe + ffmpeg.exe + wwwroot + appsettings.json)이 있어야 함.
;  컴파일: Inno Setup Compiler(ISCC.exe)로 이 .iss 를 열어 [Build] → Output\ 에 "PromptFlow Studio Setup.exe" 생성.
; ============================================================================

#define MyAppName        "PromptFlow Studio"
; CI 에서 ISCC /DMyAppVersion=1.1.0 로 재정의 가능(없으면 기본값).
#ifndef MyAppVersion
  #define MyAppVersion   "1.1.0"
#endif
#define MyAppPublisher   "ANG Technology"
#define MyAppExeName     "PromptFlowStudio.exe"
; 퍼블리시 출력 폴더(이 .iss 기준 상대경로). RID/구성이 다르면 여기만 수정.
#define PublishDir       "..\bin\Release\net8.0-windows\win-x64\publish"
; (선택) WebView2 부트스트래퍼를 동봉하려면 installer\redist\ 에 내려받아 두세요.
#define WebView2Setup    "redist\MicrosoftEdgeWebview2Setup.exe"

[Setup]
; ⚠ AppId 는 업그레이드 추적 키 — 한 번 정하면 버전이 올라가도 절대 바꾸지 마세요(바꾸면 별도 앱으로 설치됨).
AppId={{8F3B9C2A-1D4E-4A6B-9C7F-2E5A8B1D3C6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=Output
OutputBaseFilename=PromptFlow Studio Setup
SetupIconFile=..\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Program Files 에 쓰려면 관리자 권한 필요.
PrivilegesRequired=admin
; 64비트 전용(WebView2/ffmpeg x64). 구버전 Inno(6.2 이하)는 x64compatible → x64 로 바꾸세요.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 자동 업데이트: 실행 중인 앱이 파일을 잠그면 Restart Manager 로 닫고(설치 후 재시작).
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; 퍼블리시 산출물 전체(단일 exe + ffmpeg.exe + wwwroot\ + appsettings.json + Tools\ 번들 CLI 엔진)를 통째로 설치.
;   ★ recursesubdirs 로 Tools\{engine}\(Real-ESRGAN/Real-CUGAN/Kokoro 등)도 자동 포함 → 런타임 CliPathResolver 가 {app}\Tools\ 자동 탐색.
;      (csproj CopyToolsToPublish 타깃이 publish 출력에 Tools\ 를 넣어 두면 아래 와일드카드가 그대로 편입한다. 없어도 앱은 Graceful.)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; (선택) WebView2 런타임 부트스트래퍼 — redist 에 파일이 있을 때만 포함.
#if FileExists(AddBackslash(SourcePath) + WebView2Setup)
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWebView2Installed
#endif

[Icons]
Name: "{group}\{#MyAppName}";                       Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";                 Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; (선택) WebView2 미설치 시 조용히 설치(부트스트래퍼를 동봉한 경우에만 동작).
#if FileExists(AddBackslash(SourcePath) + WebView2Setup)
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft Edge WebView2 런타임 설치 중..."; Check: not IsWebView2Installed
#endif
; 설치 마지막에 앱 실행 — 무음(/SILENT) 업데이트에서도 재시작되도록 skipifsilent 미사용.
;   runasoriginaluser: 관리자로 승격된 설치 컨텍스트가 아니라 일반 사용자 권한으로 앱 실행.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall runasoriginaluser

[Code]
{ WebView2 Evergreen 런타임 설치 여부(잘 알려진 클라이언트 GUID). 시스템/사용자 설치 모두 확인. }
function IsWebView2Installed(): Boolean;
var
  v: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

{ 설치 시작 시 WebView2 부재를 부드럽게 안내(설치 자체는 계속 진행). }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWebView2Installed() then
    MsgBox('이 프로그램은 Microsoft Edge WebView2 런타임이 필요합니다.' #13#10#13#10
      + '설치되어 있지 않은 것으로 보입니다. 대부분의 Windows 11 및 최신 Windows 10 에는 기본 포함되어 있습니다.' #13#10
      + '앱 실행 시 화면이 비어 있으면 아래에서 런타임을 설치하세요:' #13#10
      + 'https://developer.microsoft.com/microsoft-edge/webview2/',
      mbInformation, MB_OK);
end;
