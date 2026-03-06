using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Hipercool;

public sealed class HipercoolScraper : WordPressMadaraScraper
{
    public override string Name => "HipercooL";

    public HipercoolScraper() : this(null, null) { }

    public HipercoolScraper(LogService? logService) : this(logService, null) { }

    public HipercoolScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hiper.cool", logService, cfStore)
    { }
}
