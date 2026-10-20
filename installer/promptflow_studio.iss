; ─────────────────────────────────────────────────────────────────────────────
; PromptFlow Studio v16.4.0 — Inno Setup 6 프로덕션 인스톨러 (win-x64 · 사용자 단위 설치)
;   ▸ 입력: build_titan_release.ps1 이 만든 서명 완료 퍼블리시 폴더(PublishDir) — pfi1.manifest 필수
;   ▸ 서명 대상(PromptFlowStudio.dll · PromptFlowStudio.Shared.dll · ffmpeg.exe)은 바이트 그대로 복사(재서명·변형 금지)
;   ▸ %LocalAppData%\PromptFlowStudio(로그·볼트·engines.json) 생성 — 상속 ACL(현재 사용자 전용) 최소 권한, 제거 시 사용자 데이터 보존
;   ▸ WebView2(BlazorWebView) Evergreen 런타임 검증 — 미설치 시 installer\redist\ 자동 감지:
;       ① Standalone 오프라인(MicrosoftEdgeWebView2RuntimeInstallerX64.exe) → ② Evergreen Bootstrapper(MicrosoftEdgeWebview2Setup.exe)
;       PrepareToInstall 단계에서 /silent /install 사일런트 실행 후 레지스트리 재검증. redist 부재 시 안내 메시지.
;   컴파일: ISCC.exe /DPublishDir=<publish> /DAppVersion=16.4.0 [/DRequireWebView2Redist] [/DAllowMissingFfmpeg] installer\promptflow_studio.iss
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "16.4.0"
#endif
#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\publish\v16.4.0"
#endif
#define AppName "PromptFlow Studio"
#define AppExe "PromptFlowStudio.exe"
#define Manifest "pfi1.manifest"
#define WebView2Guid "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
#define WebView2Bootstrapper AddBackslash(SourcePath) + "redist\MicrosoftEdgeWebview2Setup.exe"
#define WebView2Standalone AddBackslash(SourcePath) + "redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
; redist 자동 감지 — 오프라인 Standalone 우선, 없으면 Bootstrapper. WebView2Redist 미정의 = 미동봉.
#if FileExists(WebView2Standalone)
  #define WebView2Redist WebView2Standalone
  #define WebView2RedistName "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#elif FileExists(WebView2Bootstrapper)
  #define WebView2Redist WebView2Bootstrapper
  #define WebView2RedistName "MicrosoftEdgeWebview2Setup.exe"
#endif
#ifdef RequireWebView2Redist
  #ifndef WebView2Redist
    #error "installer\redist\ 에 WebView2 런타임(Standalone/Bootstrapper)이 없습니다. scripts\fetch_webview2.ps1 을 실행하세요."
  #endif
#endif

; ── 컴파일 타임 릴리스 게이트: 서명되지 않은 산출물은 패키징 거부 ──
#if !FileExists(AddBackslash(PublishDir) + AppExe)
  #error "퍼블리시 산출물에 PromptFlowStudio.exe 가 없습니다. build_titan_release.ps1 을 먼저 실행하세요."
#endif
#if !FileExists(AddBackslash(PublishDir) + Manifest)
  #error "pfi1.manifest(RSA-2048 서명) 가 없습니다. 서명되지 않은 빌드는 패키징할 수 없습니다."
#endif
#ifndef AllowMissingFfmpeg
  #if !FileExists(AddBackslash(PublishDir) + "ffmpeg.exe")
    #error "ffmpeg.exe 미동봉 — scripts\bundle_ffmpeg.ps1 로 동봉하거나 /DAllowMissingFfmpeg 를 지정하세요."
  #endif
#endif

