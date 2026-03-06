using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Atemporal;

public sealed class AtemporalScraper : MangaThemesiaScraper
{
    public override string Name => "Atemporal";

    public AtemporalScraper() : this(null, null) { }

    public AtemporalScraper(LogService? logService) : this(logService, null) { }

    public AtemporalScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://atemporal.cloud", logService, cfStore)
    { }
}
