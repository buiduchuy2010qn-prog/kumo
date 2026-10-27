; Inno Setup 6 Official Script for Kumo v1.0.0
; Note: build.ps1 also compiles a standalone native KumoSetup.exe directly with zero external tools!

[Setup]
AppId={{8D4F6A12-3C9E-4B71-9E55-A2018C90E104}
AppName=Kumo
AppVersion=1.0.0
AppVerName=Kumo 1.0.0
AppPublisher=Kumo Desktop
DefaultDirName={localappdata}\Programs\Kumo
DefaultGroupName=Kumo
UninstallDisplayIcon={app}\Kumo.exe
PrivilegesRequired=lowest
AppMutex=Kumo.SingleInstance
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=Kumo.ico
OutputBaseFilename=KumoSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktop"; Description: "Create a Kumo shortcut on Desktop"
Name: "startup"; Description: "Start Kumo automatically when Windows starts"

[Files]
Source: "Kumo.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Kumo"; Filename: "{app}\Kumo.exe"
Name: "{autoprograms}\Kumo\Kumo"; Filename: "{app}\Kumo.exe"
Name: "{autoprograms}\Kumo\Kumo Settings"; Filename: "{app}\Kumo.exe"; Parameters: "--settings"
Name: "{autodesktop}\Kumo"; Filename: "{app}\Kumo.exe"; Tasks: desktop

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Kumo"; ValueData: """{app}\Kumo.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Kumo.exe"; Description: "{cm:LaunchProgram,Kumo}"; Flags: nowait postinstall skipifsilent
