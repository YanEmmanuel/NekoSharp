namespace NekoSharp.Core.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Details { get; init; }
    
    public string TimestampStr => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelStr => Level.ToString().ToUpper();
}

public class LogService
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();

    public event Action<LogEntry>? OnLogAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList().AsReadOnly(); }
    }

    public void Log(LogLevel level, string message, string? details = null)
    {
        var entry = new LogEntry { Level = level, Message = message, Details = details };
        lock (_lock) _entries.Add(entry);
        OnLogAdded?.Invoke(entry);
    }

    public void Debug(string message, string? details = null) => Log(LogLevel.Debug, message, details);
    public void Info(string message, string? details = null) => Log(LogLevel.Info, message, details);
    public void Warn(string message, string? details = null) => Log(LogLevel.Warning, message, details);
    public void Error(string message, string? details = null) => Log(LogLevel.Error, message, details);

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
