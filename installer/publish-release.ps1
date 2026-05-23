# Reson - one-command release publisher
#
# Bumps the version everywhere, builds the desktop installer + Android APK,
# tags the commit, and creates a GitHub Release with both artifacts attached.
# Everyone running an older version then auto-updates (desktop + Android both
# poll GitHub Releases on launch).
#
# Usage (from repo root, PowerShell):
#   .\installer\publish-release.ps1 -Version 1.0.1 -Notes "Volume individual + boards"
#
# Requires: .NET 8 SDK, Inno Setup 6+, Flutter, and `gh` authenticated as the
# repo owner (GabrielSoarde).
#
# Asset names are fixed (the updaters look for these exact names):
#   ResonSetup.exe      <- desktop updater downloads this
#   Reson-Android.apk   <- Android updater downloads this

param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# --- Helpers --------------------------------------------------------------

# UTF-8 (no BOM) read/write. PowerShell 5.1's Get-Content/Set-Content default to
# the ANSI codepage, which corrupts non-ASCII content (em-dashes, accents) in
# files like Soundpad.iss on a round-trip. Use .NET I/O to be unambiguous.
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
function Get-FileText([string]$p) { return [System.IO.File]::ReadAllText($p) }
function Set-FileText([string]$p, [string]$t) { [System.IO.File]::WriteAllText($p, $t, $Utf8NoBom) }

# Run git, ignoring its stderr warnings (e.g. "LF will be replaced by CRLF").
# Only a non-zero exit code is treated as failure.
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$Args)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git @Args 2>$null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { throw "git $($Args -join ' ') failed (exit $code)" }
}

# --- Validate -------------------------------------------------------------

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be X.Y.Z (e.g. 1.0.1). Got: $Version"
}
$tag = "v$Version"

Write-Host "==> Releasing Reson $Version (tag $tag)" -ForegroundColor Cyan
$ghUser = (gh api user --jq .login 2>$null)
Write-Host "    GitHub account: $ghUser"

if ((git status --porcelain 2>$null) -ne $null) {
    throw "Working tree is not clean. Commit or stash changes before releasing."
}
if (git tag --list $tag) { throw "Tag $tag already exists. Pick a new version." }

# --- 1. Bump version in all four source-of-truth files --------------------

Write-Host "==> Bumping version to $Version in source files..."

$appVer = "src\Soundpad\AppVersion.cs"
Set-FileText $appVer ((Get-FileText $appVer) -replace 'Current = "\d+\.\d+\.\d+"', "Current = `"$Version`"")

$csproj = "src\Soundpad\Soundpad.csproj"
Set-FileText $csproj ((Get-FileText $csproj) -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>")

$iss = "installer\Soundpad.iss"
Set-FileText $iss ((Get-FileText $iss) -replace '#define MyAppVersion "\d+\.\d+\.\d+"', "#define MyAppVersion `"$Version`"")

$pubspec = "mobile\reson_app\pubspec.yaml"
$pubContent = Get-FileText $pubspec
if ($pubContent -match 'version:\s*\d+\.\d+\.\d+\+(\d+)') { $build = [int]$Matches[1] + 1 } else { $build = 1 }
Set-FileText $pubspec ($pubContent -replace 'version:\s*\d+\.\d+\.\d+\+\d+', "version: $Version+$build")
Write-Host "    pubspec build number -> $build"

# --- 2. Commit + tag ------------------------------------------------------

Write-Host "==> Committing version bump + tagging $tag..."
Invoke-Git add $appVer $csproj $iss $pubspec
Invoke-Git commit -m "release: $Version"
Invoke-Git tag $tag

# --- 3. Build desktop installer -------------------------------------------

Write-Host "==> Building desktop installer..." -ForegroundColor Cyan
& "$repoRoot\installer\build.ps1"
$setupExe = "dist\ResonSetup.exe"
if (-not (Test-Path $setupExe)) { throw "Desktop installer not produced." }

# --- 4. Build Android APK -------------------------------------------------

Write-Host "==> Building Android APK..." -ForegroundColor Cyan
Push-Location "mobile\reson_app"
# Flutter writes progress/warnings to stderr; under ErrorActionPreference=Stop
# that would abort the script even on a successful build. Drop to Continue and
# gate on the exit code instead.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& flutter build apk --release 2>&1 | Write-Host
$apkExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap
Pop-Location
if ($apkExit -ne 0) { throw "flutter build apk failed (exit $apkExit)" }
$apkSrc = "mobile\reson_app\build\app\outputs\flutter-apk\app-release.apk"
if (-not (Test-Path $apkSrc)) { throw "APK not produced." }
$apkDist = "dist\Reson-Android.apk"
Copy-Item $apkSrc $apkDist -Force

# --- 5. Push + create the GitHub Release ----------------------------------

Write-Host "==> Pushing and creating GitHub release..." -ForegroundColor Cyan
Invoke-Git push origin master
Invoke-Git push origin $tag

if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Reson $Version" }
gh release create $tag $setupExe $apkDist --title "Reson $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Released Reson $Version" -ForegroundColor Green
Write-Host "    https://github.com/GabrielSoarde/reson/releases/tag/$tag"
Write-Host "    Desktop + Android clients on older versions will be prompted to update on next launch."
