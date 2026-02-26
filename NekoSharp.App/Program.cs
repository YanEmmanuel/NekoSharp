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

        var application = Adw.Application.New(AppId, Gio.ApplicationFlags.FlagsNone);

        application.OnActivate += (sender, args) =>
        {
            LoadCss();

            var styleManager = Adw.StyleManager.GetDefault();
            styleManager.ColorScheme = Adw.ColorScheme.Default;

            var logService = new LogService();
            var settingsStore = new SettingsStore(logService: logService);
            var cfStore = new CloudflareCredentialStore(logService: logService);
            var scraperManager = new ScraperManager();
            var downloadService = new DownloadService(scraperManager, logService: logService, cfStore: cfStore, settingsStore: settingsStore);
            var viewModel = new MainWindowViewModel(scraperManager, downloadService, logService, settingsStore);
            var providerUpdateService = new ProviderUpdateService(settingsStore, logService: logService);

            scraperManager.DiscoverAndRegisterAll(logService, cfStore, providerUpdateService.GetInstalledProviderAssemblies());

            var window = new MainWindow(viewModel, (Adw.Application)sender, logService);
            window.Present();

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await providerUpdateService.UpdateProvidersAsync();
                    logService.Info($"[ProviderUpdate] {result.Message}");

                    if (result.DownloadedCount > 0)
                    {
                        logService.Info("[ProviderUpdate] Novos providers foram baixados. Reinicie o app para carregar as novas versões.");
                    }
                }
                catch (Exception ex)
                {
                    logService.Warn($"[ProviderUpdate] Falha ao verificar atualizações dinâmicas: {ex.Message}");
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
