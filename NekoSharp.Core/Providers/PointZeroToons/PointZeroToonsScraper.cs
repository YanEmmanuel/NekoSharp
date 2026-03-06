using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.PointZeroToons;

public sealed class PointZeroToonsScraper : MangaThemesiaScraper
{
    private static readonly Regex JsonImageListRegex = new("\"images\"\\s*:\\s*(\\[.*?])", RegexOptions.Singleline | RegexOptions.Compiled);

    public override string Name => "Point Zero Toons";
    protected override string SeriesThumbnailSelector => ".tx-hero-cover > img.wp-post-image";

    public PointZeroToonsScraper() : this(null, null) { }

    public PointZeroToonsScraper(LogService? logService) : this(logService, null) { }

    public PointZeroToonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://kitsuneyako.com", logService, cfStore)
    { }

    protected override List<Page> ParsePages(IDocument document, string chapterUrl)
    {
        var scriptSrc = document.QuerySelector("script[src^='data:text/javascript;base64,']")?.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(scriptSrc) || !scriptSrc.Contains("base64,", StringComparison.OrdinalIgnoreCase))
            return base.ParsePages(document, chapterUrl);

        var encoded = scriptSrc[(scriptSrc.IndexOf("base64,", StringComparison.OrdinalIgnoreCase) + "base64,".Length)..];
        var data = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var match = JsonImageListRegex.Match(data);
        if (!match.Success)
            return base.ParsePages(document, chapterUrl);

        using var json = JsonDocument.Parse(match.Groups[1].Value);
        return json.RootElement.EnumerateArray()
            .Select((item, index) => new Page
            {
                Number = index + 1,
                ImageUrl = item.GetString() ?? string.Empty
            })
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .ToList();
    }
}
