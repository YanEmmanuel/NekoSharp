using NekoSharp.App.ViewModels;
using NekoSharp.App.Views;
using NekoSharp.Core.Services;

namespace NekoSharp.App;

class Program
{
    public static int Main(string[] args)
    {
        var application = Adw.Application.New("io.github.nekosharp", Gio.ApplicationFlags.FlagsNone);

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

            scraperManager.DiscoverAndRegisterAll(logService, cfStore);

            var window = new MainWindow(viewModel, (Adw.Application)sender, logService);
            window.Present();
        };

        return application.RunWithSynchronizationContext(args);
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
