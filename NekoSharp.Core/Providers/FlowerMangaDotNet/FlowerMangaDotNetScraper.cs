using System.Globalization;
using System.Text.Json;
using AngleSharp;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.FlowerMangaDotNet;

public sealed class FlowerMangaDotNetScraper : WordPressMadaraScraper
{
    public override string Name => "FlowerManga.net";
    protected override bool UseNewChapterEndpoint => false;

    public FlowerMangaDotNetScraper() : this(null, null) { }

    public FlowerMangaDotNetScraper(LogService? logService) : this(logService, null) { }

    public FlowerMangaDotNetScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://flowermanga.org", logService, cfStore)
    { }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(url, ct);
        var document = await Browser.OpenAsync(request => request.Content(html).Address(url), ct);
        var chaptersJson = document.QuerySelector("#mk-chapters-data")?.TextContent;

        return string.IsNullOrWhiteSpace(chaptersJson)
            ? await base.GetChaptersAsync(url, ct)
            : ParseMkChapters(chaptersJson);
    }

    internal static List<Chapter> ParseMkChapters(string chaptersJson)
    {
        using var document = JsonDocument.Parse(chaptersJson);
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<Chapter>();
        foreach (var item in items.EnumerateArray())
        {
            var url = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var title = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var numberText = item.TryGetProperty("num", out var numberElement) ? numberElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                continue;

            _ = double.TryParse(numberText, NumberStyles.Any, CultureInfo.InvariantCulture, out var number);
            chapters.Add(new Chapter { Number = number, Title = title, Url = url });
        }

        return chapters;
    }
}
