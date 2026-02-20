using NekoSharp.Core.Interfaces;

namespace NekoSharp.Core.Services;

 
 
 
public class ScraperManager
{
    private readonly List<IScraper> _scrapers = [];

     
     
     
    public IReadOnlyList<IScraper> Scrapers => _scrapers.AsReadOnly();

     
     
     
    public void DiscoverAndRegisterAll(LogService? logService = null, CloudflareCredentialStore? cfStore = null)
    {
        var assembly = typeof(ScraperManager).Assembly;
        var scraperTypes = assembly.GetTypes()
            .Where(t => typeof(IScraper).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

        foreach (var type in scraperTypes)
        {
            try
            {
                IScraper? scraper = null;

                var ctorFull = type.GetConstructor([typeof(LogService), typeof(CloudflareCredentialStore)]);
                if (ctorFull is not null)
                {
                    scraper = Activator.CreateInstance(type, logService, cfStore) as IScraper;
                }
                else
                {
                    var ctorWithLog = type.GetConstructor([typeof(LogService)]);
                    scraper = ctorWithLog is not null
                        ? Activator.CreateInstance(type, logService) as IScraper
                        : Activator.CreateInstance(type) as IScraper;
                }

                if (scraper != null)
                {
                    if (TryRegister(scraper))
                        logService?.Info($"Provider registered: {scraper.Name}");
                    else
                        logService?.Warn($"Duplicate provider skipped: {scraper.Name}");
                }
            }
            catch (Exception ex)
            {
                logService?.Error($"Failed to instantiate provider {type.FullName}", ex.ToString());
            }
        }
    }

     
     
     
    public void Register(IScraper scraper)
    {
        if (_scrapers.Any(s => s.Name == scraper.Name))
            throw new InvalidOperationException($"Scraper '{scraper.Name}' is already registered.");
        
        _scrapers.Add(scraper);
    }

     
     
     
    public bool TryRegister(IScraper scraper)
    {
        if (_scrapers.Any(s => s.Name == scraper.Name))
            return false;

        _scrapers.Add(scraper);
        return true;
    }

     
     
     
    public bool Unregister(string name)
    {
        var scraper = _scrapers.FirstOrDefault(s => s.Name == name);
        return scraper != null && _scrapers.Remove(scraper);
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
