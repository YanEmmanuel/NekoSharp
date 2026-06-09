using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class ScraperManagerTests
{
    [Fact]
    public void DiscoverAndRegisterAll_RegistersFlowerMangaDotNet()
    {
        var manager = new ScraperManager();

        manager.DiscoverAndRegisterAll();

        var scraper = manager.GetScraperForUrl("https://flowermanga.org/manga/please-bully-me-miss-villainess/");

        Assert.NotNull(scraper);
        Assert.Equal("FlowerManga.net", scraper.Name);
    }

    [Fact]
    public void DiscoverAndRegisterAll_WithDynamicProvidersPackage_RegistersFlowerMangaDotNetOrg()
    {
        var manager = new ScraperManager();

        manager.DiscoverAndRegisterAll(externalAssemblyPaths: [FindDynamicProvidersPackage()]);

        var scraper = manager.GetScraperForUrl("https://flowermanga.org/manga/please-bully-me-miss-villainess/");

        Assert.NotNull(scraper);
        Assert.Equal("FlowerManga.net", scraper.Name);
    }

    [Fact]
    public void DiscoverAndRegisterAll_DoesNotReplaceAuthenticatedProviderWithLegacyVersion()
    {
        var manager = new ScraperManager();

        manager.DiscoverAndRegisterAll(externalAssemblyPaths: [FindDynamicProvidersPackage()]);

        var scraper = manager.GetScraperByName("Little Tyrant");

        Assert.NotNull(scraper);
        Assert.IsAssignableFrom<IInteractiveAuthProvider>(scraper);
        Assert.IsAssignableFrom<IAuthenticatedRequestProvider>(scraper);
    }

    private static string FindDynamicProvidersPackage()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            var packagePath = Path.Combine(current.FullName, "providers", "NekoSharp.DynamicProviders.dll");
            if (File.Exists(packagePath))
                return packagePath;

            current = current.Parent;
        }

        throw new FileNotFoundException("Pacote de providers dinamicos nao encontrado.");
    }
}
