using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Ler999;

public sealed class Ler999Scraper : ZeistMangaScraper
{
    public override string Name => "Ler 999";

    public Ler999Scraper() : this(null, null) { }

    public Ler999Scraper(LogService? logService) : this(logService, null) { }

    public Ler999Scraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://ler999.blogspot.com", logService, cfStore)
    { }
}