[Setup]
AppId={{6E8DCAE8-3450-478F-BD1D-6A379B8CBD3F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ANG Technology
VersionInfoVersion={#AppVersion}
VersionInfoCompany=ANG Technology
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#SourcePath}Output
OutputBaseFilename=PromptFlowStudio-{#AppVersion}-win-x64-setup
SetupIconFile={#SourcePath}..\PromptFlowStudio\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; 사용자 데이터 루트 — 로그(logs\shell.log)·키 볼트(Config\vault.enc)·engines.json. 앱 업데이트/제거에도 보존.
; v16.4.0 최소 권한: Permissions 미지정 → 프로필 상속 ACL(SYSTEM · Administrators · 현재 사용자)만 유지.
;   (v16.1~16.3 의 users-modify 는 BUILTIN\Users 전체에 수정 권한을 부여하던 과도 권한 → 폐기 · 업그레이드 시 RevokeLegacyUsersAce 로 회수)
Name: "{localappdata}\PromptFlowStudio"; Flags: uninsneveruninstall
Name: "{localappdata}\PromptFlowStudio\Config"; Flags: uninsneveruninstall
Name: "{localappdata}\PromptFlowStudio\logs"; Flags: uninsneveruninstall

[Files]
; 서명 매니페스트 — 무결성 쉴드(AppIntegrityGuard.VerifyInstalled)가 기동 시 검증
Source: "{#PublishDir}\{#Manifest}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "{#Manifest},{#AppExe},ffmpeg.exe,*.pdb,*.tmp"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WebView2Redist
; dontcopy — PrepareToInstall 에서 필요할 때만 ExtractTemporaryFile 로 {tmp} 에 풀어 사일런트 실행
Source: "{#WebView2Redist}"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 설치 폴더의 런타임 부산물만 정리(사용자 데이터 %LocalAppData%\PromptFlowStudio 는 보존)
Type: files; Name: "{app}\{#Manifest}.tmp"

[Code]
// WebView2 Evergreen 런타임: EdgeUpdate Clients\{GUID} 의 pv 가 비어 있지 않고 0.0.0.0 이 아니면 설치됨(머신/사용자 단위 모두 확인).
function ReadWebView2Version(RootKey: Integer; const SubKey: String): String;
begin
  if not RegQueryStringValue(RootKey, SubKey, 'pv', Result) then Result := '';
end;

function IsWebView2Installed: Boolean;
var
  V: String;
begin
  V := ReadWebView2Version(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}');
  if (V = '') or (V = '0.0.0.0') then V := ReadWebView2Version(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}');
  if (V = '') or (V = '0.0.0.0') then V := ReadWebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}');
  Result := (V <> '') and (V <> '0.0.0.0');
end;

#ifdef WebView2Redist
// 동봉 redist 를 /silent /install 로 실행(사용자 단위 설치는 비관리자 권한으로도 가능) → 레지스트리 재검증.
function InstallWebView2Runtime(var ResultCode: Integer): Boolean;
var
  Exe: String;
begin
  Result := False;
  ResultCode := -1;
  ExtractTemporaryFile('{#WebView2RedistName}');
  Exe := ExpandConstant('{tmp}\{#WebView2RedistName}');
  if Exec(Exe, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := IsWebView2Installed;
  DeleteFile(Exe);
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
#ifdef WebView2Redist
var
  Code: Integer;
#endif
begin
  Result := '';
#ifdef WebView2Redist
  if IsWebView2Installed then Exit;
  WizardForm.StatusLabel.Caption := 'WebView2 런타임 설치 중…';
  if not InstallWebView2Runtime(Code) then
    // 앱 파일 설치는 계속(WebView2 는 나중에 설치 가능) — 사일런트 모드에선 메시지 억제
    SuppressibleMsgBox('WebView2 런타임 설치에 실패했습니다(코드 ' + IntToStr(Code) + ').' + #13#10 +
      'https://developer.microsoft.com/microsoft-edge/webview2/ 에서 수동 설치 후 실행하세요.',
      mbError, MB_OK, IDOK);
#endif
end;

function InitializeSetup: Boolean;
begin
  Result := True;
#ifndef WebView2Redist
  if not IsWebView2Installed then
    Result := SuppressibleMsgBox(
      'Microsoft Edge WebView2 런타임이 설치되어 있지 않습니다.' + #13#10 +
      'PromptFlow Studio 화면(BlazorWebView)을 표시하려면 WebView2 가 필요합니다.' + #13#10#13#10 +
      'https://developer.microsoft.com/microsoft-edge/webview2/ 에서 설치 후 실행하세요.' + #13#10#13#10 +
      '설치를 계속하시겠습니까?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
#endif
end;

// v16.4.0 ACL 회수: 구버전(users-modify)이 데이터 루트에 명시 부여한 BUILTIN\Users(S-1-5-32-545) 허용 ACE 만 제거.
//   상속 ACE(SYSTEM · Administrators · 현재 사용자)는 건드리지 않으며, 하위 항목의 상속분은 루트 변경으로 자동 재전파된다.
procedure RevokeLegacyUsersAce;
var
  Dir: String;
  Code: Integer;
begin
  Dir := ExpandConstant('{localappdata}\PromptFlowStudio');
  if not DirExists(Dir) then Exit;
  if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /remove:g *S-1-5-32-545 /C /Q', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('ACL 회수 실패(icacls 코드 ' + IntToStr(Code) + ') — 데이터는 영향 없음');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RevokeLegacyUsersAce;
  // 설치 후 서명 매니페스트 존재 재확인 — 누락 시 무결성 쉴드가 Unsealed 로 동작하므로 설치 실패로 간주
  if (CurStep = ssPostInstall) and not FileExists(ExpandConstant('{app}\{#Manifest}')) then
    MsgBox('무결성 서명 파일(pfi1.manifest)이 설치되지 않았습니다. 설치 패키지를 다시 받으세요.', mbCriticalError, MB_OK);
end;
