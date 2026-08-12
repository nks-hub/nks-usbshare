using System.Collections.Concurrent;

namespace NksUsbShare.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

/// <summary>
/// A small in-process log with a bounded history, so the web interface can show what happened
/// without anything having to be written to disk or a logging framework being pulled in.
/// </summary>
public sealed class ActivityLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;

    public ActivityLog(int capacity = 500) => _capacity = capacity;

    public event Action<LogEntry>? Entered;

    /// <summary>Set to true to also mirror entries to the console; the CLI turns this on.</summary>
    public bool EchoToConsole { get; set; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Warning(string category, string message) => Write(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Write(LogLevel.Error, category, message);

    public void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity) _entries.TryDequeue(out _);

        if (EchoToConsole)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Debug => ConsoleColor.DarkGray,
                _ => previous,
            };
            Console.WriteLine($"{entry.Timestamp:HH:mm:ss}  {category,-12} {message}");
            Console.ForegroundColor = previous;
        }

        Entered?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Recent(int count = 200)
        => _entries.Reverse().Take(count).Reverse().ToArray();
}
