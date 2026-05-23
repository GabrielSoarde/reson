using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Covers the FileSystemWatcher-driven auto-refresh of the sounds/ folder.
/// Uses a short (50ms) debounce window so we don't sit in a long Task.Delay
/// per test — the production window is 500ms.
/// </summary>
public class SoundsFolderWatcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-fsw-" + Guid.NewGuid());
    private readonly string _soundsDir;
    private SoundsFolderWatcher? _watcher;

    public SoundsFolderWatcherTests()
    {
        Directory.CreateDirectory(_tempDir);
        _soundsDir = Path.Combine(_tempDir, "sounds");
        Directory.CreateDirectory(_soundsDir);
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Drop_Mp3_Fires_Refresh_Without_Importing()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        var changedCount = 0;
        lib.Changed += () => Interlocked.Increment(ref changedCount);

        _watcher = new SoundsFolderWatcher(lib, _soundsDir, TimeSpan.FromMilliseconds(50), a => a());
        _watcher.Start();

        File.WriteAllBytes(Path.Combine(_soundsDir, "hello.mp3"), new byte[10]);

        await WaitUntil(() => changedCount > 0, TimeSpan.FromSeconds(2));

        // The watcher refreshes the UI (so a re-appearing file clears its
        // "missing" flag) but never auto-imports — sounds are added explicitly
        // via Upload, not by dropping files in the folder. Re-importing would
        // resurrect sounds the user intentionally removed from the board.
        changedCount.Should().BeGreaterThan(0);
        lib.ActiveBoard.Sounds.Should().NotContain(s => s.File == "hello.mp3");
    }

    [Fact]
    public async Task NonWhitelisted_Extension_Is_Ignored()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        int changes = 0;
        lib.Changed += () => Interlocked.Increment(ref changes);

        _watcher = new SoundsFolderWatcher(lib, _soundsDir, TimeSpan.FromMilliseconds(50), a => a());
        _watcher.Start();

        File.WriteAllText(Path.Combine(_soundsDir, "notes.txt"), "hi");
        File.WriteAllText(Path.Combine(_soundsDir, "blob.bin"), "x");

        // Give the FSW + debounce a generous window — nothing should fire.
        await Task.Delay(400);
        changes.Should().Be(0);
        lib.ActiveBoard.Sounds.Should().BeEmpty();
    }

    [Fact]
    public async Task Burst_Of_Files_Triggers_Single_Refresh()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        int changes = 0;
        lib.Changed += () => Interlocked.Increment(ref changes);

        _watcher = new SoundsFolderWatcher(lib, _soundsDir, TimeSpan.FromMilliseconds(100), a => a());
        _watcher.Start();

        // Drop 5 files in quick succession — the debounce coalesces them.
        for (int i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(_soundsDir, $"s{i}.mp3"), new byte[10]);

        await WaitUntil(() => changes > 0, TimeSpan.FromSeconds(2));
        await Task.Delay(250); // let any straggler flush settle

        // Files are not auto-imported; the board stays empty. The burst is
        // coalesced into a small number of refresh fires.
        lib.ActiveBoard.Sounds.Should().BeEmpty();
        changes.Should().BeLessOrEqualTo(2);
    }

    [Fact]
    public async Task Delete_Triggers_Notify_For_Ui_Refresh()
    {
        // Pre-seed a sound and load the library so it knows about it.
        File.WriteAllBytes(Path.Combine(_soundsDir, "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.ActiveBoard.Sounds.Should().HaveCount(1);

        int changesAfterStart = 0;
        // Wire Changed AFTER the watcher starts so we measure only delete-driven fires.
        _watcher = new SoundsFolderWatcher(lib, _soundsDir, TimeSpan.FromMilliseconds(50), a => a());
        _watcher.Start();
        lib.Changed += () => Interlocked.Increment(ref changesAfterStart);

        File.Delete(Path.Combine(_soundsDir, "x.mp3"));

        await WaitUntil(() => changesAfterStart > 0, TimeSpan.FromSeconds(2));
        changesAfterStart.Should().BeGreaterThan(0);
        // Entry stays in config (deletes don't auto-remove); runtime status
        // flips to "missing" — verified by GetRuntimeStatuses returning Missing=true.
        var statuses = lib.GetRuntimeStatuses();
        statuses.Should().ContainSingle().Which.Missing.Should().BeTrue();
    }

    [Fact]
    public void Dispose_Stops_Receiving_Events()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        int changes = 0;
        lib.Changed += () => Interlocked.Increment(ref changes);

        var watcher = new SoundsFolderWatcher(lib, _soundsDir, TimeSpan.FromMilliseconds(50), a => a());
        watcher.Start();
        watcher.Dispose();

        File.WriteAllBytes(Path.Combine(_soundsDir, "after-dispose.mp3"), new byte[10]);
        // Give FSW a chance — but it's been disposed so no flush should run.
        Thread.Sleep(200);
        changes.Should().Be(0);
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
