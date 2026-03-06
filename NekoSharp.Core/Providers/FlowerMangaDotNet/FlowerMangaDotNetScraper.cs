using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FlowerMangaDotNet;

public sealed class FlowerMangaDotNetScraper : WordPressMadaraScraper
{
    public override string Name => "FlowerManga.net";

    public FlowerMangaDotNetScraper() : this(null, null) { }

    public FlowerMangaDotNetScraper(LogService? logService) : this(logService, null) { }

    public FlowerMangaDotNetScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://flowermangas.net", logService, cfStore)
    { }
}
