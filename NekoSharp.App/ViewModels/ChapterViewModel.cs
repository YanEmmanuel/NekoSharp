using CommunityToolkit.Mvvm.ComponentModel;
using NekoSharp.Core.Models;

namespace NekoSharp.App.ViewModels;

public partial class ChapterViewModel : ObservableObject
{
    public Chapter Chapter { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _status = "Pending";

    [ObservableProperty]
    private ChapterDownloadStatus _downloadStatus = ChapterDownloadStatus.Queued;

    [ObservableProperty]
    private string _progressText = string.Empty;

    public string DisplayTitle => string.IsNullOrEmpty(Chapter.Title)
        ? $"Chapter {Chapter.Number}"
        : $"Ch. {Chapter.Number} — {Chapter.Title}";

    public ChapterViewModel(Chapter chapter)
    {
        Chapter = chapter;
    }

    public void UpdateProgress(int current, int total)
    {
        DownloadProgress = total > 0 ? (double)current / total * 100 : 0;
        ProgressText = $"{current}/{total}";
    }

    public void SetDownloading()
    {
        DownloadStatus = ChapterDownloadStatus.Downloading;
        Status = "Downloading...";
    }

    public void SetCompleted()
    {
        DownloadStatus = ChapterDownloadStatus.Completed;
        Status = "Done ✓";
        DownloadProgress = 100;
    }

    public void SetFailed(string error)
    {
        DownloadStatus = ChapterDownloadStatus.Failed;
        Status = $"Failed: {error}";
    }

    public void Reset()
    {
        DownloadStatus = ChapterDownloadStatus.Queued;
        Status = "Pending";
        DownloadProgress = 0;
        ProgressText = string.Empty;
    }
}
