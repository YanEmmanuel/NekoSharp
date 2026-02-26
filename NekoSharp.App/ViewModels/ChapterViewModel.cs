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
    private string _status = "Pendente";

    [ObservableProperty]
    private ChapterDownloadStatus _downloadStatus = ChapterDownloadStatus.Queued;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _isStitching;

    public string DisplayTitle => string.IsNullOrEmpty(Chapter.Title)
        ? $"Capítulo {Chapter.Number}"
        : $"Capítulo {Chapter.Number} — {Chapter.Title}";

    public ChapterViewModel(Chapter chapter)
    {
        Chapter = chapter;
    }

    public void UpdateProgress(DownloadProgress progress)
    {
        if (progress.IsStitching)
        {
            IsStitching = true;
            DownloadStatus = ChapterDownloadStatus.Stitching;
            DownloadProgress = 100;
            ProgressText = progress.StitchingStatus;
            Status = progress.StitchingStatus;
        }
        else
        {
            IsStitching = false;
            DownloadStatus = ChapterDownloadStatus.Downloading;
            DownloadProgress = progress.TotalPages > 0 ? (double)progress.CurrentPage / progress.TotalPages * 100 : 0;
            ProgressText = $"{progress.CurrentPage}/{progress.TotalPages}";
            Status = $"Baixando... {ProgressText}";
        }
    }

    public void SetDownloading()
    {
        DownloadStatus = ChapterDownloadStatus.Downloading;
        Status = "Baixando...";
        IsStitching = false;
    }

    public void SetCompleted()
    {
        DownloadStatus = ChapterDownloadStatus.Completed;
        Status = "Concluído ✓";
        DownloadProgress = 100;
        IsStitching = false;
    }

    public void SetFailed(string error)
    {
        DownloadStatus = ChapterDownloadStatus.Failed;
        Status = $"Falhou: {error}";
        IsStitching = false;
    }

    public void Reset()
    {
        DownloadStatus = ChapterDownloadStatus.Queued;
        Status = "Pendente";
        DownloadProgress = 0;
        ProgressText = string.Empty;
        IsStitching = false;
    }
}
