using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PlumaComics;

public sealed class PlumaComicsScraper : MangaThemesiaScraper
{
    public override string Name => "Pluma Comics";

    public PlumaComicsScraper() : this(null, null) { }

    public PlumaComicsScraper(LogService? logService) : this(logService, null) { }

    public PlumaComicsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://plumacomics.cloud", logService, cfStore)
    { }
}
