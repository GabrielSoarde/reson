using FluentAssertions;
using Soundpad.Audio;
using Soundpad.Update;

namespace Soundpad.Tests.Update;

/// <summary>
/// Smoke tests for the Velopack-backed updater. The test host is not a
/// Velopack-installed app, so the updater must report "not installed" and a
/// check must collapse to "no update" without throwing — never crashing the
/// app's fire-and-forget startup check.
/// </summary>
public class VelopackUpdaterTests
{
    [Fact]
    public void Ctor_DoesNotThrow()
    {
        var act = () => new VelopackUpdater();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsInstalled_IsFalse_WhenNotAVelopackApp()
    {
        // The test runner / dotnet host has no Velopack metadata, so the
        // updater must treat it as a non-managed install.
        new VelopackUpdater().IsInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_AndDoesNotThrow_WhenNotInstalled()
    {
        var info = await new VelopackUpdater().CheckAsync();
        info.Should().BeNull();
    }

    [Fact]
    public async Task CheckDownloadAndApplyAsync_ReturnsFalse_WhenNotInstalled()
    {
        var applied = await new VelopackUpdater().CheckDownloadAndApplyAsync();
        applied.Should().BeFalse();
    }

    [Fact]
    public void VbCableInstaller_CanInstallBundled_ReflectsBundlePresence()
    {
        // No vbcable\ folder ships next to the test assembly, so the bundled
        // installer must report unavailable and BundledSetupPath must be null.
        VbCableInstaller.CanInstallBundled.Should().Be(VbCableInstaller.BundledSetupPath is not null);
        VbCableInstaller.BundledSetupPath.Should().BeNull();
    }

    [Fact]
    public void VbCableInstaller_RunBundledInstaller_ReturnsFalse_WhenNoBundle()
    {
        // With no bundled installer present, running it is a safe no-op false.
        VbCableInstaller.RunBundledInstaller().Should().BeFalse();
    }
}
