; Inno Setup script for Housecast. Build with:
;   dotnet publish src\Housecast\Housecast.csproj -c Release -r win-x64 -o publish\win-x64
;   iscc /DAppVersion=1.0.0 /DArch=x64 installer\Housecast.iss
; Arch is x64 or arm64, and expects the matching publish\win-<Arch> folder.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6A1F2C3E-4B5D-4E8F-9A0B-1C2D3E4F5A6B}
AppName=Housecast
AppVersion={#AppVersion}
AppPublisher=Housecast contributors
AppPublisherURL=https://github.com/sherri55/housecast
DefaultDirName={autopf}\Housecast
DisableProgramGroupPage=yes
; Admin rights are needed for the firewall rule that lets speakers reach the app
PrivilegesRequired=admin
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
LicenseFile=..\LICENSE
SetupIconFile=..\src\Housecast\Housecast.ico
UninstallDisplayIcon={app}\Housecast.exe
OutputDir=..\dist
OutputBaseFilename=Housecast-{#AppVersion}-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running copy, which holds its files and the PTP ports, before installing over it.
; Force: a tray app has no window to ask, and an AppMutex check would abort silent upgrades.
CloseApplications=force

[Tasks]
Name: startup; Description: "Start Housecast when Windows starts"

[Files]
Source: "..\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"
Source: "..\NOTICE"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\Housecast"; Filename: "{app}\Housecast.exe"

[Registry]
; The same value the app's own "Start with Windows" menu item sets
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Housecast"; \
  ValueData: """{app}\Housecast.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Speakers answer the app's PTP clock and ask it for missed audio over UDP; this lets those
; replies in on home (private) networks without a firewall prompt
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Housecast"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Housecast"" dir=in action=allow protocol=UDP profile=private,domain program=""{app}\Housecast.exe"" enable=yes"; Flags: runhidden
Filename: "{app}\Housecast.exe"; Description: "Start Housecast"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Housecast"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
