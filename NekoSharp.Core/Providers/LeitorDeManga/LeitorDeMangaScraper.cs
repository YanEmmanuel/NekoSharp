using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.LeitorDeManga;

public sealed class LeitorDeMangaScraper : WordPressMadaraScraper
{
    public override string Name => "Leitor de Mangá";

    public LeitorDeMangaScraper() : this(null, null) { }

    public LeitorDeMangaScraper(LogService? logService) : this(logService, null) { }

    public LeitorDeMangaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://leitordemanga.com", logService, cfStore)
    { }
}
