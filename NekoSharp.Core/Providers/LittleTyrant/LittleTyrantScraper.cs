using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.LittleTyrant;

public sealed class LittleTyrantScraper : WordPressMadaraScraper
{
    public override string Name => "Little Tyrant";

    protected override string MangaDescriptionSelector => "div.manga-summary, div.summary__content p";

    public LittleTyrantScraper() : this(null, null) { }

    public LittleTyrantScraper(LogService? logService) : this(logService, null) { }

    public LittleTyrantScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://tiraninha.world", logService, cfStore)
    { }
}
