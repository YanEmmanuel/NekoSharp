namespace NekoSharp.App;

internal static class WindowsGtkBootstrap
{
    public static void Configure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appDir = AppContext.BaseDirectory;
        var gtkRoot = Path.Combine(appDir, "gtk");
        var gtkBin = Path.Combine(gtkRoot, "bin");
        if (!Directory.Exists(gtkBin))
        {
            throw new DirectoryNotFoundException(
                $"GTK runtime not found at '{gtkBin}'. Ensure the portable package keeps a sibling 'gtk' folder next to the executable.");
        }

        PrependEnvironmentPath("PATH", gtkBin);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(gtkRoot, "share"));
        Environment.SetEnvironmentVariable("GSETTINGS_SCHEMA_DIR", Path.Combine(gtkRoot, "share", "glib-2.0", "schemas"));
        Environment.SetEnvironmentVariable("GTK_DATA_PREFIX", gtkRoot);
        Environment.SetEnvironmentVariable("GIO_MODULE_DIR", Path.Combine(gtkRoot, "lib", "gio", "modules"));

        var gdkPixbufModuleDir = ResolveGdkPixbufModuleDir(gtkRoot);
        if (gdkPixbufModuleDir is null)
        {
            var searchedRoot = Path.Combine(gtkRoot, "lib", "gdk-pixbuf-2.0");
            throw new DirectoryNotFoundException($"Could not locate GDK Pixbuf loader modules under '{searchedRoot}'.");
        }

        Environment.SetEnvironmentVariable("GDK_PIXBUF_MODULEDIR", gdkPixbufModuleDir);

        var loadersCache = Path.Combine(gdkPixbufModuleDir, "loaders.cache");
        if (File.Exists(loadersCache))
        {
            Environment.SetEnvironmentVariable("GDK_PIXBUF_MODULE_FILE", loadersCache);
        }
    }

    private static string? ResolveGdkPixbufModuleDir(string gtkRoot)
    {
        var pixbufRoot = Path.Combine(gtkRoot, "lib", "gdk-pixbuf-2.0");
        if (!Directory.Exists(pixbufRoot))
        {
            return null;
        }

        var candidateLoaders = Directory.GetDirectories(pixbufRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(versionDir => Path.Combine(versionDir, "loaders"))
            .Where(Directory.Exists)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidateLoaders.Length > 0)
        {
            return candidateLoaders[0];
        }

        var fallback = Path.Combine(pixbufRoot, "2.10.0", "loaders");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static void PrependEnvironmentPath(string variableName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var currentValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            Environment.SetEnvironmentVariable(variableName, value);
            return;
        }

        var entries = currentValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(variableName, $"{value};{currentValue}");
    }
}
