; SVoice standalone installer (Xbox Game Bar widget + XTTS v2 service + VB-CABLE).
; Built by installer\build-installer.ps1, which prepares StagingDir.

#define MyAppName "SVoice"
#ifndef MyAppVersion
  #define MyAppVersion "2.1.0"
#endif
#define MyAppPublisher "SVoice"
#ifndef StagingDir
  #define StagingDir "..\artifacts\installer-staging"
#endif
#define LegacyAppId "{7727E37A-370C-47E4-A753-C095766D64E7}"

[Setup]
AppId={{B4C1E6A2-5D0F-4E7A-9C3B-2F6D8A1E7C55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/maximosdrr/SVoice
AppSupportURL=https://github.com/maximosdrr/SVoice
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=SVoice-Setup-{#MyAppVersion}
SetupIconFile=svoice.ico
UninstallDisplayIcon={app}\SVoice.Setup.exe
UninstallDisplayName={#MyAppName} (Xbox Game Bar)
InfoBeforeFile=licenses\VB-CABLE-NOTICE.txt
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupLogging=yes
DiskSpanning=no
ExtraDiskSpaceRequired=8589934592
UsePreviousAppDir=yes
CloseApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "installvbcable"; Description: "Instalar ou reparar o microfone virtual VB-CABLE (VB-Audio, donationware)"; GroupDescription: "Microfone virtual:"; Flags: checkedonce
Name: "downloadmodel"; Description: "Baixar o modelo XTTS v2 (1,9 GB) agora, se ainda não estiver instalado"; GroupDescription: "Modelo de voz:"

[Files]
Source: "{#StagingDir}\SVoice.Setup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\SVoice.Setup.exe"; Flags: dontcopy
Source: "{#StagingDir}\widget\*"; DestDir: "{app}\widget"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\runtime\manifest.json"; DestDir: "{app}"; DestName: "runtime-manifest.json"; Flags: ignoreversion
Source: "{#StagingDir}\runtime\*.zip"; DestDir: "{tmp}\runtime"; Flags: nocompression deleteafterinstall
Source: "{#StagingDir}\vendor\VBCABLE\*"; DestDir: "{app}\vendor\VBCABLE"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#StagingDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
; Optional offline model placed next to the installer (xtts_v2\model.pth etc.).
Source: "{src}\xtts_v2\*"; DestDir: "{commonappdata}\SVoice\models\tts\tts_models--multilingual--multi-dataset--xtts_v2"; Flags: external skipifsourcedoesntexist ignoreversion uninsneveruninstall

[InstallDelete]
; An update must not leave an older MSIX beside the current package. FindFirst
; order is not a version order and could otherwise select the stale package.
Type: files; Name: "{app}\widget\*.msix"

[UninstallDelete]
; Runtime packs are extracted by SVoice.Setup after [Files] is processed, so
; Inno does not track their individual files in the uninstall log.
Type: filesandordirs; Name: "{app}\runtime"

[Dirs]
Name: "{commonappdata}\SVoice"; Permissions: users-modify
Name: "{commonappdata}\SVoice\models"; Permissions: users-modify

[Icons]
Name: "{group}\Reparar SVoice"; Filename: "{app}\SVoice.Setup.exe"; Parameters: "repair --install-dir ""{app}"" --pause"; Comment: "Reinstala o widget e valida VB-CABLE, modelo e GPU"
Name: "{group}\Diagnóstico do SVoice"; Filename: "{app}\SVoice.Setup.exe"; Parameters: "diagnostics --install-dir ""{app}"" --pause"
Name: "{group}\Guia de instalação e uso"; Filename: "{app}\docs\instalacao-e-uso.md"
Name: "{group}\Sobre o VB-CABLE (VB-Audio)"; Filename: "https://vb-audio.com/Cable/"

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\RunOnce"; ValueType: string; ValueName: "SVoiceVerify"; ValueData: """{app}\SVoice.Setup.exe"" verify --install-dir ""{app}"" --after-reboot"; Flags: uninsdeletevalue; Check: NeedRestart

[Run]
Filename: "{cmd}"; Parameters: "/c start """" ms-gamebar://"; Description: "Abrir a Xbox Game Bar (Win+G) e escolher o widget SVoice"; Flags: postinstall nowait skipifsilent runasoriginaluser unchecked; Check: not NeedRestart

[Code]
var
  BackendPage: TInputOptionWizardPage;
  RestartRequired: Boolean;
  StepFailures: TStringList;
  RecommendedPack: String;
  RecommendedReason: String;
  StatusCounter: Integer;

function NeedRestart(): Boolean;
begin
  Result := RestartRequired;
end;

function HelperPath(): String;
begin
  Result := ExpandConstant('{app}\SVoice.Setup.exe');
end;

// ------------------------------------------------------------ JSON helpers

function JsonUnescape(const Value: String): String;
var
  I: Integer;
  C: Char;
  Hex: String;
begin
  Result := '';
  I := 1;
  while I <= Length(Value) do
  begin
    C := Value[I];
    if (C = '\') and (I < Length(Value)) then
    begin
      I := I + 1;
      C := Value[I];
      case C of
        'n': Result := Result + #13#10;
        't': Result := Result + #9;
        'r': ;
        'u':
          begin
            Hex := Copy(Value, I + 1, 4);
            Result := Result + Chr(StrToIntDef('$' + Hex, 63));
            I := I + 4;
          end;
      else
        Result := Result + C;
      end;
    end
    else
      Result := Result + C;
    I := I + 1;
  end;
end;

function JsonValue(const Json, Key: String): String;
var
  P, Start: Integer;
  Quoted: Boolean;
  C: Char;
begin
  Result := '';
  P := Pos('"' + Key + '":', Json);
  if P = 0 then
    Exit;
  P := P + Length(Key) + 3;
  while (P <= Length(Json)) and (Json[P] = ' ') do
    P := P + 1;
  if P > Length(Json) then
    Exit;
  Quoted := Json[P] = '"';
  if Quoted then
    P := P + 1;
  Start := P;
  while P <= Length(Json) do
  begin
    C := Json[P];
    if Quoted then
    begin
      if C = '\' then
        P := P + 1
      else if C = '"' then
        Break;
    end
    else if (C = ',') or (C = '}') then
      Break;
    P := P + 1;
  end;
  Result := Copy(Json, Start, P - Start);
  if Quoted then
    Result := JsonUnescape(Result);
end;

function ReadStatus(const StatusFile: String; var State, Msg: String; var Progress, ExitCode: Integer): Boolean;
var
  Lines: TArrayOfString;
  Json: String;
  I: Integer;
  ProgressText: String;
begin
  Result := False;
  if not FileExists(StatusFile) then
    Exit;
  if not LoadStringsFromFile(StatusFile, Lines) then
    Exit;
  Json := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Json := Json + Lines[I];
  if Json = '' then
    Exit;
  State := JsonValue(Json, 'state');
  Msg := JsonValue(Json, 'message');
  ProgressText := JsonValue(Json, 'progress');
  if Copy(ProgressText, 1, 1) = '1' then
    Progress := 100
  else if Pos('.', ProgressText) > 0 then
  begin
    ProgressText := Copy(ProgressText, Pos('.', ProgressText) + 1, 2);
    if Length(ProgressText) = 1 then
      ProgressText := ProgressText + '0';
    Progress := StrToIntDef(ProgressText, 0);
  end
  else
    Progress := 0;
  ExitCode := StrToIntDef(JsonValue(Json, 'exit_code'), -1);
  Result := State <> '';
end;

// ---------------------------------------------------------- helper runner

function RunHelper(const Args: String; AsUser: Boolean; const Caption, Description: String;
  TimeoutSeconds: Integer; var ResultMessage: String): Integer;
var
  StatusDir: String;
  StatusFile: String;
  Page: TOutputProgressWizardPage;
  Launched: Boolean;
  ExecCode: Integer;
  State, Msg: String;
  Progress, ExitCode: Integer;
  Elapsed: Integer;
begin
  StatusCounter := StatusCounter + 1;
  if AsUser then
  begin
    // Inno Setup 7 protects {tmp} so a helper deliberately launched with the
    // original, non-elevated token only has read access there. Use a per-user
    // directory for the status channel shared with that helper.
    StatusDir := ExpandConstant('{localappdata}\SVoice\InstallerStatus');
    ForceDirectories(StatusDir);
    StatusFile := StatusDir + '\svoice-status-' + IntToStr(StatusCounter) + '.json';
  end
  else
    StatusFile := ExpandConstant('{tmp}\svoice-status-') + IntToStr(StatusCounter) + '.json';
  DeleteFile(StatusFile);
  Log('Helper: ' + Args + ' (asUser=' + IntToStr(Integer(AsUser)) + ')');
  Page := CreateOutputProgressPage(Caption, Description);
  Page.SetProgress(0, 100);
  Page.Show;
  try
    if AsUser then
      Launched := ExecAsOriginalUser(HelperPath(), Args + ' --status-file "' + StatusFile + '"', '', SW_HIDE, ewNoWait, ExecCode)
    else
      Launched := Exec(HelperPath(), Args + ' --status-file "' + StatusFile + '"', '', SW_HIDE, ewNoWait, ExecCode);
    if not Launched then
    begin
      ResultMessage := 'Não foi possível iniciar o assistente: ' + SysErrorMessage(ExecCode);
      Result := 1;
      Exit;
    end;
    Elapsed := 0;
    State := '';
    ExitCode := -1;
    Result := 1;
    ResultMessage := 'O assistente não respondeu.';
    while Elapsed < TimeoutSeconds * 2 do
    begin
      Sleep(500);
      Elapsed := Elapsed + 1;
      if ReadStatus(StatusFile, State, Msg, Progress, ExitCode) then
      begin
        Page.SetText(Msg, '');
        if Progress > 0 then
          Page.SetProgress(Progress, 100);
        if (State = 'done') or (State = 'failed') then
        begin
          ResultMessage := Msg;
          Result := ExitCode;
          Break;
        end;
      end;
    end;
    Log('Helper result ' + IntToStr(Result) + ': ' + ResultMessage);
  finally
    Page.Hide;
    DeleteFile(StatusFile);
  end;
end;

function RunHelperCapture(const Args: String; var Output: String): Integer;
var
  OutFile: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
begin
  StatusCounter := StatusCounter + 1;
  OutFile := ExpandConstant('{tmp}\svoice-out-') + IntToStr(StatusCounter) + '.txt';
  Result := 1;
  Output := '';
  if not Exec(ExpandConstant('{cmd}'), '/c ""' + ExpandConstant('{tmp}\SVoice.Setup.exe') + '" ' + Args + ' > "' + OutFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  Result := ResultCode;
  if LoadStringsFromFile(OutFile, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      Output := Output + Lines[I] + #13#10;
end;

// -------------------------------------------------------- legacy version

function LegacyUninstallString(): String;
var
  Value: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#LegacyAppId}_is1', 'UninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKLM, 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{#LegacyAppId}_is1', 'UninstallString', Value) then
    Result := Value;
end;

procedure RemoveLegacyVersion();
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  Uninstaller := LegacyUninstallString();
  if Uninstaller = '' then
    Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  if MsgBox('Uma versão anterior do SVoice (aplicativo de área de trabalho) está instalada. ' +
    'Ela será removida para dar lugar à versão para Xbox Game Bar. Seus perfis de voz e o modelo XTTS serão preservados.' + #13#10#13#10 +
    'Deseja continuar?', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    RaiseException('A instalação foi cancelada porque a versão anterior não foi removida.');
  end;
  Log('Removing legacy SVoice: ' + Uninstaller);
  if not Exec(Uninstaller, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    MsgBox('A versão anterior não pôde ser removida automaticamente. Remova-a em Aplicativos instalados e execute este instalador novamente.', mbError, MB_OK)
  else
    Log('Legacy uninstaller exit code ' + IntToStr(ResultCode));
end;

// --------------------------------------------------------------- wizard

function InitializeSetup(): Boolean;
var
  Output: String;
  Code: Integer;
begin
  Result := True;
  StepFailures := TStringList.Create;
  RestartRequired := False;
  ExtractTemporaryFile('SVoice.Setup.exe');
  Code := RunHelperCapture('check --install-dir "' + ExpandConstant('{autopf}\{#MyAppName}') + '"', Output);
  Log('Pre-check (' + IntToStr(Code) + '): ' + Output);
  if Code = 2 then
  begin
    MsgBox('O SVoice não pode ser instalado neste computador:' + #13#10#13#10 + Output, mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  RecommendedPack := 'torch-cpu';
  RecommendedReason := 'não foi possível detectar a GPU';
  if Code = 0 then
  begin
    Code := RunHelperCapture('detect-gpu --json', Output);
    RecommendedPack := JsonValue(Output, 'recommended_pack');
    RecommendedReason := JsonValue(Output, 'message');
    if RecommendedPack = '' then
      RecommendedPack := 'torch-cpu';
  end;
end;

procedure InitializeWizard();
begin
  BackendPage := CreateInputOptionPage(wpSelectTasks,
    'Aceleração de hardware',
    'Escolha o runtime de inferência do XTTS v2',
    'O SVoice detectou: ' + RecommendedReason + '.' + #13#10 +
    'O runtime recomendado é instalado e validado com uma síntese completa. ' +
    'Se a validação falhar, o SVoice usa a CPU automaticamente.',
    True, False);
  BackendPage.Add('Automático (recomendado): ' + RecommendedPack);
  BackendPage.Add('NVIDIA CUDA 13 (placas Turing/RTX ou mais novas, driver 580+; 3 GB)');
  BackendPage.Add('AMD/Intel DirectML — experimental (DirectX 12; 1,8 GB)');
  BackendPage.Add('Somente CPU (funciona em qualquer PC; mais lento)');
  BackendPage.SelectedValueIndex := 0;
end;

function SelectedPack(): String;
begin
  case BackendPage.SelectedValueIndex of
    1: Result := 'torch-cuda';
    2: Result := 'torch-directml';
    3: Result := 'torch-cpu';
  else
    Result := RecommendedPack;
  end;
end;

function SelectedMode(): String;
begin
  case BackendPage.SelectedValueIndex of
    1: Result := 'cuda';
    2: Result := 'directml';
    3: Result := 'cpu';
  else
    Result := 'auto';
  end;
end;

procedure RecordFailure(const Step, Detail: String);
begin
  StepFailures.Add(Step + ': ' + Detail);
  Log('Step failed - ' + Step + ': ' + Detail);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Msg: String;
  App: String;
  Msix: String;
  Args: String;
begin
  if CurStep = ssInstall then
  begin
    RemoveLegacyVersion();
    Exec(ExpandConstant('{tmp}\SVoice.Setup.exe'), 'stop-service', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exit;
  end;

  if CurStep <> ssPostInstall then
    Exit;

  App := ExpandConstant('{app}');

  // 1. Trust the widget certificate now, but register the MSIX only after the
  // runtime/model tests. An open Game Bar otherwise starts XTTS midway through
  // installation and races the validation helper.
  Msix := App + '\widget\SVoice.GameBar_{#MyAppVersion}.0_x64.msix';
  if not FileExists(Msix) then
    RecordFailure('Widget', 'pacote MSIX não encontrado')
  else if FileExists(App + '\widget\SVoice.GameBar.cer') then
  begin
    Code := RunHelper('install-cert --cer "' + App + '\widget\SVoice.GameBar.cer"', False,
      'Certificado do widget', 'Confiando no certificado de assinatura do SVoice…', 120, Msg);
    if Code <> 0 then
      RecordFailure('Certificado', Msg);
  end;

  // 2. VB-CABLE (driver; elevated).
  if WizardIsTaskSelected('installvbcable') then
  begin
    Code := RunHelper('vbcable --install --source "' + App + '\vendor\VBCABLE"', False,
      'Microfone virtual VB-CABLE', 'VB-CABLE é um software da VB-Audio Software (vb-cable.com), distribuído como donationware.', 600, Msg);
    if Code = 3010 then
      RestartRequired := True
    else if Code <> 0 then
      RecordFailure('VB-CABLE', Msg);
  end;

  // 3. Runtime packs (Program Files; elevated).
  Code := RunHelper('install-runtime --manifest "' + App + '\runtime-manifest.json" --source "' + ExpandConstant('{tmp}\runtime') +
    '" --target "' + App + '\runtime" --pack ' + SelectedPack(), False,
    'Runtime XTTS', 'Instalando o Python embutido e o PyTorch (' + SelectedPack() + ')…', 3600, Msg);
  if Code <> 0 then
    RecordFailure('Runtime', Msg);

  // An older widget may still be open during an update. Stop its service so
  // model/backend validation starts with the complete installed runtime.
  if StepFailures.Count = 0 then
  begin
    Code := RunHelper('stop-service', True, 'Serviço XTTS', 'Preparando o serviço XTTS instalado…', 120, Msg);
    if Code <> 0 then
      RecordFailure('Serviço XTTS', Msg);
  end;

  // 4. Compute mode chosen by the user (written to the user's config by the service).
  if SelectedMode() <> 'auto' then
    Log('Compute mode override: ' + SelectedMode());

  // 5. Model (shared with the full-trust Game Bar process; legacy per-user models are migrated).
  if WizardIsTaskSelected('downloadmodel') and (StepFailures.Count = 0) then
  begin
    Code := RunHelper('ensure-model --service-dir "' + App + '\service" --runtime-dir "' + App + '\runtime" --target shared', True,
      'Modelo XTTS v2', 'Verificando ou baixando o modelo XTTS v2 (1,9 GB, licença Coqui CPML — uso não comercial)…', 4 * 3600, Msg);
    if Code <> 0 then
      RecordFailure('Modelo XTTS v2', Msg);
  end
  else if StepFailures.Count = 0 then
  begin
    // Even when downloads are disabled, promote a complete model left by an
    // older SVoice release so the packaged Game Bar process can read it.
    Code := RunHelper('ensure-model --service-dir "' + App + '\service" --runtime-dir "' + App + '\runtime" --target shared --no-download', True,
      'Modelo XTTS v2', 'Procurando um modelo XTTS existente para reutilizar…', 1800, Msg);
    if Code <> 0 then
      Log('Nenhum modelo existente foi migrado; o download continua opcional. ' + Msg);
  end;

  // 6. Backend validation (full synthesis on the selected backend, CPU fallback).
  if StepFailures.Count = 0 then
  begin
    Args := 'test-service --service-dir "' + App + '\service" --runtime-dir "' + App + '\runtime" --set-mode ' + SelectedMode();
    if SelectedMode() <> 'auto' then
      Args := Args + ' --backend ' + SelectedMode() + ' --torch-pack ' + SelectedPack();
    if not WizardIsTaskSelected('downloadmodel') then
      Args := Args + ' --skip-if-model-missing';
    Code := RunHelper(Args, True, 'Teste de síntese', 'Executando uma síntese completa para validar a GPU ou a CPU…', 1800, Msg);
    if Code <> 0 then
      RecordFailure('Teste de síntese', Msg);
  end;

  // 7. Register/update the widget only after XTTS is ready. The MSIX update
  // closes an older widget process and its next activation sees a complete
  // runtime plus the shared model.
  if FileExists(Msix) then
  begin
    if FileExists(App + '\widget\SVoice.GameBar.cer') then
      Args := 'install-msix --msix "' + Msix + '" --cer "' + App + '\widget\SVoice.GameBar.cer"'
    else
      Args := 'install-msix --msix "' + Msix + '"';
    Code := RunHelper(Args, True, 'Widget da Xbox Game Bar', 'Instalando o widget SVoice…', 600, Msg);
    if Code <> 0 then
      RecordFailure('Widget', Msg);
  end;

  // 8. Final verification.
  Args := 'verify --install-dir "' + App + '"';
  if not WizardIsTaskSelected('downloadmodel') then
    Args := Args + ' --model-optional';
  Code := RunHelper(Args, True, 'Verificação final', 'Conferindo o widget, o VB-CABLE, o runtime e o modelo…', 300, Msg);
  if (Code <> 0) and (Code <> 3010) then
    RecordFailure('Verificação', Msg);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
  I: Integer;
begin
  if CurPageID <> wpFinished then
    Exit;
  if StepFailures.Count = 0 then
  begin
    Text := 'O SVoice foi instalado.' + #13#10#13#10 +
      'Pressione Win + G, abra o menu de widgets e escolha SVoice. ' +
      'No Discord, selecione "CABLE Output" como microfone.';
    if RestartRequired then
      Text := Text + #13#10#13#10 + 'Reinicie o Windows para ativar o microfone virtual VB-CABLE. ' +
        'A instalação será verificada automaticamente após a reinicialização.';
    if not WizardIsTaskSelected('downloadmodel') then
      Text := Text + #13#10#13#10 + 'O modelo XTTS v2 não foi solicitado durante a instalação. ' +
        'Abra o diagnóstico do widget para baixá-lo antes da primeira síntese.';
  end
  else
  begin
    Text := 'A instalação terminou com pendências:' + #13#10;
    for I := 0 to StepFailures.Count - 1 do
      Text := Text + '• ' + StepFailures[I] + #13#10;
    Text := Text + #13#10 + 'Use Iniciar › SVoice › Reparar SVoice ou execute este instalador novamente. ' +
      'Registro: ' + ExpandConstant('{log}');
  end;
  WizardForm.FinishedLabel.Caption := Text;
end;

// ------------------------------------------------------------ uninstall

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Helper: String;
  RemoveProfiles, RemoveModels: Boolean;
  Args: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  Helper := ExpandConstant('{app}\SVoice.Setup.exe');
  Exec(Helper, 'stop-service', '', SW_HIDE, ewWaitUntilTerminated, Code);

  if UninstallSilent then
  begin
    // Automation, enterprise removal and upgrades must be non-destructive by
    // default. Data deletion always requires the interactive confirmation.
    RemoveProfiles := False;
    RemoveModels := False;
  end
  else
  begin
    RemoveProfiles := MsgBox('Excluir também os perfis de voz clonados (pasta AppData\Local\SVoice\XTTS\voices)?' + #13#10 +
      'Escolha Não para preservá-los para uma instalação futura.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    RemoveModels := MsgBox('Excluir também o modelo XTTS v2 baixado (cerca de 1,9 GB)?' + #13#10 +
      'Escolha Não para evitar um novo download em uma instalação futura.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;

  Args := 'uninstall-data';
  if RemoveProfiles then
    Args := Args + ' --profiles';
  if RemoveModels then
    Args := Args + ' --models';
  // Inno Setup does not allow ExecAsOriginalUser during uninstall. The
  // elevated process still belongs to the same Windows account, so the helper
  // resolves the same LocalAppData and can safely preserve/remove its data.
  Exec(Helper, Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if RemoveModels then
    Exec(Helper, 'uninstall-data --models', '', SW_HIDE, ewWaitUntilTerminated, Code);

  Exec(Helper, 'remove-msix', '', SW_HIDE, ewWaitUntilTerminated, Code);
  MsgBox('O VB-CABLE (VB-Audio Software) é um driver compartilhado e permanece instalado. ' +
    'Para removê-lo, use "VBCABLE_Setup_x64.exe -u" ou Aplicativos instalados do Windows.', mbInformation, MB_OK);
end;
