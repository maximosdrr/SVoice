#define MyAppName "SVoice"
#define MyAppVersion "1.4.6"
#define MyAppPublisher "SVoice"
#define MyAppExeName "SVoice.exe"

[Setup]
AppId={{7727E37A-370C-47E4-A753-C095766D64E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://vb-audio.com/Cable/
AppSupportURL=https://vb-audio.com/Cable/
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=SVoice-Setup-{#MyAppVersion}
SetupIconFile=..\windows\runner\resources\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
InfoBeforeFile=licenses\VB-CABLE-NOTICE.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
RestartIfNeededByRun=no
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "installvbcable"; Description: "Instalar o microfone virtual VB-CABLE (recomendado)"; GroupDescription: "Integração com o Discord:"; Flags: checkedonce
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "..\build\windows\x64\runner\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\python_service\dist\svoice_xtts_service\*"; DestDir: "{app}\xtts_service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "vendor\VBCABLE\*"; DestDir: "{tmp}\SVoice-VBCABLE"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall; Tasks: installvbcable
Source: "licenses\VB-CABLE-NOTICE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\XTTS-NOTICE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "licenses\FFMPEG-NOTICE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autoprograms}\{#MyAppName}\Sobre o VB-CABLE"; Filename: "https://vb-audio.com/Cable/"

[Run]
Filename: "{tmp}\SVoice-VBCABLE\VBCABLE_Setup_x64.exe"; Parameters: "-i -h"; WorkingDir: "{tmp}\SVoice-VBCABLE"; StatusMsg: "Instalando o microfone virtual VB-CABLE..."; Flags: runhidden waituntilterminated; Tasks: installvbcable; Check: ShouldInstallCable
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o SVoice"; Flags: nowait postinstall skipifsilent; Check: CanLaunchAfterInstall

[Code]
var
  CableWasInstalledBefore: Boolean;

function IsCableInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\VBAudioVACMME') or
            RegKeyExists(HKLM64, 'SOFTWARE\VB-Audio\Cable');
end;

procedure InitializeWizard;
begin
  CableWasInstalledBefore := IsCableInstalled;
end;

function ShouldInstallCable: Boolean;
begin
  Result := not CableWasInstalledBefore;
end;

function CanLaunchAfterInstall: Boolean;
begin
  Result := CableWasInstalledBefore or
            (not WizardIsTaskSelected('installvbcable'));
end;

function NeedRestart: Boolean;
begin
  Result := WizardIsTaskSelected('installvbcable') and
            (not CableWasInstalledBefore);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and NeedRestart then
  begin
    WizardForm.FinishedLabel.Caption :=
      'O SVoice e o microfone virtual foram instalados.' + #13#10 + #13#10 +
      'Reinicie o Windows para ativar o CABLE Output no Discord.';
  end;
end;
