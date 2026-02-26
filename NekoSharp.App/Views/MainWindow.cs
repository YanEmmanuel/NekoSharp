using System.Collections.Specialized;
using System.ComponentModel;
using NekoSharp.App.ViewModels;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;

namespace NekoSharp.App.Views;

public class MainWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly Adw.ApplicationWindow _window;

    private Gtk.SearchEntry _urlEntry = null!;
    private Gtk.Button _fetchButton = null!;
    private Gtk.Button _pasteButton = null!;
    private Gtk.MenuButton _providersButton = null!;
    private Gtk.Popover _providersPopover = null!;

    private Gtk.Stack _contentStack = null!;
    private Adw.ViewStack _viewStack = null!;

    private Gtk.Box _emptyState = null!;
    private Adw.StatusPage _emptyStatusPage = null!;

    private Gtk.Box _errorState = null!;
    private Adw.StatusPage _errorStatusPage = null!;
    private Gtk.Box _errorSearchContainer = null!;
    private Gtk.Button _backButton = null!;

    private Gtk.Box _fetchingState = null!;
    private Gtk.Spinner _fetchingSpinner = null!;

    private Gtk.Box _loadedState = null!;
    private Gtk.Picture _coverImage = null!;
    private Gtk.Label _mangaTitleLabel = null!;
    private Gtk.Label _mangaDescLabel = null!;
    private Gtk.Label _siteBadgeLabel = null!;
    private Gtk.Label _chapterBadgeLabel = null!;
    private Gtk.Button _downloadSelectedBtn = null!;
    private Gtk.Button _downloadAllBtn = null!;
    private Gtk.Button _cancelBtn = null!;

    private Gtk.Box _progressSection = null!;
    private Gtk.Label _progressTextLabel = null!;
    private Gtk.Label _progressPercentLabel = null!;
    private Gtk.ProgressBar _overallProgressBar = null!;

    private Gtk.Box _chapterListBox = null!;

    private LogWindow _logWindow;
    private Gtk.Button _logToggleBtn = null!;

    private Gtk.Label _statusIconLabel = null!;
    private Gtk.Label _statusMessageLabel = null!;
    private Gtk.Label _outputDirLabel = null!;

    private readonly HttpClient _httpClient;

    private readonly Dictionary<ChapterViewModel, ChapterRowWidgets> _chapterRows = new();

    public MainWindow(MainWindowViewModel viewModel, Adw.Application app, LogService? logService = null)
    {
        _vm = viewModel;
        _logWindow = new LogWindow(viewModel, app);

         
        HttpMessageHandler handler = logService != null
            ? new LoggingHttpHandler(logService, new HttpClientHandler())
            : new HttpClientHandler();
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);

        _window = Adw.ApplicationWindow.New(app);
        _window.SetTitle("NekoSharp");
        _window.SetDefaultSize(1150, 780);
        _window.SetSizeRequest(900, 650);
        
        _window.OnCloseRequest += (s, e) => { 
            app.Quit();
            return false; 
        };

        var mainBox = BuildLayout();
        _window.SetContent(mainBox);

        BindViewModel();
    }

    public void Present() => _window.Present();

     
     
     

    private Gtk.Widget BuildLayout()
    {
        var mainVBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        _viewStack = Adw.ViewStack.New();
        
        var libraryPage = _viewStack.AddNamed(BuildLibraryView(), "library");
        libraryPage.SetTitle("Biblioteca");
        libraryPage.SetIconName("library-symbolic");

        var settingsPage = _viewStack.AddNamed(BuildSettingsView(), "settings");
        settingsPage.SetTitle("Configurações");
        settingsPage.SetIconName("emblem-system-symbolic");

        _viewStack.SetVisibleChildName("library");

        _viewStack.OnNotify += (_, args) =>
        {
             
        };

         
        mainVBox.Append(BuildHeaderBar());

         
        _viewStack.SetVexpand(true);
        _viewStack.SetHexpand(true);
        mainVBox.Append(_viewStack);

        mainVBox.Append(BuildStatusBar());

        return mainVBox;
    }

    private Gtk.Widget BuildHeaderBar()
    {
        var headerBar = Adw.HeaderBar.New();

        var viewSwitcher = Adw.ViewSwitcher.New();
        viewSwitcher.SetStack(_viewStack);
        headerBar.SetTitleWidget(viewSwitcher);

        _logToggleBtn = Gtk.Button.New();
        _logToggleBtn.SetIconName("utilities-terminal-symbolic");
        _logToggleBtn.SetTooltipText("Abrir logs");
        _logToggleBtn.OnClicked += (_, _) => 
        {
            _logWindow.Present();
        };
        headerBar.PackEnd(_logToggleBtn);

        return headerBar;
    }

     
    private Gtk.Widget _searchBarWidget = null!;

    private Gtk.Widget BuildLibraryView()
    {
        var libraryBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);  

        var topArea = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        topArea.SetMarginTop(16);
        topArea.SetMarginBottom(8);
        topArea.SetMarginStart(16);
        topArea.SetMarginEnd(16);

        var providersRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        providersRow.SetHalign(Gtk.Align.End);

        _providersButton = Gtk.MenuButton.New();
        _providersButton.SetLabel("Provedores");
        _providersButton.SetTooltipText("Sites suportados");
        _providersButton.AddCssClass("flatpak-button");
        _providersPopover = BuildProvidersPopover();
        _providersButton.SetPopover(_providersPopover);
        providersRow.Append(_providersButton);

        topArea.Append(providersRow);

         
        _searchBarWidget = BuildSearchBar();
        topArea.Append(_searchBarWidget);

        libraryBox.Append(topArea);
        libraryBox.Append(BuildContentArea());

        return libraryBox;
    }

    private Gtk.Widget BuildSearchBar()
    {
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(600);
        clamp.SetTighteningThreshold(400);
        clamp.SetHalign(Gtk.Align.Center);

        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);

        _urlEntry = Gtk.SearchEntry.New();
        _urlEntry.SetPlaceholderText("Cole a URL do mangá...");
        _urlEntry.SetHexpand(true);

        _urlEntry.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "text")
                _vm.MangaUrl = _urlEntry.GetText();
        };

        _urlEntry.OnActivate += (_, _) =>
        {
            if (_vm.FetchMangaCommand.CanExecute(null))
                _vm.FetchMangaCommand.Execute(null);
        };

        bar.Append(_urlEntry);

        _pasteButton = Gtk.Button.New();
        _pasteButton.SetIconName("edit-paste-symbolic");
        _pasteButton.SetTooltipText("Colar");
        _pasteButton.AddCssClass("flatpak-button");
        _pasteButton.OnClicked += (_, _) => PasteFromClipboardAsync();
        bar.Append(_pasteButton);

        _fetchButton = Gtk.Button.New();
        _fetchButton.SetIconName("system-search-symbolic");
        _fetchButton.SetTooltipText("Buscar");
        _fetchButton.AddCssClass("flatpak-button");
        _fetchButton.AddCssClass("flatpak-primary");
        _fetchButton.OnClicked += (_, _) =>
        {
            if (_vm.FetchMangaCommand.CanExecute(null))
                _vm.FetchMangaCommand.Execute(null);
        };
        bar.Append(_fetchButton);

        clamp.SetChild(bar);
        return clamp;
    }

    private Gtk.Popover BuildProvidersPopover()
    {
        var popover = Gtk.Popover.New();
        popover.SetHasArrow(true);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.SetMarginStart(12);
        box.SetMarginEnd(12);

        var title = Gtk.Label.New("Provedores suportados");
        title.SetHalign(Gtk.Align.Start);
        title.AddCssClass("heading");
        box.Append(title);

        var listBox = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        foreach (var name in _vm.ProviderNames)
        {
            var label = Gtk.Label.New(name);
            label.SetHalign(Gtk.Align.Start);
            listBox.Append(label);
        }
        box.Append(listBox);

        popover.SetChild(box);
        return popover;
    }

    private Gtk.Widget BuildSettingsView()
    {
        var page = Adw.PreferencesPage.New();

        var group = Adw.PreferencesGroup.New();
        group.SetTitle("Downloads");
        group.SetDescription("Configure onde e como os arquivos são salvos.");

        var outputRow = Adw.ActionRow.New();
        outputRow.SetTitle("Pasta de Destino");
        outputRow.SetSubtitle(_vm.OutputDirectory);

        var folderBtn = Gtk.Button.New();
        folderBtn.SetIconName("folder-open-symbolic");
        folderBtn.SetTooltipText("Escolher pasta");
        folderBtn.OnClicked += async (_, _) => await ChooseOutputFolderAsync(outputRow);
        outputRow.AddSuffix(folderBtn);
        outputRow.SetActivatable(false);
        group.Add(outputRow);
        
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.OutputDirectory))
            {
                outputRow.SetSubtitle(_vm.OutputDirectory);
            }
        };

        var formatRow = Adw.ComboRow.New();
        formatRow.SetTitle("Formato de download");
        formatRow.SetSubtitle("Escolha o formato de saída");
        var model = Gtk.StringList.New(new[] { "Pasta (imagens)", "CBZ (zip)" });
        formatRow.SetModel(model);
        formatRow.SetSelected((uint)_vm.DownloadFormat);
        formatRow.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                _vm.DownloadFormat = (DownloadFormat)formatRow.GetSelected();
        };
        group.Add(formatRow);

        var imageFormatRow = Adw.ComboRow.New();
        imageFormatRow.SetTitle("Formato da Imagem");
        imageFormatRow.SetSubtitle("Escolha a conversão para as imagens (Original mantém o formato do site)");
        var imageModel = Gtk.StringList.New(new[] { "Original", "JPEG", "PNG", "WebP" });
        imageFormatRow.SetModel(imageModel);
        
         
        imageFormatRow.SetSelected((uint)_vm.SelectedImageFormat);
        
        imageFormatRow.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                _vm.SelectedImageFormat = (ImageFormat)imageFormatRow.GetSelected();
        };

         
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.SelectedImageFormat))
            {
                var val = (uint)_vm.SelectedImageFormat;
                if (imageFormatRow.GetSelected() != val)
                    imageFormatRow.SetSelected(val);
            }
            if (args.PropertyName == nameof(_vm.DownloadFormat))
            {
                var val = (uint)_vm.DownloadFormat;
                if (formatRow.GetSelected() != val)
                    formatRow.SetSelected(val);
            }
        };

        group.Add(imageFormatRow);

        var compressionRow = Adw.ActionRow.New();
        compressionRow.SetTitle("Compressão da Imagem");
        compressionRow.SetSubtitle("0% = sem compressão, 100% = compressão máxima");

        var compressionSpin = Gtk.SpinButton.NewWithRange(0, 100, 1);
        compressionSpin.SetNumeric(true);
        compressionSpin.SetValue(_vm.ImageCompressionPercent);
        compressionSpin.OnValueChanged += (_, _) =>
        {
            _vm.ImageCompressionPercent = compressionSpin.GetValueAsInt();
        };
        compressionRow.AddSuffix(compressionSpin);
        compressionRow.SetActivatable(false);
        group.Add(compressionRow);

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.ImageCompressionPercent))
            {
                var val = _vm.ImageCompressionPercent;
                if (compressionSpin.GetValueAsInt() != val)
                    compressionSpin.SetValue(val);
            }
        };

        var concurrentRow = Adw.ActionRow.New();
        concurrentRow.SetTitle("Downloads Simultâneos");
        concurrentRow.SetSubtitle("Quantidade de capítulos baixados ao mesmo tempo (1-10). Padrão: 3");
        var concurrentSpin = Gtk.SpinButton.NewWithRange(1, 10, 1);
        concurrentSpin.SetNumeric(true);
        concurrentSpin.SetValue(_vm.MaxConcurrentChapters);
        concurrentSpin.OnValueChanged += (_, _) =>
        {
            _vm.MaxConcurrentChapters = concurrentSpin.GetValueAsInt();
        };
        concurrentRow.AddSuffix(concurrentSpin);
        concurrentRow.SetActivatable(false);
        group.Add(concurrentRow);

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.MaxConcurrentChapters))
            {
                var val = _vm.MaxConcurrentChapters;
                if (concurrentSpin.GetValueAsInt() != val)
                    concurrentSpin.SetValue(val);
            }
        };

        page.Add(group);

        page.Add(BuildSmartStitchSettingsGroup());

        return page;
    }

    private Adw.PreferencesGroup BuildSmartStitchSettingsGroup()
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle("SmartStitch");
        group.SetDescription("Pós-processamento: junta páginas verticalmente e recorta em painéis inteligentes. Desativado por padrão.");

        var enableRow = Adw.ActionRow.New();
        enableRow.SetTitle("Ativar SmartStitch");
        enableRow.SetSubtitle("Após o download, as imagens serão unidas e recortadas automaticamente");
        var enableSwitch = Gtk.Switch.New();
        enableSwitch.SetValign(Gtk.Align.Center);
        enableSwitch.SetActive(_vm.SmartStitchEnabled);
        enableSwitch.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "active")
                _vm.SmartStitchEnabled = enableSwitch.GetActive();
        };
        enableRow.AddSuffix(enableSwitch);
        enableRow.SetActivatableWidget(enableSwitch);
        group.Add(enableRow);

        var detailRows = new List<Adw.ActionRow>();

        var splitHeightRow = Adw.ActionRow.New();
        splitHeightRow.SetTitle("Altura de Corte (px)");
        splitHeightRow.SetSubtitle("Altura aproximada de cada painel de saída. Padrão: 5000");
        var splitHeightSpin = Gtk.SpinButton.NewWithRange(100, 50000, 100);
        splitHeightSpin.SetNumeric(true);
        splitHeightSpin.SetValue(_vm.SmartStitchSplitHeight);
        splitHeightSpin.OnValueChanged += (_, _) => _vm.SmartStitchSplitHeight = splitHeightSpin.GetValueAsInt();
        splitHeightRow.AddSuffix(splitHeightSpin);
        splitHeightRow.SetActivatable(false);
        group.Add(splitHeightRow);
        detailRows.Add(splitHeightRow);

        var detectorRow = Adw.ComboRow.New();
        detectorRow.SetTitle("Tipo de Detector");
        detectorRow.SetSubtitle("Comparação de pixels evita cortar por falas/SFX; Direto corta exatamente");
        var detectorModel = Gtk.StringList.New(new[] { "Direto (sem detecção)", "Comparação de pixels" });
        detectorRow.SetModel(detectorModel);
        detectorRow.SetSelected((uint)_vm.SmartStitchDetectorType);
        detectorRow.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                _vm.SmartStitchDetectorType = (StitchDetectorType)detectorRow.GetSelected();
        };
        group.Add(detectorRow);

        var sensitivityRow = Adw.ActionRow.New();
        sensitivityRow.SetTitle("Sensibilidade (%)");
        sensitivityRow.SetSubtitle("0 = corta em qualquer lugar, 100 = exige pixels idênticos. Padrão: 90");
        var sensitivitySpin = Gtk.SpinButton.NewWithRange(0, 100, 1);
        sensitivitySpin.SetNumeric(true);
        sensitivitySpin.SetValue(_vm.SmartStitchSensitivity);
        sensitivitySpin.OnValueChanged += (_, _) => _vm.SmartStitchSensitivity = sensitivitySpin.GetValueAsInt();
        sensitivityRow.AddSuffix(sensitivitySpin);
        sensitivityRow.SetActivatable(false);
        group.Add(sensitivityRow);
        detailRows.Add(sensitivityRow);

        var scanStepRow = Adw.ActionRow.New();
        scanStepRow.SetTitle("Passo de varredura (px)");
        scanStepRow.SetSubtitle("Passo de busca quando a linha atual não pode ser cortada. Padrão: 5");
        var scanStepSpin = Gtk.SpinButton.NewWithRange(1, 100, 1);
        scanStepSpin.SetNumeric(true);
        scanStepSpin.SetValue(_vm.SmartStitchScanStep);
        scanStepSpin.OnValueChanged += (_, _) => _vm.SmartStitchScanStep = scanStepSpin.GetValueAsInt();
        scanStepRow.AddSuffix(scanStepSpin);
        scanStepRow.SetActivatable(false);
        group.Add(scanStepRow);
        detailRows.Add(scanStepRow);

        var ignorableRow = Adw.ActionRow.New();
        ignorableRow.SetTitle("Margem Ignorável (px)");
        ignorableRow.SetSubtitle("Pixels na borda a ignorar durante detecção. Padrão: 0");
        var ignorableSpin = Gtk.SpinButton.NewWithRange(0, 500, 1);
        ignorableSpin.SetNumeric(true);
        ignorableSpin.SetValue(_vm.SmartStitchIgnorablePixels);
        ignorableSpin.OnValueChanged += (_, _) => _vm.SmartStitchIgnorablePixels = ignorableSpin.GetValueAsInt();
        ignorableRow.AddSuffix(ignorableSpin);
        ignorableRow.SetActivatable(false);
        group.Add(ignorableRow);
        detailRows.Add(ignorableRow);

        var widthEnfRow = Adw.ComboRow.New();
        widthEnfRow.SetTitle("Forçar Largura");
        widthEnfRow.SetSubtitle("Nenhum mantém original, Automático usa a menor largura, Manual define um valor");
        var widthEnfModel = Gtk.StringList.New(new[] { "Nenhum", "Automático", "Manual" });
        widthEnfRow.SetModel(widthEnfModel);
        widthEnfRow.SetSelected((uint)_vm.SmartStitchWidthEnforcement);
        widthEnfRow.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                _vm.SmartStitchWidthEnforcement = (StitchWidthEnforcement)widthEnfRow.GetSelected();
        };
        group.Add(widthEnfRow);
        detailRows.Add(widthEnfRow);

        var customWidthRow = Adw.ActionRow.New();
        customWidthRow.SetTitle("Largura Customizada (px)");
        customWidthRow.SetSubtitle("Usado quando 'Forçar Largura' está em Manual. Padrão: 720");
        var customWidthSpin = Gtk.SpinButton.NewWithRange(1, 10000, 10);
        customWidthSpin.SetNumeric(true);
        customWidthSpin.SetValue(_vm.SmartStitchCustomWidth);
        customWidthSpin.OnValueChanged += (_, _) => _vm.SmartStitchCustomWidth = customWidthSpin.GetValueAsInt();
        customWidthRow.AddSuffix(customWidthSpin);
        customWidthRow.SetActivatable(false);
        group.Add(customWidthRow);
        detailRows.Add(customWidthRow);

        var outFmtRow = Adw.ComboRow.New();
        outFmtRow.SetTitle("Formato de Saída (Stitch)");
        outFmtRow.SetSubtitle("Formato das imagens recortadas pelo SmartStitch");
        var outFmtModel = Gtk.StringList.New(new[] { "Original", "JPEG", "PNG", "WebP" });
        outFmtRow.SetModel(outFmtModel);
        outFmtRow.SetSelected((uint)_vm.SmartStitchOutputFormat);
        outFmtRow.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                _vm.SmartStitchOutputFormat = (ImageFormat)outFmtRow.GetSelected();
        };
        group.Add(outFmtRow);
        detailRows.Add(outFmtRow);

        var lossyRow = Adw.ActionRow.New();
        lossyRow.SetTitle("Qualidade (com perdas)");
        lossyRow.SetSubtitle("Qualidade para JPEG/WebP. 1-100. Padrão: 100");
        var lossySpin = Gtk.SpinButton.NewWithRange(1, 100, 1);
        lossySpin.SetNumeric(true);
        lossySpin.SetValue(_vm.SmartStitchLossyQuality);
        lossySpin.OnValueChanged += (_, _) => _vm.SmartStitchLossyQuality = lossySpin.GetValueAsInt();
        lossyRow.AddSuffix(lossySpin);
        lossyRow.SetActivatable(false);
        group.Add(lossyRow);
        detailRows.Add(lossyRow);

        void UpdateSmartStitchVisibility()
        {
            var enabled = _vm.SmartStitchEnabled;
            foreach (var row in detailRows)
                row.SetVisible(enabled);

            customWidthRow.SetVisible(enabled && _vm.SmartStitchWidthEnforcement == StitchWidthEnforcement.Manual);

            var isPixel = _vm.SmartStitchDetectorType == StitchDetectorType.PixelComparison;
            sensitivityRow.SetVisible(enabled && isPixel);
            scanStepRow.SetVisible(enabled && isPixel);
            ignorableRow.SetVisible(enabled && isPixel);

            var isLossy = _vm.SmartStitchOutputFormat == ImageFormat.Jpeg ||
                          _vm.SmartStitchOutputFormat == ImageFormat.WebP;
            lossyRow.SetVisible(enabled && isLossy);
        }

        UpdateSmartStitchVisibility();

        _vm.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(_vm.SmartStitchEnabled):
                    if (enableSwitch.GetActive() != _vm.SmartStitchEnabled)
                        enableSwitch.SetActive(_vm.SmartStitchEnabled);
                    UpdateSmartStitchVisibility();
                    break;
                case nameof(_vm.SmartStitchSplitHeight):
                    if (splitHeightSpin.GetValueAsInt() != _vm.SmartStitchSplitHeight)
                        splitHeightSpin.SetValue(_vm.SmartStitchSplitHeight);
                    break;
                case nameof(_vm.SmartStitchDetectorType):
                    var dtVal = (uint)_vm.SmartStitchDetectorType;
                    if (detectorRow.GetSelected() != dtVal)
                        detectorRow.SetSelected(dtVal);
                    UpdateSmartStitchVisibility();
                    break;
                case nameof(_vm.SmartStitchSensitivity):
                    if (sensitivitySpin.GetValueAsInt() != _vm.SmartStitchSensitivity)
                        sensitivitySpin.SetValue(_vm.SmartStitchSensitivity);
                    break;
                case nameof(_vm.SmartStitchScanStep):
                    if (scanStepSpin.GetValueAsInt() != _vm.SmartStitchScanStep)
                        scanStepSpin.SetValue(_vm.SmartStitchScanStep);
                    break;
                case nameof(_vm.SmartStitchIgnorablePixels):
                    if (ignorableSpin.GetValueAsInt() != _vm.SmartStitchIgnorablePixels)
                        ignorableSpin.SetValue(_vm.SmartStitchIgnorablePixels);
                    break;
                case nameof(_vm.SmartStitchWidthEnforcement):
                    var weVal = (uint)_vm.SmartStitchWidthEnforcement;
                    if (widthEnfRow.GetSelected() != weVal)
                        widthEnfRow.SetSelected(weVal);
                    UpdateSmartStitchVisibility();
                    break;
                case nameof(_vm.SmartStitchCustomWidth):
                    if (customWidthSpin.GetValueAsInt() != _vm.SmartStitchCustomWidth)
                        customWidthSpin.SetValue(_vm.SmartStitchCustomWidth);
                    break;
                case nameof(_vm.SmartStitchOutputFormat):
                    var ofVal = (uint)_vm.SmartStitchOutputFormat;
                    if (outFmtRow.GetSelected() != ofVal)
                        outFmtRow.SetSelected(ofVal);
                    UpdateSmartStitchVisibility();
                    break;
                case nameof(_vm.SmartStitchLossyQuality):
                    if (lossySpin.GetValueAsInt() != _vm.SmartStitchLossyQuality)
                        lossySpin.SetValue(_vm.SmartStitchLossyQuality);
                    break;
            }
        };

        return group;
    }

    private Gtk.Widget BuildContentArea()
    {
        _contentStack = Gtk.Stack.New();
        _contentStack.SetTransitionType(Gtk.StackTransitionType.Crossfade);
        _contentStack.SetTransitionDuration(200);
        _contentStack.SetHexpand(true);
        _contentStack.SetVexpand(true);

         
        _emptyState = BuildEmptyState();
        _contentStack.AddNamed(_emptyState, "empty");

         
        _errorState = BuildErrorState();
        _contentStack.AddNamed(_errorState, "error");

         
        _fetchingState = BuildFetchingState();
        _contentStack.AddNamed(_fetchingState, "fetching");

         
        var loadedScroll = Gtk.ScrolledWindow.New();
         
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(860);
        clamp.SetTighteningThreshold(600);
        
        _loadedState = BuildLoadedState();
        clamp.SetChild(_loadedState);
        
        loadedScroll.SetChild(clamp);
        _contentStack.AddNamed(loadedScroll, "loaded");

        _contentStack.SetVisibleChildName("empty");

        return _contentStack;
    }

    private Gtk.Box BuildEmptyState()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        _emptyStatusPage = Adw.StatusPage.New();
        _emptyStatusPage.SetIconName("library-symbolic");
        _emptyStatusPage.SetTitle("NekoSharp");
        _emptyStatusPage.SetDescription("Use a barra de pesquisa acima para começar.");
        _emptyStatusPage.SetVexpand(true);

        box.Append(_emptyStatusPage);
        return box;
    }

    private Gtk.Box BuildErrorState()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        _errorStatusPage = Adw.StatusPage.New();
        _errorStatusPage.SetIconName("dialog-error-symbolic");
        _errorStatusPage.SetTitle("Algo deu errado");
        _errorStatusPage.SetDescription(_vm.StatusMessage);
        _errorStatusPage.SetVexpand(true);

        _errorSearchContainer = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        _errorSearchContainer.SetHalign(Gtk.Align.Center);

        var actionsRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        actionsRow.SetHalign(Gtk.Align.Center);

        _backButton = Gtk.Button.NewWithLabel("Voltar");
        _backButton.AddCssClass("flatpak-button");
        _backButton.OnClicked += (_, _) => _vm.ResetToSearchCommand.Execute(null);
        actionsRow.Append(_backButton);

        _errorSearchContainer.Append(actionsRow);
        _errorStatusPage.SetChild(_errorSearchContainer);

        box.Append(_errorStatusPage);
        return box;
    }

    private Gtk.Box BuildFetchingState()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 16);
        box.SetValign(Gtk.Align.Center);
        box.SetHalign(Gtk.Align.Center);

        _fetchingSpinner = Gtk.Spinner.New();
        _fetchingSpinner.SetSizeRequest(40, 40);
        _fetchingSpinner.Start();
        box.Append(_fetchingSpinner);

        var text = Gtk.Label.New("Buscando informações...");
        text.AddCssClass("fetching-text");
        box.Append(text);

        return box;
    }

    private Gtk.Box BuildLoadedState()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 16);
        box.SetMarginTop(24);
        box.SetMarginBottom(24);
        box.SetMarginStart(24);
        box.SetMarginEnd(24);

         
        box.Append(BuildMangaHeader());

         
        _progressSection = BuildProgressSection();
        _progressSection.SetVisible(false);
        box.Append(_progressSection);

         
        var selectionBar = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        selectionBar.SetHalign(Gtk.Align.End);  
        
        var selectAllBtn = Gtk.Button.NewWithLabel("Selecionar todos");
        selectAllBtn.AddCssClass("flat");
        selectAllBtn.OnClicked += (_, _) => _vm.SelectAllCommand.Execute(null);
        selectionBar.Append(selectAllBtn);
        
        var deselectAllBtn = Gtk.Button.NewWithLabel("Limpar seleção");
        deselectAllBtn.AddCssClass("flat");
        deselectAllBtn.OnClicked += (_, _) => _vm.DeselectAllCommand.Execute(null);
        selectionBar.Append(deselectAllBtn);

        var invertOrderBtn = Gtk.Button.NewWithLabel("Inverter ordem");
        invertOrderBtn.AddCssClass("flat");
        invertOrderBtn.OnClicked += (_, _) =>
        {
            if (_vm.InvertChapterOrderCommand.CanExecute(null))
                _vm.InvertChapterOrderCommand.Execute(null);
        };
        selectionBar.Append(invertOrderBtn);
        
        box.Append(selectionBar);

         
        box.Append(BuildChapterListCard());

        return box;
    }

    private Gtk.Widget BuildMangaHeader()
    {
        var container = Gtk.Box.New(Gtk.Orientation.Horizontal, 32);
        container.SetMarginBottom(12);

         
        var coverFrame = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        coverFrame.SetSizeRequest(180, 260);
        coverFrame.SetHalign(Gtk.Align.Start);
        coverFrame.SetValign(Gtk.Align.Start);
        coverFrame.SetOverflow(Gtk.Overflow.Hidden);
        coverFrame.AddCssClass("manga-cover");

        _coverImage = Gtk.Picture.New();
        _coverImage.SetSizeRequest(180, 260);
        _coverImage.SetContentFit(Gtk.ContentFit.Cover);
        _coverImage.SetCanShrink(true);
        _coverImage.SetHexpand(false);
        _coverImage.SetVexpand(false);

        coverFrame.Append(_coverImage);
        container.Append(coverFrame);

         
        var infoBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        infoBox.SetHexpand(true);
        infoBox.SetValign(Gtk.Align.Start);

         
        _mangaTitleLabel = Gtk.Label.New("");
        _mangaTitleLabel.AddCssClass("title-1");
        _mangaTitleLabel.SetHalign(Gtk.Align.Start);
        _mangaTitleLabel.SetWrap(true);
        _mangaTitleLabel.SetSelectable(true);
        _mangaTitleLabel.SetMarginBottom(8);
        infoBox.Append(_mangaTitleLabel);

         
        var metaBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        metaBox.SetMarginBottom(16);
        
        _siteBadgeLabel = Gtk.Label.New("");
        _siteBadgeLabel.AddCssClass("pill");  
        _siteBadgeLabel.AddCssClass("dim-label");
        metaBox.Append(_siteBadgeLabel);

        _chapterBadgeLabel = Gtk.Label.New("");
        _chapterBadgeLabel.AddCssClass("dim-label");  
        metaBox.Append(_chapterBadgeLabel);
        
        infoBox.Append(metaBox);

         
        var actionsBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        actionsBox.SetMarginBottom(24);

        _downloadSelectedBtn = Gtk.Button.NewWithLabel("Baixar selecionados");
        _downloadSelectedBtn.AddCssClass("suggested-action");
        _downloadSelectedBtn.AddCssClass("pill");
        _downloadSelectedBtn.SetTooltipText("Baixar capítulos selecionados");
        _downloadSelectedBtn.OnClicked += (_, _) =>
        {
            if (_vm.DownloadSelectedCommand.CanExecute(null))
                _vm.DownloadSelectedCommand.Execute(null);
        };
        actionsBox.Append(_downloadSelectedBtn);

        _downloadAllBtn = Gtk.Button.NewWithLabel("Baixar todos");
        _downloadAllBtn.AddCssClass("pill"); 
        _downloadAllBtn.OnClicked += (_, _) =>
        {
            if (_vm.DownloadAllCommand.CanExecute(null))
                _vm.DownloadAllCommand.Execute(null);
        };
        actionsBox.Append(_downloadAllBtn);
        
        _cancelBtn = Gtk.Button.NewWithLabel("Cancelar");
        _cancelBtn.AddCssClass("destructive-action");
        _cancelBtn.AddCssClass("pill");
        _cancelBtn.SetVisible(false);
        _cancelBtn.OnClicked += (_, _) => _vm.CancelDownloadCommand.Execute(null);
        actionsBox.Append(_cancelBtn);

        infoBox.Append(actionsBox);

         
         
        var descScroll = Gtk.ScrolledWindow.New();
        descScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        descScroll.SetMaxContentHeight(150);  
        descScroll.SetPropagateNaturalHeight(true);

        _mangaDescLabel = Gtk.Label.New("");
        _mangaDescLabel.SetHalign(Gtk.Align.Start);
        _mangaDescLabel.SetWrap(true);
        _mangaDescLabel.SetWrapMode(Pango.WrapMode.Word);
        _mangaDescLabel.AddCssClass("body");
        _mangaDescLabel.SetSelectable(true);
        
        descScroll.SetChild(_mangaDescLabel);
        infoBox.Append(descScroll);

        container.Append(infoBox);
        return container;
    }

    private Gtk.Box BuildProgressSection()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.AddCssClass("progress-section");

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);

        _progressTextLabel = Gtk.Label.New("");
        _progressTextLabel.AddCssClass("progress-text");
        _progressTextLabel.SetHexpand(true);
        _progressTextLabel.SetHalign(Gtk.Align.Start);
        row.Append(_progressTextLabel);

        _progressPercentLabel = Gtk.Label.New("0%");
        _progressPercentLabel.AddCssClass("progress-percent");
        row.Append(_progressPercentLabel);

        box.Append(row);

        _overallProgressBar = Gtk.ProgressBar.New();
        _overallProgressBar.SetFraction(0);
        box.Append(_overallProgressBar);

        return box;
    }

    private Gtk.Widget BuildChapterListCard()
    {
         
        var group = Adw.PreferencesGroup.New();
        group.SetTitle("Capítulos");
        
        _chapterListBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _chapterListBox.AddCssClass("boxed-list"); 
        
        group.Add(_chapterListBox);

        return group;
    }

    private Gtk.Widget BuildStatusBar()
    {
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        bar.AddCssClass("status-bar");

        _statusIconLabel = Gtk.Label.New("ℹ");
        _statusIconLabel.AddCssClass("status-icon");
        _statusIconLabel.AddCssClass("status-info");
        bar.Append(_statusIconLabel);

        _statusMessageLabel = Gtk.Label.New(_vm.StatusMessage);
        _statusMessageLabel.AddCssClass("status-message");
        _statusMessageLabel.AddCssClass("status-info");
        _statusMessageLabel.SetHexpand(true);
        _statusMessageLabel.SetHalign(Gtk.Align.Start);
        _statusMessageLabel.SetEllipsize(Pango.EllipsizeMode.End);
        bar.Append(_statusMessageLabel);

        var outputBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        outputBox.SetOpacity(0.7);
        var folderIcon = Gtk.Label.New("📁");
        folderIcon.AddCssClass("output-dir");
        outputBox.Append(folderIcon);
        _outputDirLabel = Gtk.Label.New(_vm.OutputDirectory);
        _outputDirLabel.AddCssClass("output-dir");
        outputBox.Append(_outputDirLabel);
        bar.Append(outputBox);

        return bar;
    }

     
     
     

    private void BindViewModel()
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Chapters.CollectionChanged += OnChaptersCollectionChanged;
         
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsFetching):
                UpdateContentStack();
                _fetchButton.SetSensitive(!_vm.IsFetching);
                _urlEntry.SetSensitive(!_vm.IsFetching);
                if (_vm.IsFetching) _fetchingSpinner.Start();
                else _fetchingSpinner.Stop();
                break;

            case nameof(MainWindowViewModel.IsMangaLoaded):
                UpdateContentStack();
                break;

            case nameof(MainWindowViewModel.IsErrorState):
                UpdateContentStack();
                break;

            case nameof(MainWindowViewModel.MangaName):
                _mangaTitleLabel.SetText(_vm.MangaName);
                break;

            case nameof(MainWindowViewModel.MangaDescription):
                _mangaDescLabel.SetText(_vm.MangaDescription);
                break;

            case nameof(MainWindowViewModel.MangaSite):
                _siteBadgeLabel.SetText(_vm.MangaSite);
                break;

            case nameof(MainWindowViewModel.TotalChapters):
                _chapterBadgeLabel.SetText($"{_vm.TotalChapters} capítulos");
                break;

            case nameof(MainWindowViewModel.MangaCoverUrl):
                LoadCoverImageAsync(_vm.MangaCoverUrl);
                break;

            case nameof(MainWindowViewModel.IsDownloading):
                _progressSection.SetVisible(_vm.IsDownloading);
                _cancelBtn.SetVisible(_vm.IsDownloading);
                _downloadSelectedBtn.SetSensitive(!_vm.IsDownloading);
                _downloadAllBtn.SetSensitive(!_vm.IsDownloading);
                break;

            case nameof(MainWindowViewModel.OverallProgress):
                _overallProgressBar.SetFraction(_vm.OverallProgress / 100.0);
                _progressPercentLabel.SetText($"{_vm.OverallProgress:F1}%");
                break;

            case nameof(MainWindowViewModel.OverallProgressText):
                _progressTextLabel.SetText(_vm.OverallProgressText);
                break;

            case nameof(MainWindowViewModel.LogCount):
                _logToggleBtn.SetTooltipText($"Logs ({_vm.LogCount})");
                break;

            case nameof(MainWindowViewModel.StatusMessage):
            case nameof(MainWindowViewModel.StatusType):
                UpdateStatusBar();
                if (_errorStatusPage != null)
                    _errorStatusPage.SetDescription(_vm.StatusMessage);
                break;
                
            case nameof(MainWindowViewModel.OutputDirectory):
                _outputDirLabel.SetText(_vm.OutputDirectory);
                break;
        }
    }

    private void UpdateContentStack()
    {
        if (_vm.IsFetching)
        {
            _contentStack.SetVisibleChildName("fetching");
        }
        else if (_vm.IsMangaLoaded)
        {
            _contentStack.SetVisibleChildName("loaded");
        }
        else if (_vm.IsErrorState)
        {
            _contentStack.SetVisibleChildName("error");
        }
        else
        {
            _contentStack.SetVisibleChildName("empty");
        }
    }


    private void UpdateStatusBar()
    {
        _statusMessageLabel.SetText(_vm.StatusMessage);

         
        foreach (var cls in new[] { "status-info", "status-success", "status-warning", "status-error" })
        {
            _statusIconLabel.RemoveCssClass(cls);
            _statusMessageLabel.RemoveCssClass(cls);
        }

        var typeClass = _vm.StatusType switch
        {
            "success" => "status-success",
            "error" => "status-error",
            "warning" => "status-warning",
            _ => "status-info"
        };

        _statusIconLabel.AddCssClass(typeClass);
        _statusMessageLabel.AddCssClass(typeClass);

        _statusIconLabel.SetText(_vm.StatusType switch
        {
            "success" => "✓",
            "error" => "✕",
            "warning" => "⚠",
            _ => "ℹ"
        });
    }

     
     
     

    private class ChapterRowWidgets
    {
        public Adw.ActionRow Row = null!;
        public Gtk.CheckButton CheckButton = null!;
         
        public Gtk.Label StatusLabel = null!;
        public Gtk.ProgressBar ProgressBar = null!;
    }

    private void OnChaptersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                {
                    foreach (ChapterViewModel chapterVm in e.NewItems)
                        AddChapterRow(chapterVm);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                ClearChapterRows();
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                {
                    foreach (ChapterViewModel chapterVm in e.OldItems)
                        RemoveChapterRow(chapterVm);
                }
                break;
        }
    }

    private void AddChapterRow(ChapterViewModel chapterVm)
    {
        var row = Adw.ActionRow.New();
        row.SetTitle(chapterVm.DisplayTitle);
         
         
        
        var check = Gtk.CheckButton.New();
        check.SetActive(chapterVm.IsSelected);
        check.OnToggled += (_, _) => chapterVm.IsSelected = check.GetActive();
        check.SetValign(Gtk.Align.Center);
        row.AddPrefix(check);
        
         
        var suffixBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        suffixBox.SetValign(Gtk.Align.Center);
        
        var statusLabel = Gtk.Label.New(chapterVm.Status);
        statusLabel.AddCssClass("dim-label");
         
        suffixBox.Append(statusLabel);
        
        var progressBar = Gtk.ProgressBar.New();
        progressBar.SetFraction(chapterVm.DownloadProgress / 100.0);
        progressBar.AddCssClass("chapter-progress");
        progressBar.SetValign(Gtk.Align.Center);
         
         
         
        progressBar.SetVisible(false); 
        suffixBox.Append(progressBar);
        
        row.AddSuffix(suffixBox);

        var widgets = new ChapterRowWidgets
        {
            Row = row,
            CheckButton = check,
            StatusLabel = statusLabel,
            ProgressBar = progressBar
        };

        _chapterRows[chapterVm] = widgets;
        _chapterListBox.Append(row);

         
        chapterVm.PropertyChanged += OnChapterPropertyChanged;
    }

    private void OnChapterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChapterViewModel chapterVm) return;
        if (!_chapterRows.TryGetValue(chapterVm, out var widgets)) return;

        switch (e.PropertyName)
        {
            case nameof(ChapterViewModel.Status):
                widgets.StatusLabel.SetText(chapterVm.Status);
                break;
            case nameof(ChapterViewModel.DownloadStatus):
                UpdateChapterStatusCssClass(widgets.StatusLabel, chapterVm.DownloadStatus);
                widgets.ProgressBar.SetVisible(
                    chapterVm.DownloadStatus == ChapterDownloadStatus.Downloading ||
                    chapterVm.DownloadStatus == ChapterDownloadStatus.Stitching);
                break;
            case nameof(ChapterViewModel.DownloadProgress):
                widgets.ProgressBar.SetFraction(chapterVm.DownloadProgress / 100.0);
                break;
            case nameof(ChapterViewModel.IsStitching):
                if (chapterVm.IsStitching)
                {
                    widgets.ProgressBar.AddCssClass("stitching-progress");
                    widgets.ProgressBar.SetVisible(true);
                    widgets.ProgressBar.Pulse();
                }
                else
                {
                    widgets.ProgressBar.RemoveCssClass("stitching-progress");
                }
                break;
            case nameof(ChapterViewModel.IsSelected):
                widgets.CheckButton.SetActive(chapterVm.IsSelected);
                break;
        }
    }

    private void UpdateChapterStatusCssClass(Gtk.Label label, ChapterDownloadStatus status)
    {
         
        label.RemoveCssClass("accent-text");
        label.RemoveCssClass("success-text");
        label.RemoveCssClass("error-text");
        label.RemoveCssClass("warning-text");
        label.RemoveCssClass("stitching-text");

        switch (status)
        {
            case ChapterDownloadStatus.Downloading: label.AddCssClass("accent-text"); break;
            case ChapterDownloadStatus.Stitching: label.AddCssClass("stitching-text"); break;
            case ChapterDownloadStatus.Completed: label.AddCssClass("success-text"); break;
            case ChapterDownloadStatus.Failed: label.AddCssClass("error-text"); break;
            case ChapterDownloadStatus.Queued: label.AddCssClass("dim-label"); break;
        }
    }

    private void ClearChapterRows()
    {
        foreach (var (chapterVm, widgets) in _chapterRows)
        {
            chapterVm.PropertyChanged -= OnChapterPropertyChanged;
            _chapterListBox.Remove(widgets.Row);
        }
        _chapterRows.Clear();
    }

    private void RemoveChapterRow(ChapterViewModel chapterVm)
    {
        if (_chapterRows.TryGetValue(chapterVm, out var widgets))
        {
            chapterVm.PropertyChanged -= OnChapterPropertyChanged;
            _chapterListBox.Remove(widgets.Row);
            _chapterRows.Remove(chapterVm);
        }
    }


     
     
     

    private async void LoadCoverImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
             
            var cacheDir = Path.Combine(Path.GetTempPath(), "NekoSharp", "covers");
            Directory.CreateDirectory(cacheDir);

            var urlHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(url)))[..16];
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var cachePath = Path.Combine(cacheDir, $"{urlHash}{ext}");

             
            if (!File.Exists(cachePath))
            {
                var imageUri = new Uri(url);
                var referer = $"{imageUri.Scheme}://{imageUri.Host}/";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Referer", referer);
                request.Headers.Add("Accept", "image/avif,image/webp,image/apng,image/*;q=0.8");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await contentStream.CopyToAsync(fileStream);
            }

             
            var file = Gio.FileHelper.NewForPath(cachePath);
            var texture = Gdk.Texture.NewFromFile(file);

            GLib.Functions.IdleAdd(0, () =>
            {
                _coverImage.SetPaintable(texture);
                return false;
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Cover] Falha ao carregar capa: {ex.Message}");
        }
    }

    private async void PasteFromClipboardAsync()
    {
        try
        {
            var display = Gdk.Display.GetDefault();
            if (display == null) return;

            var clipboard = display.GetClipboard();
            var text = await clipboard.ReadTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                _urlEntry.SetText(trimmed);
                _vm.MangaUrl = trimmed;
            }
        }
        catch
        {
             
        }
    }

    private async Task ChooseOutputFolderAsync(Adw.ActionRow row)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Escolher pasta de download");
        try
        {
            var file = await dialog.SelectFolderAsync(_window);
            if (file != null)
            {
                var path = file.GetPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _vm.OutputDirectory = path;
                    row.SetSubtitle(path);
                    _outputDirLabel.SetText(path);
                }
            }
        }
        catch
        {
             
        }
    }
}
