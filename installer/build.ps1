# Soundpad — build script
#
# Steps:
#   1. dotnet publish (self-contained single-file Win x64)
#   2. Compile Soundpad.iss with Inno Setup ISCC
# Output: dist\SoundpadSetup.exe
#
# Requires:
#   - .NET 8 SDK
#   - Inno Setup 6+ installed (https://jrsoftware.org/isdl.php)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

Write-Host "==> Stopping any running Soundpad.exe (avoids file lock during publish)..."
Get-Process -Name 'Soundpad' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    Killing PID $($_.Id)"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

Write-Host "==> Cleaning previous publish output..."
Remove-Item -Recurse -Force "src\Soundpad\bin\Release\net8.0-windows\win-x64\publish" -ErrorAction SilentlyContinue

Write-Host "==> Publishing Soundpad (self-contained, single file, win-x64)..."
dotnet publish src\Soundpad -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishDir = "src\Soundpad\bin\Release\net8.0-windows\win-x64\publish"
if (-not (Test-Path "$publishDir\Soundpad.exe")) {
  throw "Publish output not found at $publishDir\Soundpad.exe"
}
Write-Host "    Published to: $publishDir"

# Extract VB-Cable bundle if present so Inno Setup can include the .exe directly.
# (Skipping if the ZIP isn't there is fine — Soundpad.iss falls back to the
# "open download page" path it already had.)
$vbcZip = "installer\dependencies\VBCABLE_Driver_Pack.zip"
$vbcDir = "installer\dependencies\vbcable"
if (Test-Path $vbcZip) {
  Write-Host "==> Extracting VB-Cable bundle..."
  if (Test-Path $vbcDir) { Remove-Item -Recurse -Force $vbcDir }
  Expand-Archive -LiteralPath $vbcZip -DestinationPath $vbcDir
  if (-not (Test-Path "$vbcDir\VBCABLE_Setup_x64.exe")) {
    throw "VBCABLE_Setup_x64.exe not found inside $vbcZip"
  }
  Write-Host "    Extracted to: $vbcDir"
} else {
  Write-Host "==> VB-Cable ZIP not found at $vbcZip — installer will use download-page fallback"
  if (Test-Path $vbcDir) { Remove-Item -Recurse -Force $vbcDir }
}

# Locate Inno Setup compiler
$isccCandidates = @(
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  Write-Error "Inno Setup nao encontrado. Baixe em https://jrsoftware.org/isdl.php"
  exit 1
}
Write-Host "==> Using Inno Setup: $iscc"

Write-Host "==> Compiling installer..."
& $iscc "installer\Soundpad.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }

$setupExe = "dist\SoundpadSetup.exe"
if (Test-Path $setupExe) {
  $sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
  Write-Host ""
  Write-Host "==> Done. Installer: $setupExe ($sizeMb MB)"
} else {
  throw "Installer not generated"
}
