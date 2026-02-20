using NekoSharp.Core.Services;

namespace NekoSharp.App.ViewModels;

public class LogEntryViewModel
{
    public LogEntry Entry { get; }

    public string Timestamp => Entry.TimestampStr;
    public string Level => Entry.LevelStr;
    public string Message => Entry.Message;
    public string? Details => Entry.Details;
    public bool HasDetails => !string.IsNullOrEmpty(Entry.Details);

    public string LevelColor => Entry.Level switch
    {
        LogLevel.Debug => "#808890",
        LogLevel.Info => "#58A6FF",
        LogLevel.Warning => "#D29922",
        LogLevel.Error => "#F85149",
        _ => "#B0B0C0"
    };

    public LogEntryViewModel(LogEntry entry)
    {
        Entry = entry;
    }
}
