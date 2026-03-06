using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangasBrasuka;

public sealed class MangasBrasukaScraper : WordPressMadaraScraper
{
    public override string Name => "Mangas Brasuka";

    public MangasBrasukaScraper() : this(null, null) { }

    public MangasBrasukaScraper(LogService? logService) : this(logService, null) { }

    public MangasBrasukaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangasbrasuka.com.br", logService, cfStore)
    { }
}
