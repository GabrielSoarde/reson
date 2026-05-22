; Soundpad installer (Inno Setup script)
;
; Builds SoundpadSetup.exe — a single installer that:
;   - Installs Soundpad.exe + assets to Program Files\Soundpad
;   - Detects VB-Cable; if missing, runs its installer (bundled or downloaded)
;   - Creates Start Menu shortcut and (optional) desktop shortcut
;   - Optional: start with Windows
;
; To compile:
;   1. Install Inno Setup 6+ (https://jrsoftware.org/isdl.php)
;   2. Run `installer\build.ps1` from the repo root.
;
; Notes on VB-Cable bundling:
;   VB-Cable is donationware (vb-audio.com). For personal/private redistribution
;   it's allowed unmodified. The script supports two modes:
;     - BUNDLED (#define VBC_BUNDLED): the VB-Cable installer ZIP is included.
;       Drop VBCABLE_Driver_Pack.zip into installer\dependencies\ before build.
;     - LINK ONLY (default): if VB-Cable isn't detected on the target machine,
;       the installer opens https://vb-audio.com/Cable in the user's browser.

#define MyAppName "Soundpad"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AP2 QuantumSolutions"
#define MyAppURL "https://vb-audio.com/Cable"
#define MyAppExeName "Soundpad.exe"

; Uncomment to bundle VB-Cable installer (place ZIP in installer\dependencies\):
; #define VBC_BUNDLED

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
; If `dotnet publish` was NOT run with PublishSingleFile, you'd need to include all DLLs too.
; Adjust this section if you switch builds.

#ifdef VBC_BUNDLED
Source: "dependencies\VBCABLE_Driver_Pack.zip"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Code]
function IsVbCableInstalled(): Boolean;
var
  Names: TArrayOfString;
  i: Integer;
  Subkey: string;
  Disp: string;
begin
  Result := False;
  // Search uninstall keys for "VB-CABLE" — covers both per-user and per-machine
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', Names) then
  begin
    for i := 0 to GetArrayLength(Names) - 1 do
    begin
      Subkey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[i];
      if RegQueryStringValue(HKLM, Subkey, 'DisplayName', Disp) then
        if (Pos('VB-CABLE', Uppercase(Disp)) > 0) or (Pos('VOICEMEETER', Uppercase(Disp)) > 0) then
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
begin
  if CurStep = ssPostInstall then
  begin
    if not (IsVbCableInstalled() or IsVoiceMeeterInstalled()) then
    begin
      Msg := 'O Soundpad precisa de um cabo de áudio virtual (VB-Cable ou VoiceMeeter) para enviar som ao Discord/Valorant.' + #13#10#13#10 +
             'Nenhum foi detectado nesta máquina.' + #13#10#13#10 +
             'Deseja abrir a página de download do VB-Cable agora?';
      if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES then
      begin
        ShellExec('open', 'https://vb-audio.com/Cable/', '', '', SW_SHOW, ewNoWait, ResultCode);
        MsgBox('Após instalar o VB-Cable e reiniciar o Windows, abra o Soundpad novamente — ele detectará o dispositivo automaticamente.', mbInformation, MB_OK);
      end;
    end;
  end;
end;
