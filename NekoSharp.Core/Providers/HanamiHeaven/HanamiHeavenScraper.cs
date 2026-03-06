using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HanamiHeaven;

public sealed class HanamiHeavenScraper : WordPressMadaraScraper
{
    public override string Name => "Hanami Heaven";

    public HanamiHeavenScraper() : this(null, null) { }

    public HanamiHeavenScraper(LogService? logService) : this(logService, null) { }

    public HanamiHeavenScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hanamiheaven.org", logService, cfStore)
    { }
}
