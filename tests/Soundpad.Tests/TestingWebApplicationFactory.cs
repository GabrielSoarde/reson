using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Soundpad.Tests;

/// <summary>
/// WebApplicationFactory that forces the "Testing" environment so Program.cs
/// skips the tray icon, Kestrel ListenAnyIP, and other process-level side
/// effects that would interfere with the in-memory test server.
///
/// Sets the ASPNETCORE_ENVIRONMENT env var at construction so Program.cs
/// sees "Testing" before any top-level statements run (UseEnvironment via the
/// IWebHostBuilder is applied too late for top-level side effects).
///
/// Also pins the content root to the SUT project directory so static files
/// (wwwroot/index.html) resolve correctly when ASPNETCORE_ENVIRONMENT changes.
/// </summary>
public class TestingWebApplicationFactory : WebApplicationFactory<Program>
{
    public TestingWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var contentRoot = ResolveSutContentRoot();
        if (contentRoot is not null) builder.UseContentRoot(contentRoot);
        base.ConfigureWebHost(builder);
    }

    private static string? ResolveSutContentRoot()
    {
        // Walk up from test bin dir until we find src/Soundpad/Soundpad.csproj
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Soundpad", "Soundpad.csproj");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "src", "Soundpad");
            dir = dir.Parent;
        }
        return null;
    }
}
