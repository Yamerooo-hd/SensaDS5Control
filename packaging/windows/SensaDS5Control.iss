#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{8D6B4F7C-1C39-4E65-8C5A-926D5E1D9042}
AppName=SensaDS5Control
AppVersion={#AppVersion}
AppPublisher=Yamerooo-hd
AppPublisherURL=https://github.com/Yamerooo-hd/SensaDS5Control
DefaultDirName={localappdata}\Programs\SensaDS5Control
DefaultGroupName=SensaDS5Control
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist-packages
OutputBaseFilename=SensaDS5Control-Setup-win-x64
SetupIconFile=..\..\app\SensaDS5Control.App\Assets\app_icon.ico
UninstallDisplayIcon={app}\SensaDS5Control.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

[Files]
Source: "..\..\dist-win\SensaDS5Control.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\SensaDS5Control"; Filename: "{app}\SensaDS5Control.exe"
Name: "{group}\Uninstall SensaDS5Control"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SensaDS5Control"; Filename: "{app}\SensaDS5Control.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SensaDS5Control.exe"; Description: "Launch SensaDS5Control"; Flags: nowait postinstall skipifsilent
