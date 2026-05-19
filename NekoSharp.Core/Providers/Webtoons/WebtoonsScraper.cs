using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;

namespace NekoSharp.Core.Providers.Webtoons;

public sealed partial class WebtoonsScraper : HtmlScraperBase
{
    private const string DesktopBaseUrl = "https://www.webtoons.com";
    private const string MobileBaseUrl = "https://m.webtoons.com";

    public override string Name => "Webtoons";

    protected override IReadOnlyCollection<string> SupportedHosts =>
        ["webtoons.com", "www.webtoons.com", "m.webtoons.com"];

    public WebtoonsScraper() : this(null, null) { }

    public WebtoonsScraper(LogService? logService) : this(logService, null) { }

    public WebtoonsScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base(DesktopBaseUrl, logService, cfStore)
    {
        Http.DefaultRequestHeaders.Remove("Accept");
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var document = await LoadDocumentAsync(CreateHtmlRequest(parsed.AbsoluteUrl, parsed.LanguageCode), ct);

        var detailElement = document.QuerySelector(".detail_header .info");
        var infoElement = document.QuerySelector("#_asideDetail");
        var title = GetFirstText(document, "h1.subj", "h3.subj") ?? $"Webtoons {parsed.TitleId}";
        var author = GetAuthor(detailElement);
        var artist = GetArtist(detailElement, author);
        var genre = string.Join(", ", document.QuerySelectorAll(".detail_header .info .genre")
            .Select(node => node.TextContent?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        var description = infoElement?.QuerySelector("p.summary")?.TextContent?.Trim() ?? string.Empty;
        var statusText = infoElement?.QuerySelector("p.day_info")?.TextContent?.Trim() ?? string.Empty;
        var canonicalUrl = document.QuerySelector("meta[property='og:url']")?.GetAttribute("content")
            ?? parsed.AbsoluteUrl;
        var coverUrl = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content") ?? string.Empty;

        return new Manga
        {
            Name = WebUtility.HtmlDecode(title).Trim(),
            CoverUrl = coverUrl,
            Description = description,
            Url = canonicalUrl,
            SiteName = Name,
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var requestUrl = BuildEpisodeListApiUrl(parsed);
        using var request = CreateJsonRequest(requestUrl, parsed.LanguageCode, mobile: true);
        var response = await SendJsonWithRetryAsync(request, ct);
        var payload = Deserialize<WebtoonsResultDto<WebtoonsEpisodeListDto>>(response);

        return BuildChapterList(payload.Result.EpisodeList);
    }

    public override async Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var parsed = ParseSupportedUrl(chapter.Url);
        var document = await LoadDocumentAsync(CreateHtmlRequest(parsed.AbsoluteUrl, parsed.LanguageCode), ct);

        var pages = document.QuerySelectorAll("div#_imageList > img")
            .Select((element, index) => new { element, index })
            .Select(x => BuildImagePage(x.element, chapter.Url, x.index + 1))
            .Where(page => page is not null)
            .Cast<Page>()
            .ToList();

        if (pages.Count == 0)
            pages.AddRange(await FetchMotionToonPagesAsync(document, chapter.Url, ct));

        if (pages.Count == 0)
            throw new InvalidOperationException("Nao foi possivel localizar as imagens do episodio no Webtoons.");

        return pages;
    }

    internal static string BuildEpisodeListApiUrl(WebtoonsUrlRef parsed)
    {
        var typeSegment = parsed.SeriesType == WebtoonsSeriesType.Canvas ? "canvas" : "webtoon";
        return $"{MobileBaseUrl}/api/v1/{typeSegment}/{parsed.TitleId}/episodes?pageSize=99999";
    }

    internal static List<Chapter> BuildChapterList(IReadOnlyList<WebtoonsEpisodeDto> episodes)
    {
        var items = episodes.Select(MapEpisode).ToList();
        if (items.Count == 0)
            return [];

        var recognized = 0;
        var unrecognized = 0;

        foreach (var item in items)
        {
            var match = EpisodeNumberRegex().Match(item.Title);
            if (match.Success && string.IsNullOrWhiteSpace(match.Groups[6].Value))
            {
                item.ChapterNumber = float.TryParse(
                    match.Groups[11].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedNumber)
                    ? parsedNumber
                    : -1f;
                item.SeasonNumber = int.TryParse(match.Groups[4].Value, out var parsedSeason)
                    ? parsedSeason
                    : 1;
            }

            if (item.ChapterNumber < 0f)
                unrecognized++;
            else
                recognized++;
        }

        if (recognized == 0 || unrecognized > recognized)
        {
            for (var index = 0; index < items.Count; index++)
                items[index].ChapterNumber = items.Count - index;
        }
        else
        {
            var maxChapterNumber = 0f;
            var currentSeason = 1;
            var seasonOffset = 0f;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.ChapterNumber >= 0f)
                {
                    var originalNumber = item.ChapterNumber;
                    if (item.SeasonNumber > currentSeason)
                    {
                        currentSeason = item.SeasonNumber;
                        if (originalNumber <= maxChapterNumber)
                            seasonOffset = maxChapterNumber;
                    }

                    item.ChapterNumber = seasonOffset + originalNumber;
                    maxChapterNumber = Math.Max(maxChapterNumber, item.ChapterNumber);
                }
                else
                {
                    item.ChapterNumber = index == 0
                        ? 0f
                        : items[index - 1].ChapterNumber + 0.01f;
                }
            }
        }

        return items.Select(item => new Chapter
        {
            Title = item.HasBgm ? $"{item.Title} ♫" : item.Title,
            Number = item.ChapterNumber,
            Url = item.ViewerLink,
        }).ToList();
    }

