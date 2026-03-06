using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.CeriseScan;

public sealed class CeriseScanScraper : WordPressMadaraScraper
{
    private static readonly Regex PageRegex = new(@"content:\s+(\[[\s\S]+\])", RegexOptions.Compiled);

    public override string Name => "Cerise Scan";

    public CeriseScanScraper() : this(null, null) { }

    public CeriseScanScraper(LogService? logService) : this(logService, null) { }

    public CeriseScanScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://loverstoon.com", logService, cfStore)
    { }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var html = await Http.GetStringAsync(chapter.Url, ct);
        var document = await Browser.OpenAsync(req => req.Content(html).Address(chapter.Url), ct);

        var script = document.QuerySelector(".page-break script")?.TextContent ?? string.Empty;
        var match = PageRegex.Match(script);
        if (!match.Success)
            return await base.GetPagesAsync(chapter, ct);

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
