#ifndef AppVersion
  #define AppVersion "0.4.3"
#endif

[Setup]
AppId={{2393D0C4-2073-4557-BFE3-8D99E6A7245C}
AppName=키나뷰
AppVersion={#AppVersion}
AppPublisher=DAMKINA00
AppPublisherURL=https://github.com/DAMKINA00/KinaView
DefaultDirName={localappdata}\Programs\KinaView
DefaultGroupName=키나뷰
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
AllowNoIcons=yes
WizardStyle=modern
SetupIconFile=..\Assets\KinaView.ico
UninstallDisplayIcon={app}\KinaView.exe
OutputDir=..\artifacts\release
OutputBaseFilename=KinaView-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 만들기"; Flags: unchecked
Name: "explorer"; Description: "탐색기 메뉴 및 연결 프로그램에 등록 (기본 앱 유지)"

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\키나뷰"; Filename: "{app}\KinaView.exe"
Name: "{userdesktop}\키나뷰"; Filename: "{app}\KinaView.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KinaView.exe"; Parameters: "--register"; Flags: runhidden waituntilterminated; Tasks: explorer
Filename: "{app}\KinaView.exe"; Description: "키나뷰 실행"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\KinaView.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveKinaViewAssociations"; Check: RegisteredHere

[Code]
function RegisteredHere: Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Classes\Applications\KinaView.exe\shell\open\command', '', Command)
    and (CompareText(Command, '"' + ExpandConstant('{app}\KinaView.exe') + '" "%1"') = 0);
end;
