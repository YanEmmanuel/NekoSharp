using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Amuy;

public sealed class AmuyScraper : WordPressMadaraScraper
{
    public override string Name => "Amuy";

    public AmuyScraper() : this(null, null) { }

    public AmuyScraper(LogService? logService) : this(logService, null) { }

    public AmuyScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://apenasmaisumyaoi.com", logService, cfStore)
    { }
}
