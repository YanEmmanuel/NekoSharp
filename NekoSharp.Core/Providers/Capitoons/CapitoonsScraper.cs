using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Capitoons;

public sealed class CapitoonsScraper : MangaThemesiaScraper
{
    public override string Name => "Capitoons";

    public CapitoonsScraper() : this(null, null) { }

    public CapitoonsScraper(LogService? logService) : this(logService, null) { }

    public CapitoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://capitoons.com", logService, cfStore)
    { }
}
