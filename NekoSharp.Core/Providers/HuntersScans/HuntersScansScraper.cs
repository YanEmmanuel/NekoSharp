using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Helpers;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.HuntersScans;

public sealed class HuntersScansScraper : WordPressMadaraScraper
{
    private static readonly Regex PayloadRegex = new(@"payload:\s*""(.*?)""", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new(@"sk:\s*""(.*?)""", RegexOptions.Compiled);

    public override string Name => "Hunters Scans";

    public HuntersScansScraper() : this(null, null) { }

    public HuntersScansScraper(LogService? logService) : this(logService, null) { }

    public HuntersScansScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://readhunters.xyz", logService, cfStore)
    { }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var all = new List<Chapter>();
        var page = 1;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/ajax/chapters?t={page}");
            request.Headers.Referrer = new Uri(url);

            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);
            var document = await Browser.OpenAsync(req => req.Content(html).Address(url), ct);
            var current = document.QuerySelectorAll(ChaptersSelector)
                .Select(node => new Chapter
                {
                    Title = node.TextContent.Trim(),
                    Url = node.GetAttribute("href") ?? string.Empty,
                    Number = ChapterHelper.ExtractChapterNumber(node.TextContent.Trim())
                })
                .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Url))
                .ToList();

            if (current.Count == 0)
                break;

            all.AddRange(current);
            page++;
        }

        return all;
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var html = await Http.GetStringAsync(chapter.Url, ct);
        var document = await Browser.OpenAsync(req => req.Content(html).Address(chapter.Url), ct);
        var script = document.QuerySelector("script")?.TextContent ?? string.Empty;
        var payloadMatch = PayloadRegex.Match(script);
        var keyMatch = KeyRegex.Match(script);

        if (!payloadMatch.Success || !keyMatch.Success)
            return await base.GetPagesAsync(chapter, ct);

        var urls = DecryptHuntersPayload(payloadMatch.Groups[1].Value, keyMatch.Groups[1].Value);
        return urls.Select((imageUrl, index) => new Page
        {
            Number = index + 1,
            ImageUrl = imageUrl
        }).ToList();
    }

    private static List<string> DecryptHuntersPayload(string payloadBase64, string keyBase64)
    {
        var payload = Encoding.GetEncoding("ISO-8859-1").GetString(Convert.FromBase64String(payloadBase64));
        var key = Encoding.GetEncoding("ISO-8859-1").GetString(Convert.FromBase64String(keyBase64));
        var builder = new StringBuilder(payload.Length);

        for (var i = 0; i < payload.Length; i++)
        {
            var keyIndex = (i + key.Length - 1) % key.Length;
            builder.Append((char)(payload[i] - key[keyIndex]));
        }

        return JsonSerializer.Deserialize<List<string>>(builder.ToString()) ?? [];
    }
}
