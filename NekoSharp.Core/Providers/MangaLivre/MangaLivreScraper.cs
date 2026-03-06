using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangaLivre;

public sealed class MangaLivreScraper : WordPressMadaraScraper
{
    public override string Name => "Manga Livre";

    public MangaLivreScraper() : this(null, null) { }

    public MangaLivreScraper(LogService? logService) : this(logService, null) { }

    public MangaLivreScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangalivre.tv", logService, cfStore)
    { }
}
