#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{8D6B4F7C-1C39-4E65-8C5A-926D5E1D9042}
AppName=SensaDS5Control
AppVersion={#AppVersion}
AppVerName=SensaDS5Control {#AppVersion}
AppPublisher=Yamerooo-hd
AppPublisherURL=https://github.com/Yamerooo-hd/SensaDS5Control
AppSupportURL=https://github.com/Yamerooo-hd/SensaDS5Control/issues
AppUpdatesURL=https://github.com/Yamerooo-hd/SensaDS5Control/releases
DefaultDirName={autopf}\SensaDS5Control
DefaultGroupName=SensaDS5Control
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist-packages
OutputBaseFilename=SensaDS5Control-Setup-win-x64
SetupIconFile=..\..\app\SensaDS5Control.App\Assets\app_icon.ico
UninstallDisplayIcon={app}\SensaDS5Control.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=no
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Additional shortcuts:"
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "..\..\dist-win\SensaDS5Control.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SensaDS5Control"; Filename: "{app}\SensaDS5Control.exe"; IconFilename: "{app}\SensaDS5Control.exe"; Comment: "DualSense & Xbox 360 Wireless Controller Bridge"; Tasks: startmenuicon
Name: "{autodesktop}\SensaDS5Control"; Filename: "{app}\SensaDS5Control.exe"; IconFilename: "{app}\SensaDS5Control.exe"; Comment: "DualSense & Xbox 360 Wireless Controller Bridge"; Tasks: desktopicon

[Run]
Filename: "{app}\SensaDS5Control.exe"; Description: "Launch SensaDS5Control"; Flags: nowait postinstall skipifsilent
