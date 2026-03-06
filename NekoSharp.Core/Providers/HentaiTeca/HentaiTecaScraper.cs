using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HentaiTeca;

public sealed class HentaiTecaScraper : WordPressMadaraScraper
{
    public override string Name => "Hentai Teca";

    public HentaiTecaScraper() : this(null, null) { }

    public HentaiTecaScraper(LogService? logService) : this(logService, null) { }

    public HentaiTecaScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://hentaiteca.net", logService, cfStore)
    { }
}
