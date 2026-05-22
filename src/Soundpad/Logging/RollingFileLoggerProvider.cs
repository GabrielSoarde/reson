using Microsoft.Extensions.Logging;

namespace Soundpad.Logging;

public class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private DateTime _currentDate;
    private int _currentIndex;
    private StreamWriter? _writer;

    public RollingFileLoggerProvider(string dir, long maxBytesPerFile, int retentionDays)
    {
        _dir = dir;
        _maxBytes = maxBytesPerFile;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_dir);
        PurgeOld();
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            Rotate();
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    private void Rotate()
    {
        var today = DateTime.UtcNow.Date;
        if (_writer is null || _currentDate != today)
        {
            // Resume the highest existing index for today (if any) so restarts do not overwrite earlier files.
            var startIndex = FindNextIndexFor(today);
            OpenNew(today, startIndex);
            return;
        }
        if (_writer.BaseStream.Length >= _maxBytes)
        {
            OpenNew(today, _currentIndex + 1);
        }
    }

    private int FindNextIndexFor(DateTime date)
    {
        var prefix = $"soundpad-{date:yyyy-MM-dd}-";
        int maxIndex = 0;
        foreach (var f in Directory.EnumerateFiles(_dir, prefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var idxPart = name.Substring(prefix.Length);
            if (int.TryParse(idxPart, out var idx) && idx > maxIndex) maxIndex = idx;
        }
        if (maxIndex == 0) return 1;
        // If the latest existing file already exceeds the cap, start a new one; else append to it.
        var latestPath = Path.Combine(_dir, $"soundpad-{date:yyyy-MM-dd}-{maxIndex}.log");
        try
        {
            var info = new FileInfo(latestPath);
            if (info.Exists && info.Length >= _maxBytes) return maxIndex + 1;
        }
        catch { }
        return maxIndex;
    }

    private void OpenNew(DateTime date, int index)
    {
        _writer?.Dispose();
        _currentDate = date;
        _currentIndex = index;
        var path = Path.Combine(_dir, $"soundpad-{date:yyyy-MM-dd}-{index}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    private void PurgeOld()
    {
        if (!Directory.Exists(_dir)) return;
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var f in Directory.EnumerateFiles(_dir, "soundpad-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
            }
            catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal class RollingFileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(RollingFileLoggerProvider p, string c)
        {
            _provider = p;
            _category = c;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = $"{DateTime.UtcNow:O} [{level}] {_category}: {formatter(state, ex)}";
            if (ex is not null) line += "\n" + ex;
            _provider.Write(line);
        }

        private class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
