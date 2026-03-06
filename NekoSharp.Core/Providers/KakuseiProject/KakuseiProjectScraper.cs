using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.KakuseiProject;

public sealed class KakuseiProjectScraper : WordPressMadaraScraper
{
    public override string Name => "Kakusei Project";

    public KakuseiProjectScraper() : this(null, null) { }

    public KakuseiProjectScraper(LogService? logService) : this(logService, null) { }

    public KakuseiProjectScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://kakuseiproject.com", logService, cfStore)
    { }
}
