using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.LerMangas;

public sealed class LerMangasScraper : WordPressMadaraScraper
{
    public override string Name => "Ler Mangas";

    public LerMangasScraper() : this(null, null) { }

    public LerMangasScraper(LogService? logService) : this(logService, null) { }

    public LerMangasScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://lermangas.me", logService, cfStore)
    { }
}
