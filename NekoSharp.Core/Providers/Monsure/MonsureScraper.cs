using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Monsure;

public sealed class MonsureScraper : WordPressMadaraScraper
{
    public override string Name => "Monsure";

    public MonsureScraper() : this(null, null) { }

    public MonsureScraper(LogService? logService) : this(logService, null) { }

    public MonsureScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://monsuresu.com", logService, cfStore)
    { }
}
