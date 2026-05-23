# Reson - one-command release publisher
#
# Bumps the version everywhere, builds the Velopack desktop release + Android
# APK, tags the commit, and publishes a GitHub Release.
#
#   - DESKTOP: `vpk upload github` publishes the Velopack release (the *.nupkg +
#     releases.win.json + RELEASES + ResonApp-win-Setup.exe). Velopack's
#     GithubSource update feed reads those assets, so installed clients pull the
#     delta in the background and apply it seamlessly on restart (no UAC).
#   - ANDROID: the APK is attached to the SAME release/tag via `gh release upload`
#     (the Android updater is unchanged — it downloads Reson-Android.apk).
#
# Usage (from repo root, PowerShell):
#   .\installer\publish-release.ps1 -Version 1.0.3 -Notes "Velopack auto-update"
#
# Requires: .NET 8 SDK, vpk CLI (`dotnet tool install -g vpk`), Flutter, and
# `gh` authenticated as the repo owner (GabrielSoarde). `vpk upload github` needs
# a GitHub token — we pass `gh auth token`.
#
# Fixed asset name the Android updater looks for:
#   Reson-Android.apk   <- Android updater downloads this
# (The desktop no longer downloads a named installer; Velopack's manifest drives
#  it. ResonSetup.exe / ResonApp-win-Setup.exe is only the FIRST-INSTALL artifact
#  for brand-new / migrating users.)

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

# --- 3. Build desktop Velopack release ------------------------------------

Write-Host "==> Building desktop Velopack release..." -ForegroundColor Cyan
& "$repoRoot\installer\build.ps1" -Version $Version
$releasesDir = "dist\velopack"
$setupExe = Join-Path $releasesDir "ResonApp-win-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "Velopack Setup.exe not produced ($setupExe)." }

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

# --- 5. Push commit + tag -------------------------------------------------

Write-Host "==> Pushing commit + tag..." -ForegroundColor Cyan
Invoke-Git push origin master
Invoke-Git push origin $tag

if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Reson $Version" }

# --- 6. Publish the Velopack release to GitHub Releases -------------------
# `vpk upload github` creates the release (tag v$Version) and attaches ALL the
# Velopack assets the GithubSource feed needs (*.nupkg, releases.win.json,
# RELEASES, ResonApp-win-Setup.exe). It needs a token — reuse the gh login.

Write-Host "==> Publishing Velopack release to GitHub..." -ForegroundColor Cyan
$ghToken = (gh auth token).Trim()
if ([string]::IsNullOrWhiteSpace($ghToken)) { throw "Could not get a GitHub token from 'gh auth token'." }

$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) {
    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $candidate) { $vpk = $candidate }
}
if (-not $vpk) { throw "vpk CLI not found. Install it with:  dotnet tool install -g vpk" }

# Match build.ps1: roll forward if the .NET 9 runtime vpk targets is absent.
$hasNet9 = (& dotnet --list-runtimes) -match 'Microsoft\.NETCore\.App 9\.'
if (-not $hasNet9) { $env:DOTNET_ROLL_FORWARD = 'Major' }

& $vpk upload github `
    --repoUrl "https://github.com/GabrielSoarde/reson" `
    --publish `
    --releaseName "Reson $Version" `
    --tag $tag `
    --outputDir $releasesDir `
    --token $ghToken
if ($LASTEXITCODE -ne 0) { throw "vpk upload github failed (exit $LASTEXITCODE)" }

# --- 7. Attach the Android APK to the same release ------------------------
Write-Host "==> Attaching Android APK to release $tag..." -ForegroundColor Cyan
gh release upload $tag $apkDist --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload (APK) failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Released Reson $Version" -ForegroundColor Green
Write-Host "    https://github.com/GabrielSoarde/reson/releases/tag/$tag"
Write-Host "    Velopack-installed desktop clients pull the delta in the background and"
Write-Host "    apply it seamlessly on restart. Android clients update via the APK."
