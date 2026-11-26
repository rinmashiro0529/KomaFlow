namespace MangaGui.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Represents a single UI event log row. Uses reference equality (class instead of record)
/// so identical log lines emitted within the same clock tick never collide in WPF ItemsControl.
/// </summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; }
    public LogLevel Level { get; }
    public string Message { get; }

    public LogEntry(DateTimeOffset timestamp, LogLevel level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    public string FormattedLine => $"[{Timestamp:HH:mm:ss}] {Message}";
}
