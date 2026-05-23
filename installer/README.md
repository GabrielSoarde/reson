# Reson installer

Builds `dist/ResonSetup.exe` — a Windows installer that:

- Installs Reson to `Program Files\Reson`
- Detects VB-Cable / VoiceMeeter; if neither is installed, prompts the user to download VB-Cable
- Creates Start Menu and optional Desktop shortcuts
- Optional: starts Reson with Windows

> The Inno Setup script filename is still `Soundpad.iss` (matches the source `Soundpad.csproj` — internal naming kept stable to avoid churn). All user-visible strings inside the script say "Reson".

## Pre-requisites

1. **.NET 8 SDK** — to compile and publish Reson
2. **Inno Setup 6+** — to compile the installer. Free, download from <https://jrsoftware.org/isdl.php>

## Build

From the repo root in PowerShell:

```powershell
.\installer\build.ps1
```

This runs `dotnet publish` (self-contained single-file win-x64) then invokes ISCC. Output: `dist\ResonSetup.exe` (~180 MB — includes the .NET runtime).

## How VB-Cable is handled

The default script (`Soundpad.iss`) **does not bundle** VB-Cable's installer — it just checks the Windows registry for "VB-Cable" or "VoiceMeeter" under `HKLM\...\Uninstall`. If neither is found, it pops a dialog offering to open the VB-Cable download page in the browser. The user installs VB-Cable manually, reboots, and Reson picks it up on next launch.

### Optional: bundle the VB-Cable installer

VB-Cable is donationware; for personal/private use redistribution of the unmodified installer is allowed.

To bundle:

1. Download `VBCABLE_Driver_Pack.zip` from <https://vb-audio.com/Cable/>
2. Place it at `installer\dependencies\VBCABLE_Driver_Pack.zip`
3. In `Soundpad.iss`, uncomment `#define VBC_BUNDLED`
4. Rebuild

The current script does not auto-extract+install the ZIP (you'd need to add an `[Run]` line that extracts and runs `VBCABLE_Setup_x64.exe` silently with `/S`). Implement this only if you want a fully offline installer.

## Publishing a release (auto-update)

`publish-release.ps1` is the one-command release tool. It bumps the version everywhere, builds the desktop installer + Android APK, tags the commit, and creates a GitHub Release with both artifacts attached. Clients on older versions auto-update on next launch (desktop and Android both poll `github.com/GabrielSoarde/reson/releases/latest`).

```powershell
.\installer\publish-release.ps1 -Version 1.0.1 -Notes "Volume individual + boards"
```

What it does:
1. Bumps the version in `AppVersion.cs`, `Soundpad.csproj`, `Soundpad.iss`, and `pubspec.yaml` (and increments the Android build number)
2. Commits `release: X.Y.Z` and tags `vX.Y.Z`
3. Builds `dist\ResonSetup.exe` (via `build.ps1`) and `dist\Reson-Android.apk`
4. Pushes `master` + the tag
5. `gh release create vX.Y.Z` with both assets

Pre-reqs: .NET 8 SDK, Inno Setup 6+, Flutter, `gh` authenticated as the repo owner, and a clean working tree.

The asset names are fixed — the updaters look for `ResonSetup.exe` and `Reson-Android.apk` exactly. Don't rename them.

## Sounds folder

The script ships an initial `sounds/` folder with whatever was in the publish output. Files use `onlyifdoesntexist` so re-installing doesn't clobber user's existing sounds.

After install, the user's `sounds/` folder lives at `%LOCALAPPDATA%\Reson\sounds\`. They drop new MP3s there and the app auto-scans on next boot.

## Test the installer

After build:

```powershell
.\dist\ResonSetup.exe
```

Walk through the wizard, confirm install, click "Iniciar o Reson" at the end. The tray icon and WPF window should appear.
