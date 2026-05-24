namespace Soundpad;

/// <summary>
/// Single source of truth for the app's version, mirrored in the csproj
/// <c>&lt;Version&gt;</c> (assembly version) and used as the <c>--packVersion</c>
/// for <c>vpk pack</c> so the assembly and the published Velopack release agree.
///
/// <para>Velopack itself owns version comparison at update time (it reads the
/// release manifest), so this constant is no longer consumed by the updater —
/// but the build/publish tooling reads it textually to derive the release tag
/// (e.g. "v1.0.2"), so keep it a simple parseable string literal.</para>
/// </summary>
public static class AppVersion
{
    public const string Current = "1.1.1";
}
