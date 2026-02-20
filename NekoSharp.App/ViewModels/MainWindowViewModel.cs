using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ScraperManager _scraperManager;
    private readonly IDownloadService _downloadService;
    private readonly LogService _logService;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _cts;

     
    [ObservableProperty] private string _mangaUrl = string.Empty;
    [ObservableProperty] private Manga? _manga;
    [ObservableProperty] private string _mangaName = string.Empty;
    [ObservableProperty] private string _mangaDescription = string.Empty;
    [ObservableProperty] private string _mangaCoverUrl = string.Empty;
    [ObservableProperty] private string _mangaSite = string.Empty;

     
    [ObservableProperty] private bool _isMangaLoaded;
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isErrorState;

     
    [ObservableProperty] private string _statusMessage = "Cole a URL de um mangá e clique em Fetch para começar.";
    [ObservableProperty] private string _statusType = "info";

     
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _overallProgressText = string.Empty;
    [ObservableProperty] private int _totalChapters;
    [ObservableProperty] private int _completedChapters;

     
    [ObservableProperty] private DownloadFormat _downloadFormat = DownloadFormat.FolderImages;
    [ObservableProperty] private ImageFormat _selectedImageFormat = ImageFormat.Original;

     
    [ObservableProperty] private bool _isLogPanelOpen;
    [ObservableProperty] private int _logCount;

     
    [ObservableProperty] private string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MangaDownloads");

    public ObservableCollection<ChapterViewModel> Chapters { get; } = [];
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public string SupportedSites => string.Join(", ", _scraperManager.Scrapers.Select(s => s.Name));
    public IReadOnlyList<string> ProviderNames => _scraperManager.Scrapers.Select(s => s.Name).ToList();

    public MainWindowViewModel(ScraperManager scraperManager, IDownloadService downloadService, LogService logService, ISettingsStore settingsStore)
    {
        _scraperManager = scraperManager;
        _downloadService = downloadService;
        _logService = logService;
        _settingsStore = settingsStore;

        LoadSettings();

        _logService.OnLogAdded += entry =>
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                LogEntries.Add(new LogEntryViewModel(entry));
                LogCount = LogEntries.Count;

                 
                while (LogEntries.Count > 500)
                    LogEntries.RemoveAt(0);

                return false;  
            });
        };
    }

    private void LoadSettings()
    {
        Task.Run(new Func<Task>(async () =>
        {
            var df = await _settingsStore.GetEnumAsync("Download.Format", DownloadFormat.FolderImages);
            var imfmt = await _settingsStore.GetEnumAsync("Download.ImageFormat", ImageFormat.Original);
            var outDir = await _settingsStore.GetStringAsync("Download.OutputDirectory");

            GLib.Functions.IdleAdd(0, () =>
            {
                DownloadFormat = df;
                SelectedImageFormat = imfmt;
                if (!string.IsNullOrEmpty(outDir))
                {
                    OutputDirectory = outDir;
                }
                return false;
            });
        }));
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchMangaAsync()
    {
        if (string.IsNullOrWhiteSpace(MangaUrl))
        {
            SetStatus("Por favor insira a URL de um mangá.", "warning");
            return;
        }

        var scraper = _scraperManager.GetScraperForUrl(MangaUrl);
        if (scraper == null)
        {
            SetStatus($"Nenhum scraper encontrado para essa URL. Sites suportados: {SupportedSites}", "error");
            return;
        }

        IsFetching = true;
        IsMangaLoaded = false;
        Chapters.Clear();
        SetStatus($"Buscando info do mangá em {scraper.Name}...", "info");

        try
        {
            var manga = await scraper.GetMangaInfoAsync(MangaUrl);
            Manga = manga;
            MangaName = manga.Name;
            MangaDescription = manga.Description;
            MangaCoverUrl = manga.CoverUrl;
            MangaSite = manga.SiteName;

            SetStatus($"Buscando capítulos em {scraper.Name}...", "info");
            var chapters = await scraper.GetChaptersAsync(MangaUrl);
            manga.Chapters = chapters;

            foreach (var chapter in manga.Chapters)
                Chapters.Add(new ChapterViewModel(chapter));

            TotalChapters = Chapters.Count;
            IsMangaLoaded = true;
            SetStatus($"Encontrado: {manga.Name} — {Chapters.Count} capítulos.", "success");
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao buscar mangá: {ex.Message}", "error");
        }
        finally
        {
            IsFetching = false;
        }
    }

    private bool CanFetch() => !IsFetching && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadSelectedAsync()
    {
        var selected = Chapters.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Nenhum capítulo selecionado.", "warning");
            return;
        }
        await DownloadChaptersAsync(selected);
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAllAsync()
    {
        await DownloadChaptersAsync(Chapters.ToList());
    }

    private bool CanDownload() => IsMangaLoaded && !IsDownloading && !IsFetching;

    private async Task DownloadChaptersAsync(List<ChapterViewModel> chaptersToDownload)
    {
        if (Manga == null) return;

        IsDownloading = true;
        CompletedChapters = 0;
        OverallProgress = 0;
        _cts = new CancellationTokenSource();

        foreach (var chapter in chaptersToDownload)
            chapter.Reset();

        SetStatus($"Baixando {chaptersToDownload.Count} capítulos...", "info");

        try
        {
            Directory.CreateDirectory(OutputDirectory);

            for (int i = 0; i < chaptersToDownload.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var chapterVm = chaptersToDownload[i];
                chapterVm.SetDownloading();

                try
                {
                    var capturedIndex = i;
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            chapterVm.UpdateProgress(p.CurrentPage, p.TotalPages);
                            UpdateOverallProgress(capturedIndex, chaptersToDownload.Count, p);
                            return false;
                        });
                    });

                    await _downloadService.DownloadChapterAsync(
                        Manga, chapterVm.Chapter, OutputDirectory, DownloadFormat, progress, _cts.Token);

                    chapterVm.SetCompleted();
                    CompletedChapters = i + 1;
                }
                catch (OperationCanceledException) { chapterVm.SetFailed("Cancelado"); throw; }
                catch (Exception ex) { chapterVm.SetFailed(ex.Message); }
            }

            SetStatus($"Download completo! {CompletedChapters}/{chaptersToDownload.Count} capítulos salvos em {OutputDirectory}", "success");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download cancelado.", "warning");
        }
        catch (Exception ex)
        {
            SetStatus($"Erro no download: {ex.Message}", "error");
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var c in Chapters) c.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var c in Chapters) c.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleLogPanel() => IsLogPanelOpen = !IsLogPanelOpen;

    [RelayCommand]
    private void ClearLogs()
    {
        LogEntries.Clear();
        LogCount = 0;
        _logService.Clear();
    }

    [RelayCommand]
    private void ResetToSearch()
    {
        Manga = null;
        MangaName = string.Empty;
        MangaDescription = string.Empty;
        MangaCoverUrl = string.Empty;
        MangaSite = string.Empty;
        Chapters.Clear();
        IsMangaLoaded = false;
        IsFetching = false;
        IsDownloading = false;
        SetStatus("Cole a URL de um mangá e clique em Fetch para começar.", "info");
    }

    private void UpdateOverallProgress(int chapterIndex, int totalChapters, DownloadProgress pageProgress)
    {
        var chapterWeight = 100.0 / totalChapters;
        OverallProgress = chapterIndex * chapterWeight + chapterWeight * (pageProgress.Percentage / 100.0);
        OverallProgressText = $"Cap. {chapterIndex + 1}/{totalChapters} · Pág. {pageProgress.CurrentPage}/{pageProgress.TotalPages}";
    }

    private void SetStatus(string message, string type)
    {
        StatusMessage = message;
        StatusType = type;
        IsErrorState = string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnDownloadFormatChanged(DownloadFormat value)
    {
        _settingsStore.SetEnumAsync("Download.Format", value);
    }
    
    partial void OnSelectedImageFormatChanged(ImageFormat value)
    {
        _settingsStore.SetEnumAsync("Download.ImageFormat", value);
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        _settingsStore.SetStringAsync("Download.OutputDirectory", value);
    }
}
