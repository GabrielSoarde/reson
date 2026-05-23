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

# --- Validate version format (X.Y.Z) ---
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be X.Y.Z (e.g. 1.0.1). Got: $Version"
}
$tag = "v$Version"

Write-Host "==> Releasing Reson $Version (tag $tag)" -ForegroundColor Cyan

# --- Ensure gh is the personal account + clean tree ---
$ghUser = (gh api user --jq .login 2>$null)
Write-Host "    GitHub account: $ghUser"

if ((git status --porcelain) -ne $null) {
    throw "Working tree is not clean. Commit or stash changes before releasing."
}

# --- Refuse to reuse an existing tag ---
$existing = git tag --list $tag
if ($existing) { throw "Tag $tag already exists. Pick a new version." }

# --- 1. Bump version in all four source-of-truth files ---
Write-Host "==> Bumping version to $Version in source files..."

# AppVersion.cs
$appVer = "src\Soundpad\AppVersion.cs"
(Get-Content $appVer -Raw) -replace 'Current = "\d+\.\d+\.\d+"', "Current = `"$Version`"" |
    Set-Content $appVer -Encoding utf8 -NoNewline

# Soundpad.csproj  <Version>X.Y.Z</Version>
$csproj = "src\Soundpad\Soundpad.csproj"
(Get-Content $csproj -Raw) -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>" |
    Set-Content $csproj -Encoding utf8 -NoNewline

# Inno Setup  #define MyAppVersion "X.Y.Z"
$iss = "installer\Soundpad.iss"
(Get-Content $iss -Raw) -replace '#define MyAppVersion "\d+\.\d+\.\d+"', "#define MyAppVersion `"$Version`"" |
    Set-Content $iss -Encoding utf8 -NoNewline

# pubspec.yaml  version: X.Y.Z+BUILD  (increment build number)
$pubspec = "mobile\reson_app\pubspec.yaml"
$pubContent = Get-Content $pubspec -Raw
if ($pubContent -match 'version:\s*\d+\.\d+\.\d+\+(\d+)') {
    $build = [int]$Matches[1] + 1
} else {
    $build = 1
}
$pubContent = $pubContent -replace 'version:\s*\d+\.\d+\.\d+\+\d+', "version: $Version+$build"
Set-Content $pubspec -Value $pubContent -Encoding utf8 -NoNewline
Write-Host "    pubspec build number -> $build"

# --- 2. Commit + tag ---
Write-Host "==> Committing version bump + tagging $tag..."
git add $appVer $csproj $iss $pubspec
git commit -m "release: $Version"
git tag $tag

# --- 3. Build desktop installer ---
Write-Host "==> Building desktop installer..." -ForegroundColor Cyan
& "$repoRoot\installer\build.ps1"
$setupExe = "dist\ResonSetup.exe"
if (-not (Test-Path $setupExe)) { throw "Desktop installer not produced." }

# --- 4. Build Android APK ---
Write-Host "==> Building Android APK..." -ForegroundColor Cyan
Push-Location "mobile\reson_app"
flutter build apk --release
Pop-Location
$apkSrc = "mobile\reson_app\build\app\outputs\flutter-apk\app-release.apk"
if (-not (Test-Path $apkSrc)) { throw "APK not produced." }
$apkDist = "dist\Reson-Android.apk"
Copy-Item $apkSrc $apkDist -Force

# --- 5. Push commit + tag, create the GitHub Release ---
Write-Host "==> Pushing and creating GitHub release..." -ForegroundColor Cyan
git push origin master
git push origin $tag

if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Reson $Version" }
gh release create $tag $setupExe $apkDist --title "Reson $Version" --notes $Notes

Write-Host ""
Write-Host "==> Released Reson $Version" -ForegroundColor Green
Write-Host "    https://github.com/GabrielSoarde/reson/releases/tag/$tag"
Write-Host "    Desktop + Android clients on older versions will be prompted to update on next launch."
