# Soundpad installer

Builds `dist/SoundpadSetup.exe` — a Windows installer that:

- Installs Soundpad to `Program Files\Soundpad`
- Detects VB-Cable / VoiceMeeter; if neither is installed, prompts the user to download VB-Cable
- Creates Start Menu and optional Desktop shortcuts
- Optional: starts Soundpad with Windows

## Pre-requisites

1. **.NET 8 SDK** — to compile and publish Soundpad
2. **Inno Setup 6+** — to compile the installer. Free, download from <https://jrsoftware.org/isdl.php>

## Build

From the repo root in PowerShell:

```powershell
.\installer\build.ps1
```

This runs `dotnet publish` (self-contained single-file win-x64) then invokes ISCC. Output: `dist\SoundpadSetup.exe` (~180 MB — includes the .NET runtime).

## How VB-Cable is handled

The default script (`Soundpad.iss`) **does not bundle** VB-Cable's installer — it just checks the Windows registry for "VB-Cable" or "VoiceMeeter" under `HKLM\...\Uninstall`. If neither is found, it pops a dialog offering to open the VB-Cable download page in the browser. The user installs VB-Cable manually, reboots, and the Soundpad picks it up on next launch.

### Optional: bundle the VB-Cable installer

VB-Cable is donationware; for personal/private use redistribution of the unmodified installer is allowed.

To bundle:

1. Download `VBCABLE_Driver_Pack.zip` from <https://vb-audio.com/Cable/>
2. Place it at `installer\dependencies\VBCABLE_Driver_Pack.zip`
3. In `Soundpad.iss`, uncomment `#define VBC_BUNDLED`
4. Rebuild

The current script does not auto-extract+install the ZIP (you'd need to add an `[Run]` line that extracts and runs `VBCABLE_Setup_x64.exe` silently with `/S`). Implement this only if you want a fully offline installer.

## Sounds folder

The script ships an initial `sounds/` folder with whatever was in the publish output. Files use `onlyifdoesntexist` so re-installing doesn't clobber user's existing sounds.

After install, the user's `sounds/` folder lives at `Program Files\Soundpad\sounds\`. They drop new MP3s there and the app auto-scans on next boot.

## Test the installer

After build:

```powershell
.\dist\SoundpadSetup.exe
```

Walk through the wizard, confirm install, click "Iniciar o Soundpad" at the end. The tray icon and WPF window should appear.
