using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Remangas;

public sealed class RemangasScraper : WordPressMadaraScraper
{
    public override string Name => "Remangas";

    public RemangasScraper() : this(null, null) { }

    public RemangasScraper(LogService? logService) : this(logService, null) { }

    public RemangasScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://remangas.net", logService, cfStore)
    { }
}
