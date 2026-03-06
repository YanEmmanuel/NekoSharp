using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.MaidSecret;

public sealed class MaidSecretScraper : WordPressMadaraScraper
{
    public override string Name => "Maid Secret";

    public MaidSecretScraper() : this(null, null) { }

    public MaidSecretScraper(LogService? logService) : this(logService, null) { }

    public MaidSecretScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://maidsecret.com", logService, cfStore)
    { }
}
