using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Inkapk;

public sealed class InkapkScraper : WordPressMadaraScraper
{
    public override string Name => "Inkapk";

    public InkapkScraper() : this(null, null) { }

    public InkapkScraper(LogService? logService) : this(logService, null) { }

    public InkapkScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://inkapk.net", logService, cfStore)
    { }
}
