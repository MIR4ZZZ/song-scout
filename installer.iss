#define MyAppName "Song Scout"
#define MyAppVersion "1.8.4"
#define MyAppPublisher "Song Scout"
#define MyAppExeName "SongScout.exe"

[Setup]
AppId={{993F31D1-678F-4DE0-8F24-0EA5EC65DC2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Song Scout
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=installer-dist
OutputBaseFilename=SongScout-Setup-{#MyAppVersion}
SetupIconFile=TrayHost\SongScout.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\SongScout.App

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Song Scout when I sign in"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "TrayHost\bin\Release\net462\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion
Source: "release-package\SongScoutRecognizer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "release-package\_internal\*"; DestDir: "{app}\_internal"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "WinSparkle-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "third_party_licenses\*"; DestDir: "{app}\third_party_licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Song Scout"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\Song Scout"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Song Scout"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent
