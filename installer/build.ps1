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
  $size = (Get-Item $setupExe).Length / 1MB
  Write-Host ""
  Write-Host "==> Done. Installer: $setupExe ({0:N1} MB)" -f $size
} else {
  throw "Installer not generated"
}
