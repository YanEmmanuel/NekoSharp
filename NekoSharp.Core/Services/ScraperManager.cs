using System.Reflection;
using System.Runtime.Loader;
using NekoSharp.Core.Interfaces;

namespace NekoSharp.Core.Services;

public class ScraperManager
{
    private readonly List<IScraper> _scrapers = [];

    public IReadOnlyList<IScraper> Scrapers => _scrapers.AsReadOnly();

    public void DiscoverAndRegisterAll(
        LogService? logService = null,
        CloudflareCredentialStore? cfStore = null,
        IEnumerable<string>? externalAssemblyPaths = null)
    {
        DiscoverAndRegisterFromAssembly(
            typeof(ScraperManager).Assembly,
            logService,
            cfStore,
            allowReplaceExisting: false,
            sourceLabel: "interno");

        if (externalAssemblyPaths is null)
            return;

        foreach (var path in externalAssemblyPaths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DiscoverAndRegisterFromAssemblyPath(path, logService, cfStore);
        }
    }

    private void DiscoverAndRegisterFromAssemblyPath(string assemblyPath, LogService? logService, CloudflareCredentialStore? cfStore)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            logService?.Warn($"Provider externo não encontrado: {fullPath}");
            return;
        }

        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            DiscoverAndRegisterFromAssembly(
                assembly,
                logService,
                cfStore,
                allowReplaceExisting: true,
                sourceLabel: Path.GetFileName(fullPath));
        }
        catch (Exception ex)
        {
            logService?.Error($"Falha ao carregar assembly de provider externo: {fullPath}", ex.ToString());
        }
    }

    private void DiscoverAndRegisterFromAssembly(
        Assembly assembly,
        LogService? logService,
        CloudflareCredentialStore? cfStore,
        bool allowReplaceExisting,
        string sourceLabel)
    {
        Type[] scraperTypes;
        try
        {
            scraperTypes = assembly.GetTypes()
                .Where(t => typeof(IScraper).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            scraperTypes = ex.Types
                .Where(t => t is not null && typeof(IScraper).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Cast<Type>()
                .ToArray();

            foreach (var loaderEx in ex.LoaderExceptions)
                logService?.Warn($"Falha parcial ao ler tipos de providers ({sourceLabel}): {loaderEx?.Message}");
        }

        foreach (var type in scraperTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try
            {
                var scraper = CreateScraperInstance(type, logService, cfStore);
                if (scraper is null)
                    continue;

                var registered = RegisterOrReplace(scraper, allowReplaceExisting, out var replacedName);
                if (!registered)
                {
                    logService?.Warn($"Provider duplicado ignorado: {scraper.Name}");
                    continue;
                }

                if (replacedName is null)
                    logService?.Info($"Provider registrado: {scraper.Name} ({sourceLabel})");
                else
                    logService?.Info($"Provider atualizado: {replacedName} ({sourceLabel})");
            }
            catch (Exception ex)
            {
                logService?.Error($"Falha ao instanciar provider {type.FullName}", ex.ToString());
            }
        }
    }

    private static IScraper? CreateScraperInstance(Type type, LogService? logService, CloudflareCredentialStore? cfStore)
    {
        var ctorFull = type.GetConstructor([typeof(LogService), typeof(CloudflareCredentialStore)]);
        if (ctorFull is not null)
            return Activator.CreateInstance(type, logService, cfStore) as IScraper;

        var ctorWithLog = type.GetConstructor([typeof(LogService)]);
        if (ctorWithLog is not null)
            return Activator.CreateInstance(type, logService) as IScraper;

        return Activator.CreateInstance(type) as IScraper;
    }

    private bool RegisterOrReplace(IScraper scraper, bool allowReplace, out string? replacedName)
    {
        replacedName = null;
        var index = _scrapers.FindIndex(s => s.Name.Equals(scraper.Name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            if (!allowReplace)
                return false;

            replacedName = _scrapers[index].Name;
            _scrapers[index] = scraper;
            return true;
        }

        _scrapers.Add(scraper);
        return true;
    }

    public void Register(IScraper scraper)
    {
        if (_scrapers.Any(s => s.Name.Equals(scraper.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Scraper '{scraper.Name}' já está registrado.");

        _scrapers.Add(scraper);
    }

    public bool TryRegister(IScraper scraper)
    {
        if (_scrapers.Any(s => s.Name.Equals(scraper.Name, StringComparison.OrdinalIgnoreCase)))
            return false;

        _scrapers.Add(scraper);
        return true;
    }

    public bool Unregister(string name)
    {
        var scraper = _scrapers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return scraper is not null && _scrapers.Remove(scraper);
    }

    public IScraper? GetScraperForUrl(string url)
    {
        return _scrapers.FirstOrDefault(s => s.CanHandle(url));
    }

    public bool CanHandle(string url)
    {
        return _scrapers.Any(s => s.CanHandle(url));
    }
}
