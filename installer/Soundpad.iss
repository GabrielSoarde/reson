; Soundpad installer (Inno Setup script)
;
; Builds SoundpadSetup.exe — a single installer that:
;   - Installs Soundpad.exe + assets to Program Files\Soundpad
;   - Detects VB-Cable / VoiceMeeter; if missing, silent-installs the bundled
;     VBCABLE_Setup_x64.exe and prompts for reboot. If the bundle isn't present
;     at build time, falls back to opening the download page.
;   - Creates Start Menu shortcut and (optional) desktop shortcut
;   - Optional: start with Windows
;
; To compile:
;   1. Install Inno Setup 6+ (https://jrsoftware.org/isdl.php)
;   2. (Optional) drop VBCABLE_Driver_Pack.zip into installer\dependencies\
;      so build.ps1 extracts it to installer\dependencies\vbcable\ for bundling.
;   3. Run `installer\build.ps1` from the repo root.

#define MyAppName "Soundpad"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AP2 QuantumSolutions"
#define MyAppURL "https://vb-audio.com/Cable"
#define MyAppExeName "Soundpad.exe"

; Define VBC_BUNDLED if the extracted VB-Cable installer is present on disk.
; build.ps1 unzips installer\dependencies\VBCABLE_Driver_Pack.zip into
; installer\dependencies\vbcable\ before invoking ISCC.
#if FileExists("dependencies\vbcable\VBCABLE_Setup_x64.exe")
  #define VBC_BUNDLED
#endif

[Setup]
AppId={{6F8D3F5E-7A4B-4F2D-8C0E-9F1A2B3C4D5E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=SoundpadSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Iniciar o Soundpad com o Windows"; GroupDescription: "Inicialização:"; Flags: unchecked

[Files]
; The self-contained published output of `dotnet publish ... --self-contained -p:PublishSingleFile=true`
Source: "..\src\Soundpad\bin\Release\net8.0-windows\win-x64\publish\Soundpad.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\Soundpad\bin\Release\net8.0-windows\win-x64\publish\sounds\*"; DestDir: "{app}\sounds"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist

#ifdef VBC_BUNDLED
; Bundle the entire VB-Audio Virtual Cable installer folder (donationware;
; bundling allowed). VBCABLE_Setup_x64.exe is just a launcher - it requires
; the .inf / .sys / .cat driver files to live next to it at install time, so
; we ship the whole extracted folder into a temp subdirectory.
Source: "dependencies\vbcable\*"; DestDir: "{tmp}\vbcable"; Flags: deleteafterinstall recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Code]
var
  VbcInstalledThisRun: Boolean;

function IsVbCableInstalled(): Boolean;
var
  Names: TArrayOfString;
  i: Integer;
  Subkey: string;
  Disp: string;
begin
  Result := False;
  // Search uninstall keys for "VB-CABLE"
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', Names) then
  begin
    for i := 0 to GetArrayLength(Names) - 1 do
    begin
      Subkey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[i];
      if RegQueryStringValue(HKLM, Subkey, 'DisplayName', Disp) then
        if Pos('VB-CABLE', Uppercase(Disp)) > 0 then
        begin
          Result := True;
          Exit;
        end;
    end;
  end;
end;

function IsVoiceMeeterInstalled(): Boolean;
var
  Names: TArrayOfString;
  i: Integer;
  Subkey: string;
  Disp: string;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', Names) then
  begin
    for i := 0 to GetArrayLength(Names) - 1 do
    begin
      Subkey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[i];
      if RegQueryStringValue(HKLM, Subkey, 'DisplayName', Disp) then
        if Pos('VOICEMEETER', Uppercase(Disp)) > 0 then
        begin
          Result := True;
          Exit;
        end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Msg: string;
  VbcExe: string;
begin
  if CurStep = ssPostInstall then
  begin
    VbcInstalledThisRun := False;

    if not (IsVbCableInstalled() or IsVoiceMeeterInstalled()) then
    begin
      #ifdef VBC_BUNDLED
        // Bundled path: run VBCABLE_Setup_x64.exe silently.
        // The Windows driver-signing UAC prompt may still appear — that's a
        // Windows behavior we can't suppress, but the user only sees it once.
        VbcExe := ExpandConstant('{tmp}\vbcable\VBCABLE_Setup_x64.exe');
        Msg := 'O Soundpad precisa de um cabo de áudio virtual para enviar som ao Discord/Valorant.' + #13#10#13#10 +
               'Vou instalar agora o VB-Cable (gratuito, da VB-Audio).' + #13#10 +
               'O Windows pode pedir confirmação para instalar o driver. Aceite para continuar.' + #13#10#13#10 +
               'IMPORTANTE: após a instalação será necessário reiniciar o Windows.';
        MsgBox(Msg, mbInformation, MB_OK);
        if Exec(VbcExe, '-i -h', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        begin
          if ResultCode = 0 then
          begin
            VbcInstalledThisRun := True;
            MsgBox('VB-Cable instalado com sucesso!' + #13#10#13#10 +
                   'Reinicie o Windows e abra o Soundpad — ele detectará o dispositivo automaticamente.' + #13#10 +
                   'No Discord/Valorant, configure o microfone como "CABLE Output (VB-Audio Virtual Cable)".',
                   mbInformation, MB_OK);
          end
          else
          begin
            MsgBox('A instalação do VB-Cable retornou código ' + IntToStr(ResultCode) + '.' + #13#10 +
                   'Você pode reinstalar manualmente em https://vb-audio.com/Cable/ depois.',
                   mbError, MB_OK);
          end;
        end
        else
        begin
          MsgBox('Não consegui iniciar o instalador do VB-Cable.' + #13#10 +
                 'Baixe e instale manualmente em https://vb-audio.com/Cable/',
                 mbError, MB_OK);
        end;
      #else
        // Fallback path: no bundled installer, open the download page.
        Msg := 'O Soundpad precisa de um cabo de áudio virtual (VB-Cable ou VoiceMeeter) para enviar som ao Discord/Valorant.' + #13#10#13#10 +
               'Nenhum foi detectado nesta máquina.' + #13#10#13#10 +
               'Deseja abrir a página de download do VB-Cable agora?';
        if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES then
        begin
          ShellExec('open', 'https://vb-audio.com/Cable/', '', '', SW_SHOW, ewNoWait, ResultCode);
          MsgBox('Após instalar o VB-Cable e reiniciar o Windows, abra o Soundpad novamente — ele detectará o dispositivo automaticamente.', mbInformation, MB_OK);
        end;
      #endif
    end;
  end;
end;

// Trigger a reboot prompt at the end of setup if we just installed VB-Cable.
// Driver installs always require a reboot for the audio endpoint to register.
function NeedRestart(): Boolean;
begin
  Result := VbcInstalledThisRun;
end;
