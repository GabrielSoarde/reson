using System.Diagnostics;

namespace Soundpad.Audio;

/// <summary>
/// First-run VB-Cable driver installer. Velopack replaced the Inno installer
/// (which used to silent-install VB-Cable from its <c>[Code] CurStepChanged</c>
/// hook), so the one operation that genuinely needs admin — installing the
/// virtual-audio-cable <b>driver</b> — moved into the app's first run.
///
/// <para>The app itself installs and updates per-user with no elevation. Only
/// this single driver install elevates, once, via the UAC <c>runas</c> verb.</para>
///
/// <para>The VB-Cable installer + its sibling .inf/.sys/.cat driver files are
/// bundled next to <c>Reson.exe</c> in a <c>vbcable\</c> folder (see the
/// <c>&lt;None ... vbcable&gt;</c> item in Soundpad.csproj). If that folder is
/// absent (the driver ZIP wasn't bundled at pack time) <see cref="BundledSetupPath"/>
/// returns null and callers fall back to the VB-Cable download page.</para>
/// </summary>
public static class VbCableInstaller
{
    public const string DownloadPageUrl = "https://vb-audio.com/Cable/";

    /// <summary>
    /// Full path to the bundled <c>VBCABLE_Setup_x64.exe</c> next to Reson.exe,
    /// or null when it isn't bundled in this build.
    /// </summary>
    public static string? BundledSetupPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "vbcable", "VBCABLE_Setup_x64.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>True when the bundled VB-Cable installer is available to run.</summary>
    public static bool CanInstallBundled => BundledSetupPath is not null;

    /// <summary>
    /// Launches the bundled VB-Cable installer elevated (UAC prompt) with the
    /// silent install flags VB-Audio documents (<c>-i</c> install, <c>-h</c>
    /// headless). Returns true if the installer process started and exited with
    /// code 0. Throws nothing — any failure (no bundle, user declined UAC,
    /// non-zero exit) returns false.
    /// </summary>
    public static bool RunBundledInstaller()
    {
        var exe = BundledSetupPath;
        if (exe is null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-i -h",
                // The driver install needs admin; runas + UseShellExecute is what
                // triggers the single UAC consent prompt. The Windows
                // driver-signing confirmation may also appear — that's a Windows
                // behavior we can't suppress, but the user sees it only once.
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            // User cancelled the UAC prompt (Win32Exception 1223) or the launch
            // failed — treat as "not installed", caller can offer the download page.
            return false;
        }
    }

    /// <summary>Opens the VB-Cable download page in the default browser. Best-effort.</summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = DownloadPageUrl, UseShellExecute = true });
        }
        catch { /* user can navigate manually */ }
    }
}
