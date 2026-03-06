using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MangaOnline;

public sealed class MangaOnlineScraper : WordPressMadaraScraper
{
    public override string Name => "Manga Online";

    public MangaOnlineScraper() : this(null, null) { }

    public MangaOnlineScraper(LogService? logService) : this(logService, null) { }

    public MangaOnlineScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangasonline.blog", logService, cfStore)
    { }
}
