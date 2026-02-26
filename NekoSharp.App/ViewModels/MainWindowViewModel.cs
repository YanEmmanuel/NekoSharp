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

     
    [ObservableProperty] private string _statusMessage = "Cole a URL de um mangá e clique em Buscar para começar.";
    [ObservableProperty] private string _statusType = "info";

     
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _overallProgressText = string.Empty;
    [ObservableProperty] private int _totalChapters;
    [ObservableProperty] private int _completedChapters;

     
    [ObservableProperty] private DownloadFormat _downloadFormat = DownloadFormat.FolderImages;
    [ObservableProperty] private ImageFormat _selectedImageFormat = ImageFormat.Original;
    [ObservableProperty] private int _imageCompressionPercent;

    // SmartStitch settings
    [ObservableProperty] private bool _smartStitchEnabled;
    [ObservableProperty] private int _smartStitchSplitHeight = 5000;
    [ObservableProperty] private StitchDetectorType _smartStitchDetectorType = StitchDetectorType.PixelComparison;
    [ObservableProperty] private int _smartStitchSensitivity = 90;
    [ObservableProperty] private int _smartStitchScanStep = 5;
    [ObservableProperty] private int _smartStitchIgnorablePixels;
    [ObservableProperty] private StitchWidthEnforcement _smartStitchWidthEnforcement = StitchWidthEnforcement.None;
    [ObservableProperty] private int _smartStitchCustomWidth = 720;
    [ObservableProperty] private ImageFormat _smartStitchOutputFormat = ImageFormat.Png;
    [ObservableProperty] private int _smartStitchLossyQuality = 100;

    // Concurrent chapter downloads (1-10, default 3)
    [ObservableProperty] private int _maxConcurrentChapters = 3;

    // MediocreScan auth state
    [ObservableProperty] private bool _isMediocreAuthBusy;
    [ObservableProperty] private string _mediocreAuthStatus = "Desconectado";
    [ObservableProperty] private string _mediocreAuthUser = string.Empty;
    [ObservableProperty] private string _mediocreAuthLastUpdated = "-";

     
    [ObservableProperty] private bool _isLogPanelOpen;
    [ObservableProperty] private int _logCount;

     
    [ObservableProperty] private string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DownloadsMangas");

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
            var compression = await _settingsStore.GetIntAsync("Download.ImageCompressionPercent", 0);
            var outDir = await _settingsStore.GetStringAsync("Download.OutputDirectory");

            // SmartStitch settings
            var ssEnabled = await _settingsStore.GetBoolAsync(SmartStitchSettings.KeyEnabled, false);
            var ssSplitHeight = await _settingsStore.GetIntAsync(SmartStitchSettings.KeySplitHeight, 5000);
            var ssDetector = await _settingsStore.GetEnumAsync(SmartStitchSettings.KeyDetectorType, StitchDetectorType.PixelComparison);
            var ssSensitivity = await _settingsStore.GetIntAsync(SmartStitchSettings.KeySensitivity, 90);
            var ssScanStep = await _settingsStore.GetIntAsync(SmartStitchSettings.KeyScanStep, 5);
            var ssIgnorable = await _settingsStore.GetIntAsync(SmartStitchSettings.KeyIgnorablePixels, 0);
            var ssWidthEnf = await _settingsStore.GetEnumAsync(SmartStitchSettings.KeyWidthEnforcement, StitchWidthEnforcement.None);
            var ssCustomW = await _settingsStore.GetIntAsync(SmartStitchSettings.KeyCustomWidth, 720);
            var ssOutFmt = await _settingsStore.GetEnumAsync(SmartStitchSettings.KeyOutputFormat, ImageFormat.Png);
            var ssLossyQ = await _settingsStore.GetIntAsync(SmartStitchSettings.KeyLossyQuality, 100);
            var concChapters = await _settingsStore.GetIntAsync("Download.MaxConcurrentChapters", 3);

            GLib.Functions.IdleAdd(0, () =>
            {
                DownloadFormat = df;
                SelectedImageFormat = imfmt;
                ImageCompressionPercent = Math.Clamp(compression, 0, 100);
                if (!string.IsNullOrEmpty(outDir))
                {
                    OutputDirectory = outDir;
                }

                SmartStitchEnabled = ssEnabled;
                SmartStitchSplitHeight = Math.Max(100, ssSplitHeight);
                SmartStitchDetectorType = ssDetector;
                SmartStitchSensitivity = Math.Clamp(ssSensitivity, 0, 100);
                SmartStitchScanStep = Math.Clamp(ssScanStep, 1, 100);
                SmartStitchIgnorablePixels = Math.Max(0, ssIgnorable);
                SmartStitchWidthEnforcement = ssWidthEnf;
                SmartStitchCustomWidth = Math.Max(1, ssCustomW);
                SmartStitchOutputFormat = ssOutFmt;
                SmartStitchLossyQuality = Math.Clamp(ssLossyQ, 1, 100);

                MaxConcurrentChapters = Math.Clamp(concChapters, 1, 10);

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
            SetStatus($"Nenhum provedor encontrado para essa URL. Sites suportados: {SupportedSites}", "error");
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

    private bool CanRunMediocreAuthAction() => !IsMediocreAuthBusy && !IsFetching && !IsDownloading;

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

        // These run on the main thread (before first await)
        IsDownloading = true;
        CompletedChapters = 0;
        OverallProgress = 0;
        _cts = new CancellationTokenSource();

        foreach (var chapter in chaptersToDownload)
            chapter.Reset();

        SetStatus($"Baixando {chaptersToDownload.Count} capítulos...", "info");

        // Capture current settings so they don't change mid-download
        var manga = Manga;
        var outputDir = OutputDirectory;
        var format = DownloadFormat;
        var totalCount = chaptersToDownload.Count;
        var completedCount = 0;

        try
        {
            Directory.CreateDirectory(outputDir);

            // Parallel chapter downloads with concurrency limit
            var semaphore = new SemaphoreSlim(MaxConcurrentChapters);
            var tasks = chaptersToDownload.Select((chapterVm, index) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(_cts!.Token);
                try
                {
                    _cts!.Token.ThrowIfCancellationRequested();

                    // All ViewModel mutations dispatched to GTK main thread
                    GLib.Functions.IdleAdd(0, () => { chapterVm.SetDownloading(); return false; });

                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        GLib.Functions.IdleAdd(0, () =>
                        {
                            chapterVm.UpdateProgress(p);
                            return false;
                        });
                    });

                    await _downloadService.DownloadChapterAsync(
                        manga!, chapterVm.Chapter, outputDir, format, progress, _cts!.Token);

                    var count = Interlocked.Increment(ref completedCount);
                    GLib.Functions.IdleAdd(0, () =>
                    {
                        chapterVm.SetCompleted();
                        CompletedChapters = count;
                        OverallProgress = (double)count / totalCount * 100;
                        OverallProgressText = $"Cap. {count}/{totalCount}";
                        return false;
                    });
                }
                catch (OperationCanceledException)
                {
                    GLib.Functions.IdleAdd(0, () => { chapterVm.SetFailed("Cancelado"); return false; });
                    throw;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    GLib.Functions.IdleAdd(0, () => { chapterVm.SetFailed(msg); return false; });
                }
                finally
                {
                    semaphore.Release();
                }
            }, _cts!.Token)).ToList();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { throw; }
            catch { /* individual errors already handled per-chapter */ }

            // After Task.WhenAll we're on thread pool — dispatch UI updates
            GLib.Functions.IdleAdd(0, () =>
            {
                SetStatus($"Download completo! {CompletedChapters}/{totalCount} capítulos salvos em {outputDir}", "success");
                return false;
            });
        }
        catch (OperationCanceledException)
        {
            GLib.Functions.IdleAdd(0, () => { SetStatus("Download cancelado.", "warning"); return false; });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            GLib.Functions.IdleAdd(0, () => { SetStatus($"Erro no download: {msg}", "error"); return false; });
        }
        finally
        {
            GLib.Functions.IdleAdd(0, () =>
            {
                IsDownloading = false;
                _cts?.Dispose();
                _cts = null;
                return false;
            });
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

    [RelayCommand(CanExecute = nameof(CanInvertChapterOrder))]
    private void InvertChapterOrder()
    {
        if (Chapters.Count < 2)
            return;

        var ordered = Chapters.Reverse().ToList();
        Chapters.Clear();
        foreach (var chapter in ordered)
            Chapters.Add(chapter);

        SetStatus("Ordem dos capítulos invertida.", "info");
    }

    private bool CanInvertChapterOrder() => IsMangaLoaded && !IsDownloading && !IsFetching && Chapters.Count > 1;

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
        SetStatus("Cole a URL de um mangá e clique em Buscar para começar.", "info");
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

    partial void OnImageCompressionPercentChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 100);
        if (value != normalized)
        {
            ImageCompressionPercent = normalized;
            return;
        }

        _settingsStore.SetIntAsync("Download.ImageCompressionPercent", normalized);
    }

    // ── SmartStitch settings persistence ──

    partial void OnSmartStitchEnabledChanged(bool value)
        => _settingsStore.SetBoolAsync(SmartStitchSettings.KeyEnabled, value);

    partial void OnSmartStitchSplitHeightChanged(int value)
    {
        var v = Math.Max(100, value);
        if (v != value) { SmartStitchSplitHeight = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeySplitHeight, v);
    }

    partial void OnSmartStitchDetectorTypeChanged(StitchDetectorType value)
        => _settingsStore.SetEnumAsync(SmartStitchSettings.KeyDetectorType, value);

    partial void OnSmartStitchSensitivityChanged(int value)
    {
        var v = Math.Clamp(value, 0, 100);
        if (v != value) { SmartStitchSensitivity = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeySensitivity, v);
    }

    partial void OnSmartStitchScanStepChanged(int value)
    {
        var v = Math.Clamp(value, 1, 100);
        if (v != value) { SmartStitchScanStep = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeyScanStep, v);
    }

    partial void OnSmartStitchIgnorablePixelsChanged(int value)
    {
        var v = Math.Max(0, value);
        if (v != value) { SmartStitchIgnorablePixels = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeyIgnorablePixels, v);
    }

    partial void OnSmartStitchWidthEnforcementChanged(StitchWidthEnforcement value)
        => _settingsStore.SetEnumAsync(SmartStitchSettings.KeyWidthEnforcement, value);

    partial void OnSmartStitchCustomWidthChanged(int value)
    {
        var v = Math.Max(1, value);
        if (v != value) { SmartStitchCustomWidth = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeyCustomWidth, v);
    }

    partial void OnSmartStitchOutputFormatChanged(ImageFormat value)
        => _settingsStore.SetEnumAsync(SmartStitchSettings.KeyOutputFormat, value);

    partial void OnSmartStitchLossyQualityChanged(int value)
    {
        var v = Math.Clamp(value, 1, 100);
        if (v != value) { SmartStitchLossyQuality = v; return; }
        _settingsStore.SetIntAsync(SmartStitchSettings.KeyLossyQuality, v);
    }

    partial void OnMaxConcurrentChaptersChanged(int value)
    {
        var v = Math.Clamp(value, 1, 10);
        if (v != value) { MaxConcurrentChapters = v; return; }
        _settingsStore.SetIntAsync("Download.MaxConcurrentChapters", v);
    }

    [RelayCommand(CanExecute = nameof(CanRunMediocreAuthAction))]
    private async Task ConnectMediocreAuthAsync()
    {
        var provider = GetMediocreAuthProvider();
        if (provider is null)
        {
            SetStatus("Provedor MediocreScan não está disponível.", "warning");
            return;
        }

        IsMediocreAuthBusy = true;
        try
        {
            SetStatus("Abrindo login do MediocreScan no navegador...", "info");
            await provider.LoginInteractivelyAsync();
            await RefreshMediocreAuthStateAsync();
            SetStatus("Login do MediocreScan concluído.", "success");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha no login do MediocreScan: {ex.Message}", "error");
        }
        finally
        {
            IsMediocreAuthBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMediocreAuthAction))]
    private async Task ClearMediocreAuthAsync()
    {
        var provider = GetMediocreAuthProvider();
        if (provider is null)
        {
            SetStatus("Provedor MediocreScan não está disponível.", "warning");
            return;
        }

        IsMediocreAuthBusy = true;
        try
        {
            await provider.ClearAuthAsync();
            await RefreshMediocreAuthStateAsync();
            SetStatus("Sessão do MediocreScan removida.", "success");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao limpar sessão do MediocreScan: {ex.Message}", "error");
        }
        finally
        {
            IsMediocreAuthBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMediocreAuthAction))]
    private async Task RefreshMediocreAuthStateAsync()
    {
        var provider = GetMediocreAuthProvider();
        if (provider is null)
        {
            MediocreAuthStatus = "Indisponível";
            MediocreAuthUser = string.Empty;
            MediocreAuthLastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            return;
        }

        IsMediocreAuthBusy = true;
        try
        {
            var state = await provider.GetAuthStateAsync();
            MediocreAuthStatus = state.IsAuthenticated
                ? "Conectado"
                : state.IsExpired ? "Expirado" : "Desconectado";

            MediocreAuthUser = !string.IsNullOrWhiteSpace(state.UserDisplayName)
                ? state.UserDisplayName!
                : !string.IsNullOrWhiteSpace(state.UserEmail) ? state.UserEmail! : string.Empty;

            MediocreAuthLastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        }
        catch (Exception ex)
        {
            MediocreAuthStatus = "Erro";
            MediocreAuthUser = ex.Message;
            MediocreAuthLastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        }
        finally
        {
            IsMediocreAuthBusy = false;
        }
    }

    partial void OnIsMediocreAuthBusyChanged(bool value)
    {
        ConnectMediocreAuthCommand.NotifyCanExecuteChanged();
        ClearMediocreAuthCommand.NotifyCanExecuteChanged();
        RefreshMediocreAuthStateCommand.NotifyCanExecuteChanged();
    }

    private IInteractiveAuthProvider? GetMediocreAuthProvider()
    {
        return _scraperManager.Scrapers
            .FirstOrDefault(s => s.Name.Equals("MediocreScan", StringComparison.OrdinalIgnoreCase))
            as IInteractiveAuthProvider;
    }
}
