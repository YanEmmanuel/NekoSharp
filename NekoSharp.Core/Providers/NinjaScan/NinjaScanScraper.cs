using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.NinjaScan;

public sealed class NinjaScanScraper : WordPressMadaraScraper
{
    public override string Name => "Ninja Scan";

    public NinjaScanScraper() : this(null, null) { }

    public NinjaScanScraper(LogService? logService) : this(logService, null) { }

    public NinjaScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://ninjacomics.xyz", logService, cfStore)
    { }
}
