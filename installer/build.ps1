# Reson - Velopack build script
#
# Replaces the old Inno-Setup flow. Steps:
#   1. dotnet publish (self-contained FOLDER, win-x64) — NOT single-file
#      (Velopack works on a published folder; single-file is not supported).
#   2. Ensure vbcable/ + wwwroot/ + sounds/ are in the publish dir (the csproj
#      copies all three on publish).
#   3. vpk pack -> dist\velopack\ (ResonApp-win-Setup.exe + *.nupkg + manifests).
#   4. Copy ResonApp-win-Setup.exe -> dist\ResonSetup.exe for naming continuity.
# Output: dist\velopack\ (full Velopack release) + dist\ResonSetup.exe (alias).
#
# NOTE: csproj filename + namespace still say "Soundpad" (internal); the
# published assembly ships as Reson.exe via <AssemblyName>Reson</AssemblyName>.
# The Velopack PackId is "ResonApp" so app binaries land in
# %LocalAppData%\ResonApp\ and DON'T collide with user data at %LocalAppData%\Reson\.
#
# Requires:
#   - .NET 8 SDK (the app targets net8.0-windows)
#   - vpk CLI: `dotnet tool install -g vpk`  (Velopack CLI)
#
# vpk 0.0.1298 ships as a .NET 9 tool. If only .NET 8 + .NET 10 runtimes are
# installed (no .NET 9), this script sets DOTNET_ROLL_FORWARD=Major so vpk runs
# on the newer runtime. Installing the .NET 9 runtime removes the need for that.

param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# Derive version from AppVersion.cs when not passed explicitly.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $appVerText = [System.IO.File]::ReadAllText("src\Soundpad\AppVersion.cs")
    if ($appVerText -match 'Current\s*=\s*"(\d+\.\d+\.\d+)"') { $Version = $Matches[1] }
    else { throw "Could not parse version from src\Soundpad\AppVersion.cs" }
}
Write-Host "==> Building Reson (Velopack) version $Version" -ForegroundColor Cyan

# Kill any currently-running instance so the publish output isn't file-locked.
Write-Host "==> Stopping any running Reson.exe / Soundpad.exe..."
Get-Process -Name 'Reson','Soundpad' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    Killing PID $($_.Id) ($($_.ProcessName))"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}

# Extract VB-Cable bundle (if the ZIP is present) so it gets copied into the
# publish output by the csproj <None ... vbcable> item. Optional: without it the
# app's first-run VB-Cable dialog falls back to the download page.
$vbcZip = "installer\dependencies\VBCABLE_Driver_Pack.zip"
$vbcDir = "installer\dependencies\vbcable"
if (Test-Path $vbcZip) {
    if (-not (Test-Path "$vbcDir\VBCABLE_Setup_x64.exe")) {
        Write-Host "==> Extracting VB-Cable bundle..."
        if (Test-Path $vbcDir) { Remove-Item -Recurse -Force $vbcDir }
        Expand-Archive -LiteralPath $vbcZip -DestinationPath $vbcDir
    }
    if (-not (Test-Path "$vbcDir\VBCABLE_Setup_x64.exe")) {
        throw "VBCABLE_Setup_x64.exe not found inside $vbcZip"
    }
    Write-Host "    VB-Cable bundle ready at: $vbcDir"
} elseif (Test-Path "$vbcDir\VBCABLE_Setup_x64.exe") {
    Write-Host "==> Using already-extracted VB-Cable bundle at $vbcDir"
} else {
    Write-Host "==> No VB-Cable bundle - app first-run will use download-page fallback" -ForegroundColor Yellow
}

# --- 1. Publish (self-contained FOLDER, NOT single-file) ------------------
$publishDir = "dist\publish"
Write-Host "==> Publishing Reson (self-contained folder, win-x64)..."
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish src\Soundpad -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path "$publishDir\Reson.exe")) { throw "Publish output missing Reson.exe" }
Write-Host "    Published to: $publishDir"

# Sanity-check the bundled content the app expects at runtime.
foreach ($d in @('wwwroot','sounds')) {
    if (-not (Test-Path "$publishDir\$d")) { throw "Publish output missing $d\" }
}
if (Test-Path "$publishDir\vbcable\VBCABLE_Setup_x64.exe") {
    Write-Host "    vbcable\ bundled (first-run VB-Cable install enabled)"
} else {
    Write-Host "    vbcable\ NOT bundled (first-run will use download-page fallback)" -ForegroundColor Yellow
}

# --- 2. Locate vpk --------------------------------------------------------
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) {
    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $candidate) { $vpk = $candidate }
}
if (-not $vpk) {
    throw "vpk CLI not found. Install it with:  dotnet tool install -g vpk"
}
Write-Host "==> Using vpk: $vpk"

# vpk 0.0.1298 targets .NET 9. If that runtime is absent, roll forward.
$hasNet9 = (& dotnet --list-runtimes) -match 'Microsoft\.NETCore\.App 9\.'
if (-not $hasNet9) {
    Write-Host "    .NET 9 runtime not found - setting DOTNET_ROLL_FORWARD=Major for vpk"
    $env:DOTNET_ROLL_FORWARD = 'Major'
}

# --- 3. vpk pack ----------------------------------------------------------
$releasesDir = "dist\velopack"
if (-not (Test-Path "dist")) { New-Item -ItemType Directory -Path "dist" | Out-Null }
Write-Host "==> Packing Velopack release..."
& $vpk pack `
    --packId ResonApp `
    --packTitle Reson `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Reson.exe `
    --icon "assets\Reson.ico" `
    --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

$setupSrc = Join-Path $releasesDir "ResonApp-win-Setup.exe"
if (-not (Test-Path $setupSrc)) { throw "vpk pack did not produce $setupSrc" }

# --- 4. Alias the Setup.exe to dist\ResonSetup.exe for continuity ---------
$setupAlias = "dist\ResonSetup.exe"
Copy-Item $setupSrc $setupAlias -Force

$sizeMb = [math]::Round((Get-Item $setupSrc).Length / 1MB, 1)
Write-Host ""
Write-Host "==> Done. Velopack release in: $releasesDir ($sizeMb MB Setup.exe)" -ForegroundColor Green
Write-Host "    First-install artifact: $setupSrc"
Write-Host "    Alias for continuity:   $setupAlias"
Write-Host "    Update feed assets:     ResonApp-*-full.nupkg + releases.win.json + RELEASES"
