namespace Soundpad;

/// <summary>
/// Single source of truth for the app's version. Compared against the latest
/// GitHub release tag by <see cref="Update.UpdateChecker"/> and mirrored in the
/// csproj <c>&lt;Version&gt;</c> (assembly version) and the Inno Setup
/// <c>MyAppVersion</c> so the installer, the assembly, and the update check all
/// agree.
///
/// <para>Keep this a simple parseable string literal — the publish/release
/// tooling reads it textually to derive the release tag (e.g. "v1.0.0").</para>
/// </summary>
public static class AppVersion
{
    public const string Current = "1.0.2";
}