    private static WebtoonsEpisodeWorkItem MapEpisode(WebtoonsEpisodeDto episode)
    {
        var title = WebUtility.HtmlDecode(episode.EpisodeTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "Episode";

        var viewerLink = NormalizeViewerLink(episode.ViewerLink);
        return new WebtoonsEpisodeWorkItem(
            title,
            viewerLink,
            episode.ExposureDateMillis,
            episode.HasBgm);
    }

    private static string NormalizeViewerLink(string viewerLink)
    {
        if (string.IsNullOrWhiteSpace(viewerLink))
            return string.Empty;

        if (Uri.TryCreate(viewerLink, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return absolute.ToString();

        return Uri.TryCreate(new Uri($"{DesktopBaseUrl}/"), viewerLink, out var combined)
            ? combined.ToString()
            : viewerLink;
    }

    private Page? BuildImagePage(IElement element, string refererUrl, int number)
    {
        var imageUrl = ExtractImageSource(element, refererUrl);
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        return new Page
        {
            Number = number,
            ImageUrl = imageUrl,
            RefererUrl = refererUrl,
        };
    }

    private async Task<List<Page>> FetchMotionToonPagesAsync(IDocument document, string refererUrl, CancellationToken ct)
    {
        var html = document.DocumentElement?.OuterHtml ?? string.Empty;
        var documentUrlMatch = MotionToonDocumentUrlRegex().Match(html);
        var motionToonPathMatch = MotionToonPathRegex().Match(html);
        if (!documentUrlMatch.Success || !motionToonPathMatch.Success)
            return [];

        var documentUrl = documentUrlMatch.Groups[1].Value;
        var motionToonPath = motionToonPathMatch.Groups[1].Value;

        using var request = CreateHtmlRequest(documentUrl, string.Empty, acceptJson: true);
        var response = await SendJsonWithRetryAsync(request, ct);
        var payload = Deserialize<WebtoonsMotionToonDto>(response);

        return payload.Assets.Images
            .Where(static entry => entry.Key.Contains("layer", StringComparison.OrdinalIgnoreCase))
            .Select((entry, index) => new Page
            {
                Number = index + 1,
                ImageUrl = motionToonPath + entry.Value,
                RefererUrl = refererUrl,
            })
            .ToList();
    }

    private async Task<string> SendJsonWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await SendForStringAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            using var retry = CloneRequest(request);
            return await SendForStringAsync(retry, ct);
        }
    }

    private static T Deserialize<T>(string json)
    {
        var payload = JsonSerializer.Deserialize<T>(json);
        return payload ?? throw new InvalidOperationException("Resposta invalida do Webtoons.");
    }

    private HttpRequestMessage CreateHtmlRequest(string url, string languageCode, bool acceptJson = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(languageCode));
        request.Headers.Referrer = new Uri($"{DesktopBaseUrl}/");
        if (acceptJson)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return request;
    }

    private HttpRequestMessage CreateJsonRequest(string url, string languageCode, bool mobile)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(languageCode));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri((mobile ? MobileBaseUrl : DesktopBaseUrl) + "/");
        return request;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }

    private static string BuildCookieHeader(string languageCode)
    {
        var locale = languageCode.ToLowerInvariant() switch
        {
            "zh-hant" => "zh_TW",
            "" => "en",
            _ => languageCode,
        };

        return $"ageGatePass=true; locale={locale}; needGDPR=false";
    }

    private static string GetAuthor(IElement? detailElement)
    {
        var authors = detailElement?.QuerySelectorAll(".author")
            .Select(node => node.TextContent?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (authors is { Length: > 0 })
            return authors[0]!;

        return detailElement?.QuerySelector(".author_area")?.TextContent?.Trim() ?? string.Empty;
    }

    private static string GetArtist(IElement? detailElement, string author)
    {
        var authors = detailElement?.QuerySelectorAll(".author")
            .Select(node => node.TextContent?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (authors is { Length: > 1 })
            return authors[1]!;

        return detailElement?.QuerySelector(".author_area")?.TextContent?.Trim() ?? author;
    }

    private static WebtoonsUrlRef ParseSupportedUrl(string url)
    {
        if (!WebtoonsUrlParser.TryParse(url, out var parsed))
            throw new ArgumentException("URL do Webtoons invalida. Use uma URL da serie ou do episodio com title_no.", nameof(url));

        return parsed;
    }

    [GeneratedRegex("""(?:(s(eason)?|saison|part|vol(ume)?)\s*\.?\s*(\d+).*?)?(.*?(mini|bonus|special).*?)?(e(p(isode)?)?|ch(apter)?)\s*\.?\s*(\d+(\.\d+)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumberRegex();

    [GeneratedRegex("""documentURL:.*?'(.*?)'""", RegexOptions.IgnoreCase)]
    private static partial Regex MotionToonDocumentUrlRegex();

    [GeneratedRegex("""jpg:.*?'(.*?)\{""", RegexOptions.IgnoreCase)]
    private static partial Regex MotionToonPathRegex();

    private sealed class WebtoonsEpisodeWorkItem(
        string title,
        string viewerLink,
        long exposureDateMillis,
        bool hasBgm)
    {
        public string Title { get; } = title;
        public string ViewerLink { get; } = viewerLink;
        public long ExposureDateMillis { get; } = exposureDateMillis;
        public bool HasBgm { get; } = hasBgm;
        public float ChapterNumber { get; set; } = -1f;
        public int SeasonNumber { get; set; } = 1;
    }
}
