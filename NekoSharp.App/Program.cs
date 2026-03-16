using NekoSharp.App.ViewModels;
using NekoSharp.App.Views;
using NekoSharp.Core.Services;

namespace NekoSharp.App;

class Program
{
    private const string AppId = "io.github.nekosharp";
    private const string NativeSmokeArg = "--native-smoke";
    private const int StartupFailureExitCode = 1;
    private const int NativeSmokeFailureExitCode = 2;

    public static int Main(string[] args)
    {
        try
        {
            WindowsGtkBootstrap.Configure();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Startup] Windows GTK bootstrap failed: {ex.Message}");
            return StartupFailureExitCode;
        }

        if (args.Any(arg => string.Equals(arg, NativeSmokeArg, StringComparison.OrdinalIgnoreCase)))
        {
            return RunNativeSmoke();
        }

        var logService = new LogService();
        var settingsStore = new SettingsStore(logService: logService);
        var cfStore = new CloudflareCredentialStore(logService: logService);
        var libraryStore = new LibraryStore(logService: logService);
        var scraperManager = new ScraperManager();
        var providerUpdateService = new ProviderUpdateService(settingsStore, logService: logService);
        var installedProviderAssemblies = providerUpdateService.GetInstalledProviderAssemblies();
        scraperManager.DiscoverAndRegisterAll(logService, cfStore, installedProviderAssemblies);

        var application = Adw.Application.New(AppId, Gio.ApplicationFlags.FlagsNone);
        var providerUpdateStarted = 0;

        application.OnActivate += (sender, args) =>
        {
            LoadCss();

            var styleManager = Adw.StyleManager.GetDefault();
            styleManager.ColorScheme = Adw.ColorScheme.Default;

            var downloadService = new DownloadService(scraperManager, logService: logService, cfStore: cfStore, settingsStore: settingsStore);
            var libraryService = new MangaLibraryService(scraperManager, downloadService, libraryStore, logService);
            var viewModel = new MainWindowViewModel(scraperManager, downloadService, libraryService, logService, settingsStore, cfStore);
            if (viewModel.RefreshMediocreAuthStateCommand.CanExecute(null))
                viewModel.RefreshMediocreAuthStateCommand.Execute(null);

            var window = new MainWindow(viewModel, (Adw.Application)sender, logService);
            window.Present();

            if (Interlocked.Exchange(ref providerUpdateStarted, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    logService.Info("[ProviderUpdate] Verificando atualizações de providers em background...");

                    using var providerUpdateCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var result = await providerUpdateService.UpdateProvidersAsync(providerUpdateCts.Token);
                    logService.Info($"[ProviderUpdate] {result.Message}");

                    if (result.DownloadedCount <= 0)
                        return;

                    var initialPaths = new HashSet<string>(installedProviderAssemblies, StringComparer.OrdinalIgnoreCase);
                    var currentPaths = providerUpdateService.GetInstalledProviderAssemblies();
                    var newlyAvailablePaths = currentPaths
                        .Where(path => !initialPaths.Contains(path))
                        .ToArray();

                    if (newlyAvailablePaths.Length > 0)
                    {
                        scraperManager.DiscoverAndRegisterExternal(logService, cfStore, newlyAvailablePaths);

                        GLib.Functions.IdleAdd(0, () =>
                        {
                            viewModel.NotifyProviderCatalogChanged();
                            return false;
                        });

                        logService.Info("[ProviderUpdate] Novos providers foram carregados sem reiniciar o app.");
                    }
                    else
                    {
                        logService.Info("[ProviderUpdate] Providers atualizados em background. Reinicie o app para carregar as novas versões.");
                    }
                }
                catch (Exception ex)
                {
                    logService.Warn($"[ProviderUpdate] Falha ao verificar atualizações dinâmicas em background: {ex.Message}");
                }
            });
        };

        return application.RunWithSynchronizationContext(args);
    }

    private static int RunNativeSmoke()
    {
        try
        {
            _ = Adw.Application.New($"{AppId}.native-smoke", Gio.ApplicationFlags.FlagsNone);
            Console.WriteLine("[NativeSmoke] GTK/libadwaita loaded successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeSmoke] Failed to initialize GTK/libadwaita: {ex}");
            return NativeSmokeFailureExitCode;
        }
    }

    private static void LoadCss()
    {
        var provider = Gtk.CssProvider.New();

        using var stream = typeof(Program).Assembly.GetManifestResourceStream("NekoSharp.App.Styles.style.css");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            var css = reader.ReadToEnd();
            provider.LoadFromString(css);
        }

        var display = Gdk.Display.GetDefault()!;
        Gtk.StyleContext.AddProviderForDisplay(display, provider, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
    }
}
