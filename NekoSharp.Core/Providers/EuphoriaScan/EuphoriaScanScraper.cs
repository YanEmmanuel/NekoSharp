using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.EuphoriaScan;

public sealed class EuphoriaScanScraper : WordPressMadaraScraper
{
    public override string Name => "Euphoria Scan";

    public EuphoriaScanScraper() : this(null, null) { }

    public EuphoriaScanScraper(LogService? logService) : this(logService, null) { }

    public EuphoriaScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://euphoriascan.com", logService, cfStore)
    { }
}
