; Build via packaging/build.py. No files from the personal data folder are bundled.
#ifndef PayloadDir
  #error PayloadDir must identify the staged portable package
#endif
#define Launcher "00 - Start SamsungController Server.exe"

[Setup]
AppId={{D459328D-1B96-441E-A03B-431691A317A9}
AppName=SamsungController
AppVersion={#AppVersion}
AppPublisher=SamsungController contributors
AppPublisherURL=https://github.com/whipstein/SamsungController
AppSupportURL=https://github.com/whipstein/SamsungController/issues
AppUpdatesURL=https://github.com/whipstein/SamsungController/releases
DefaultDirName={localappdata}\Programs\SamsungController
DefaultGroupName=SamsungController
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#TargetArchitecture}
ArchitecturesInstallIn64BitMode={#TargetArchitecture}
MinVersion=10.0.19045
OutputBaseFilename={#OutputName}
SetupIconFile=..\icons\SamsungController.ico
UninstallDisplayIcon={app}\{#Launcher}
LicenseFile={#PayloadDir}\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
CloseApplications=no
RestartApplications=no
; Never interrupt an adjustment or forcibly terminate a server to update files.
AppMutex=Local\SamsungController.Server.Running

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SamsungController"; Filename: "{app}\{#Launcher}"; WorkingDir: "{app}"
Name: "{userdesktop}\SamsungController"; Filename: "{app}\{#Launcher}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#Launcher}"; Description: "Start SamsungController and open the webpage"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; Uninstall removes installed files/shortcuts only. Deliberately no deletion of
; AppData, saved TV profiles, pairing tokens, calibration settings, or logs.
