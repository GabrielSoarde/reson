using Velopack;
using Velopack.Sources;

namespace Soundpad.Update;

/// <summary>
/// Seamless auto-updater backed by <see cref="Velopack"/>. Replaces the old
/// download-the-Inno-installer-and-re-run-it flow with Velopack's delta-based,
/// no-UAC, per-user update mechanism (the same approach Discord / Spotify use).
///
/// <para>The release feed is the public GitHub Releases of
/// <c>GabrielSoarde/reson</c>. <c>vpk upload github</c> attaches the Velopack
/// assets (<c>*.nupkg</c> + <c>releases.win.json</c> + <c>*-Setup.exe</c>) that
/// <see cref="GithubSource"/> reads.</para>
///
/// <para><b>Not-a-Velopack-app is not an error.</b> Velopack's
/// <see cref="UpdateManager"/> throws when the running exe wasn't installed via
/// Velopack — e.g. during <c>dotnet run</c>, in CI, or for users still on the
/// old Inno install. We catch that and treat it as "no update available" so the
/// background check can never crash the app. All network failures collapse the
/// same way.</para>
/// </summary>
public sealed class VelopackUpdater
{
    // Public GitHub repo that hosts the Velopack release feed.
    public const string RepoUrl = "https://github.com/GabrielSoarde/reson";

    private readonly UpdateManager _mgr;

    public VelopackUpdater()
    {
        // GithubSource: prerelease=false (only stable releases), no auth token
        // needed for a public repo's release assets.
        _mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// True when this process is actually running as a Velopack-installed app
    /// (i.e. updates are possible). False under <c>dotnet run</c>, the old Inno
    /// install, or any non-Velopack launch — callers use this to decide whether
    /// to bother checking / what message to show.
    /// </summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>
    /// Polls GitHub for a newer release. Returns the <see cref="UpdateInfo"/>
    /// Velopack will apply, or null when up to date / not a Velopack app /
    /// offline. Never throws.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            if (!_mgr.IsInstalled) return null;
            return await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch
        {
            // Not-a-Velopack-app, offline, GitHub down, no releases — all
            // collapse to "no update". The check must never crash the app.
            return null;
        }
    }

    /// <summary>
    /// Downloads the (delta) update package in the background. Reports 0..100
    /// progress. Safe to await before prompting the user to restart so the apply
    /// step is instant. Throws on a genuine download failure (caller surfaces it).
    /// </summary>
    public Task DownloadAsync(UpdateInfo info, Action<int>? progress = null)
        => _mgr.DownloadUpdatesAsync(info, progress);

    /// <summary>
    /// Applies the previously-downloaded update and relaunches Reson. This
    /// terminates the current process — it does not return on success. No UAC,
    /// no wizard: Velopack swaps <c>current\</c> for the staged version and
    /// restarts the exe.
    /// </summary>
    public void ApplyAndRestart(UpdateInfo info)
        => _mgr.ApplyUpdatesAndRestart(info);

    /// <summary>
    /// One-shot convenience for the silent startup path: check → download →
    /// apply+restart, no prompts. Returns false (and does nothing) when there's
    /// no update or this isn't a Velopack install. Never throws.
    /// </summary>
    public async Task<bool> CheckDownloadAndApplyAsync()
    {
        try
        {
            var info = await CheckAsync().ConfigureAwait(false);
            if (info is null) return false;
            await DownloadAsync(info).ConfigureAwait(false);
            ApplyAndRestart(info); // does not return on success
            return true;
        }
        catch
        {
            return false;
        }
    }
}
