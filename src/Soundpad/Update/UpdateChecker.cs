using System.Net;
using System.Text.Json;

namespace Soundpad.Update;

/// <summary>
/// Metadata describing a newer release found on GitHub.
/// </summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string ReleaseNotes);

/// <summary>
/// Lightweight auto-updater that polls the public GitHub Releases API and, if a
/// newer <c>ResonSetup.exe</c> is available, downloads it and re-runs it. The
/// installer's in-place upgrade (same Inno Setup AppId) handles replacing the
/// files and preserving <c>%LOCALAPPDATA%\Reson\</c> data.
///
/// <para>Network failures are intentionally swallowed: offline / GitHub down /
/// no releases published all collapse to "no update available" (null) so the
/// check can never crash or block app startup.</para>
/// </summary>
public sealed class UpdateChecker
{
    // GitHub's API requires a User-Agent header (403 without one) and the
    // /releases/latest endpoint returns 404 when there are zero published
    // (non-prerelease) releases.
    public const string ReleasesLatestUrl = "https://api.github.com/repos/GabrielSoarde/reson/releases/latest";
    public const string UserAgent = "Reson-Updater";
    public const string SetupAssetName = "ResonSetup.exe";

    private readonly HttpClient _http;
    private readonly string _currentVersion;

    public UpdateChecker(HttpClient? http = null, string? currentVersion = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _currentVersion = currentVersion ?? AppVersion.Current;
    }

    /// <summary>
    /// Determines whether <paramref name="latestTag"/> (e.g. "v1.0.1") is a newer
    /// version than <paramref name="currentVersion"/> (e.g. "1.0.0"). Strips a
    /// single leading "v" from each side and compares with <see cref="Version"/>
    /// semantics. Returns false if either side fails to parse (malformed → treat
    /// as "no update"), or if they're equal / older.
    /// </summary>
    public static bool IsNewer(string? latestTag, string? currentVersion)
    {
        if (!Version.TryParse(StripLeadingV(latestTag), out var latest)) return false;
        if (!Version.TryParse(StripLeadingV(currentVersion), out var current)) return false;
        return latest > current;
    }

    private static string? StripLeadingV(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag).Trim();
    }

    /// <summary>
    /// Queries GitHub for the latest release. Returns an <see cref="UpdateInfo"/>
    /// when a strictly-newer release with a <c>ResonSetup.exe</c> asset exists,
    /// otherwise null. Never throws — all network/parse errors and a 404 (no
    /// releases yet) map to null.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // 404 = no published (non-prerelease) releases yet → not an error.
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            if (!IsNewer(tag, _currentVersion)) return null;

            // Find the ResonSetup.exe asset's download URL.
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameEl) &&
                        string.Equals(nameEl.GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("browser_download_url", out var urlEl))
                    {
                        downloadUrl = urlEl.GetString();
                        break;
                    }
                }
            }

            // A newer tag without a usable installer asset is useless to us.
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var version = StripLeadingV(tag) ?? tag;

            return new UpdateInfo(version, downloadUrl, notes);
        }
        catch
        {
            // Offline, DNS failure, timeout, malformed JSON, cancellation — all
            // collapse to "no update". The updater must never crash the app.
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer to <c>%TEMP%\ResonSetup-&lt;version&gt;.exe</c> and
    /// returns its path. Reports 0..1 progress when the server sends a
    /// Content-Length. Throws on failure (the caller surfaces the error).
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), $"ResonSetup-{info.Version}.exe");

        using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        req.Headers.UserAgent.ParseAdd(UserAgent);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
                progress?.Report((double)read / total.Value);
        }
        progress?.Report(1.0);
        return dest;
    }

    /// <summary>
    /// Launches the downloaded installer and shuts the app down so the in-place
    /// upgrade can replace the running exe. Best-effort: if the launch fails the
    /// app keeps running (caller can surface the error).
    /// </summary>
    public void RunInstallerAndExit(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        });
        // The installer needs the running Reson.exe gone to overwrite files.
        // Environment.Exit tears down both the WPF UI thread and the Kestrel
        // app.Run() loop on the main thread cleanly enough for the upgrade.
        Environment.Exit(0);
    }
}
