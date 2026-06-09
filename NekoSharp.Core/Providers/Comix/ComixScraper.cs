using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using PuppeteerSharp;

namespace NekoSharp.Core.Providers.Comix;

public sealed class ComixScraper : IScraper
{
    public string Name => "Comix";
    public string BaseUrl => "https://comix.to";

    private const string ApiBaseUrl = "https://comix.to/api/v1/";
    private static readonly HashSet<int> OfficialScanlationGroupIds = [9275, 10702];
    private static readonly Regex RelativeDateRegex = new(
        "^(?<amount>\\d+)\\s*(?<unit>s|m|h|d|w|mo|mos|y|yr|yrs|min|mins|sec|secs|hr|hrs|day|days|week|weeks|month|months|year|years)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterNumberRegex = new(
        @"(?:\bCh(?:apter)?\.?\s*|chapter-)(?<number>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly TimeSpan TokenCaptureTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChapterDomTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;
    private readonly LogService? _log;
    private readonly CloudflareCredentialStore? _cfStore;

    public ComixScraper() : this(null, null) { }

    public ComixScraper(LogService? logService) : this(logService, null) { }

    public ComixScraper(LogService? logService, CloudflareCredentialStore? cfStore)
    {
        _log = logService;
        _cfStore = cfStore;

        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };

        HttpMessageHandler handler = new CloudflareHandler(
            inner: inner,
            logService: logService,
            store: cfStore);

        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(45)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.Default);
        _http.DefaultRequestHeaders.Referrer = new Uri($"{BaseUrl}/");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public bool CanHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("comix.to", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("www.comix.to", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var result = await GetResultAsync($"manga/{Uri.EscapeDataString(parsed.HashId)}", ct);

        var title = GetString(result, "title") ?? $"Comix {parsed.HashId}";
        var slug = GetString(result, "slug");
        var canonicalUrl = GetString(result, "url");
        var coverUrl =
            GetNestedString(result, "poster", "large") ??
            GetNestedString(result, "poster", "medium") ??
            GetNestedString(result, "poster", "small") ??
            string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Description = BuildDescription(result),
            Url = BuildMangaUrl(parsed.HashId, slug, canonicalUrl),
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var mangaUrl = BuildMangaUrl(parsed.HashId, null, $"/title/{parsed.MangaSegment.Trim('/')}");
        var bestById = new Dictionary<int, ComixChapterCandidate>();
        var seenPageSignatures = new HashSet<string>(StringComparer.Ordinal);

        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();
        await PrepareBrowserPageAsync(page, mangaUrl);

        await page.GoToAsync(mangaUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = (int)ChapterDomTimeout.TotalMilliseconds
        });

        for (var pageNumber = 1; pageNumber <= 200; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();

            await page.WaitForSelectorAsync(".mchap-list .mchap-item", new WaitForSelectorOptions
            {
                Timeout = (int)ChapterDomTimeout.TotalMilliseconds
            });

            var snapshot = await CaptureChapterDomSnapshotAsync(page, ct);
            if (snapshot.Items.Count == 0)
                break;

            var pageSignature = string.Join('|', snapshot.Items.Select(static item => item.Href));
            if (!seenPageSignatures.Add(pageSignature))
                break;

            foreach (var item in snapshot.Items)
            {
                var candidate = ParseChapterDomItem(item, mangaUrl);
                if (candidate.ChapterId <= 0)
                    continue;

                if (!bestById.TryGetValue(candidate.ChapterId, out var current) || IsBetterChapter(candidate, current))
                    bestById[candidate.ChapterId] = candidate;
            }

            if (!snapshot.HasNextPage)
                break;

            var firstHref = snapshot.Items[0].Href;
            if (!await ClickNextChapterPageAsync(page))
                break;

            await WaitForChapterPageChangeAsync(page, firstHref, ct);
        }

        var chapters = BuildChapterList(parsed.MangaSegment, bestById.Values);

        _log?.Info($"[Comix] Loaded {chapters.Count} chapters for manga={parsed.HashId}");
        return chapters;
    }

    public async Task<List<NekoSharp.Core.Models.Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var parsed = ParseSupportedUrl(chapter.Url);
        if (parsed.Kind != ComixUrlKind.Chapter || parsed.ChapterId <= 0)
            throw new ArgumentException("Capítulo inválido do Comix. Use uma URL no formato /title/<hash>/<chapterId>.", nameof(chapter));

        var token = await CaptureTokenAsync(
            pageUrl: chapter.Url,
            pathSuffix: $"/api/v1/chapters/{parsed.ChapterId}",
            ct);
        var result = await GetResultAsync(BuildPageListRelativeUrl(parsed.ChapterId, token), ct);

        var imageUrls = ParsePageImageUrls(result);
        if (imageUrls.Count == 0)
            throw new InvalidOperationException($"Capítulo {parsed.ChapterId} não possui imagens.");

        var pages = new List<NekoSharp.Core.Models.Page>();
        var pageNumber = 1;

        foreach (var imageUrl in imageUrls)
        {
            pages.Add(new NekoSharp.Core.Models.Page
            {
                Number = pageNumber++,
                ImageUrl = imageUrl,
                RefererUrl = chapter.Url
            });
        }

        if (pages.Count == 0)
            throw new InvalidOperationException($"Capítulo {parsed.ChapterId} não possui imagens válidas.");

        return pages;
    }

    private async Task<JsonElement> GetResultAsync(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var response = await _http.SendAsync(request, ct);

        var actualUrl = request.RequestUri?.ToString() ?? relativeUrl;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log?.Error($"[Comix] HTTP {(int)response.StatusCode} para '{actualUrl}'. Body: {Truncate(body, 500)}");
            throw new HttpRequestException(
                $"Comix API retornou {(int)response.StatusCode} ({response.ReasonPhrase}) para '{relativeUrl}'.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var apiStatus = GetInt(root, "status");
            var apiMsg = ExtractApiMessage(root);
            _log?.Error($"[Comix] API rejeitou request (status={apiStatus}): '{actualUrl}'. Msg: {apiMsg}");
            throw new InvalidOperationException(
                $"Resposta inválida da API do Comix (status={apiStatus}). {apiMsg}");
        }

        return result.Clone();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    internal static ComixUrlRef ParseSupportedUrl(string url)
    {
        if (!ComixUrlParser.TryParse(url, out var parsed))
            throw new ArgumentException("URL do Comix inválida. Use /title/<hash>-slug ou /title/<hash>-slug/<chapterId>-slug.", nameof(url));

        return parsed;
    }

    internal static string BuildChapterListRelativeUrl(string hashId, int page, int limit, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var trimmedHashId = hashId.Trim();
        return $"manga/{Uri.EscapeDataString(trimmedHashId)}/chapters" +
               $"?order%5Bnumber%5D=desc&limit={limit}&page={page}&_={Uri.EscapeDataString(token)}";
    }

    internal static string BuildPageListRelativeUrl(int chapterId, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chapterId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return $"chapters/{chapterId}?_={Uri.EscapeDataString(token)}";
    }

    private async Task<string> CaptureTokenAsync(string pageUrl, string pathSuffix, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathSuffix);

        _log?.Debug($"[Comix] Capturing API token from {pageUrl}");

        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();

        var creds = _cfStore is null ? null : await _cfStore.TryGetAsync("comix.to");
        var userAgent = creds?.UserAgent;
        if (string.IsNullOrWhiteSpace(userAgent))
            userAgent = UserAgentProvider.Default;

        await page.SetUserAgentAsync(userAgent);
        await ApplyStoredCookiesAsync(page, pageUrl, creds);

        var tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRequest(object? _, RequestEventArgs e)
        {
            if (!Uri.TryCreate(e.Request.Url, UriKind.Absolute, out var requestUri))
                return;

            if (!requestUri.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal))
                return;

            var token = ExtractTokenFromUrl(requestUri);
            if (!string.IsNullOrWhiteSpace(token))
                tokenTcs.TrySetResult(token);
        }

        page.Request += OnRequest;

        try
        {
            await page.GoToAsync(pageUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)TokenCaptureTimeout.TotalMilliseconds
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TokenCaptureTimeout, timeoutCts.Token);
            var completed = await Task.WhenAny(tokenTcs.Task, delayTask);
            if (completed != tokenTcs.Task)
                throw new TimeoutException($"Timed out waiting for Comix token for '{pathSuffix}'.");

            timeoutCts.Cancel();
            return await tokenTcs.Task;
        }
        finally
        {
            page.Request -= OnRequest;
        }
    }

    private async Task PrepareBrowserPageAsync(IPage page, string pageUrl)
    {
        var creds = _cfStore is null ? null : await _cfStore.TryGetAsync("comix.to");
        var userAgent = creds?.UserAgent;
        if (string.IsNullOrWhiteSpace(userAgent))
            userAgent = UserAgentProvider.Default;

        await page.SetUserAgentAsync(userAgent);
        await ApplyStoredCookiesAsync(page, pageUrl, creds);
    }

    private static async Task<ComixChapterDomSnapshot> CaptureChapterDomSnapshotAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var json = await page.EvaluateFunctionAsync<string>(
            """
            () => {
                const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
                const items = Array.from(document.querySelectorAll('.mchap-list .mchap-item')).map((li) => {
                    const link = li.querySelector('a.mchap-row__primary[href]');
                    const likesText = clean(li.querySelector('.mchap-row__likes')?.textContent);
                    const likesMatch = likesText.match(/\d+/);
                    return {
                        Href: link ? link.getAttribute('href') || '' : '',
                        ChapterLabel: clean(li.querySelector('.mchap-row__ch')?.textContent),
                        Title: clean(li.querySelector('.mchap-row__title')?.textContent),
                        Likes: likesMatch ? Number(likesMatch[0]) : 0,
                        Time: clean(li.querySelector('.mchap-row__time')?.textContent)
                    };
                }).filter((item) => item.Href);

                const nextButton = Array.from(document.querySelectorAll('button[aria-label="Next page"]'))
                    .find((button) => !button.disabled && button.getAttribute('aria-disabled') !== 'true');

                return JSON.stringify({
                    Items: items,
                    HasNextPage: Boolean(nextButton)
                });
            }
            """);

        return JsonSerializer.Deserialize<ComixChapterDomSnapshot>(json) ?? new ComixChapterDomSnapshot();
    }

    private static ComixChapterCandidate ParseChapterDomItem(ComixChapterDomItem item, string pageUrl)
    {
        var sourceUrl = ToComixAbsoluteUrl(item.Href, pageUrl);
        var chapterId = 0;
        if (ComixUrlParser.TryParse(sourceUrl, out var parsed))
            chapterId = parsed.ChapterId;

        return new ComixChapterCandidate(
            ChapterId: chapterId,
            Number: ParseChapterNumber(item.ChapterLabel, sourceUrl),
            SourceUrl: sourceUrl,
            Name: NormalizeText(item.Title),
            Votes: item.Likes,
            UpdatedAt: ParseRelativeTimestamp(item.Time),
            ScanlationGroupId: 0,
            ScanlationGroupName: string.Empty,
            IsOfficial: 0);
    }

    private static double ParseChapterNumber(string? label, string? sourceUrl)
    {
        var text = $"{label} {sourceUrl}";
        var match = ChapterNumberRegex.Match(text);
        if (!match.Success)
            return 0;

        return double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    private static async Task<bool> ClickNextChapterPageAsync(IPage page)
    {
        return await page.EvaluateFunctionAsync<bool>(
            """
            () => {
                const button = Array.from(document.querySelectorAll('button[aria-label="Next page"]'))
                    .find((candidate) => !candidate.disabled && candidate.getAttribute('aria-disabled') !== 'true');
                if (!button) return false;
                button.click();
                return true;
            }
            """);
    }

    private static async Task WaitForChapterPageChangeAsync(IPage page, string firstHref, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var changed = await page.EvaluateFunctionAsync<bool>(
                """
                (previousHref) => {
                    const currentHref = document.querySelector('.mchap-list .mchap-item a.mchap-row__primary[href]')
                        ?.getAttribute('href') || '';
                    return currentHref.length > 0 && currentHref !== previousHref;
                }
                """,
                firstHref);

            if (changed)
                return;

            await Task.Delay(250, ct);
        }
    }

    private static string ToComixAbsoluteUrl(string href, string pageUrl)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var baseUri = Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsedBase)
            ? parsedBase
            : new Uri(BaseUrlStatic);

        return new Uri(baseUri, href).ToString();
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex.Replace(value.Trim(), " ");

    private static string? ExtractTokenFromUrl(Uri requestUri)
    {
        var query = requestUri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var name = idx >= 0 ? pair[..idx] : pair;
            if (!name.Equals("_", StringComparison.Ordinal))
                continue;

            var rawValue = idx >= 0 ? pair[(idx + 1)..] : string.Empty;
            return Uri.UnescapeDataString(rawValue);
        }

        return null;
    }

    private static async Task ApplyStoredCookiesAsync(IPage page, string pageUrl, CloudflareCredentials? creds)
    {
        if (creds is null || creds.AllCookies.Count == 0)
            return;

        var pageUri = new Uri(pageUrl);
        var cookieOrigin = pageUri.GetLeftPart(UriPartial.Authority);
        var cookies = creds.AllCookies
            .Where(static kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => new CookieParam
            {
                Name = kv.Key,
                Value = kv.Value,
                Url = cookieOrigin,
                Path = "/",
                Secure = pageUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
            })
            .ToArray();

        if (cookies.Length > 0)
            await page.SetCookieAsync(cookies);
    }

    private async Task<IBrowser> LaunchBrowserAsync(CancellationToken ct)
    {
        var options = new LaunchOptions
        {
            Headless = true,
            DefaultViewport = null,
            Args = ["--no-sandbox", "--disable-setuid-sandbox"],
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
        catch
        {
            var fetcher = new BrowserFetcher();
            var installed = await fetcher.DownloadAsync();
            options.ExecutablePath = fetcher.GetExecutablePath(installed.BuildId);

            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
    }

    private static string BuildMangaUrl(string hashId, string? slug, string? canonicalUrl)
    {
        if (!string.IsNullOrWhiteSpace(canonicalUrl))
        {
            var trimmedCanonicalUrl = canonicalUrl.Trim();
            if (trimmedCanonicalUrl.StartsWith("/title/", StringComparison.OrdinalIgnoreCase))
                return $"{BaseUrlStatic}{trimmedCanonicalUrl}";
        }

        var slugPart = string.IsNullOrWhiteSpace(slug)
            ? hashId
            : $"{hashId}-{slug.Trim().Trim('/')}";

        return $"{BaseUrlStatic}/title/{slugPart}";
    }

    private static string BuildChapterUrl(string mangaSegment, int chapterId, string? sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var trimmedSourceUrl = sourceUrl.Trim();
            if (trimmedSourceUrl.StartsWith("/title/", StringComparison.OrdinalIgnoreCase))
                return $"{BaseUrlStatic}{trimmedSourceUrl}";

            if (trimmedSourceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmedSourceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return trimmedSourceUrl;
        }

        return $"{BaseUrlStatic}/title/{mangaSegment.Trim('/')}/{chapterId}";
    }

    private const string BaseUrlStatic = "https://comix.to";

    private static string BuildDescription(JsonElement manga)
    {
        var sections = new List<string>();

        var synopsis = GetString(manga, "synopsis");
        if (!string.IsNullOrWhiteSpace(synopsis))
            sections.Add(synopsis);

        var altTitles = GetStringArray(manga, "alt_titles");
        if (altTitles.Count == 0)
            altTitles = GetStringArray(manga, "altTitles");
        if (altTitles.Count > 0)
            sections.Add("Alternative Names:\n" + string.Join('\n', altTitles));

        var metadata = new List<string>();

        var year = GetInt(manga, "year") ?? GetInt(manga, "start_date");
        if (year.HasValue && year.Value > 0)
            metadata.Add($"Year: {year.Value}");

        var type = FormatType(GetString(manga, "type"));
        if (!string.IsNullOrWhiteSpace(type))
            metadata.Add($"Type: {type}");

        var status = FormatStatus(GetString(manga, "status"));
        if (!string.IsNullOrWhiteSpace(status))
            metadata.Add($"Status: {status}");

        var authors = GetTermTitles(manga, "authors");
        if (authors.Count == 0)
            authors = GetTermTitles(manga, "author");
        if (authors.Count > 0)
            metadata.Add($"Author: {string.Join(", ", authors)}");

        var artists = GetTermTitles(manga, "artists");
        if (artists.Count == 0)
            artists = GetTermTitles(manga, "artist");
        if (artists.Count > 0)
            metadata.Add($"Artist: {string.Join(", ", artists)}");

        var demographics = GetTermTitles(manga, "demographics");
        if (demographics.Count == 0)
            demographics = GetTermTitles(manga, "demographic");
        if (demographics.Count > 0)
            metadata.Add($"Demographics: {string.Join(", ", demographics)}");

        var genres = GetTermTitles(manga, "genres");
        if (genres.Count == 0)
            genres = GetTermTitles(manga, "genre");
        if (genres.Count > 0)
            metadata.Add($"Genres: {string.Join(", ", genres)}");

        var tags = GetTermTitles(manga, "tags");
        if (tags.Count == 0)
            tags = GetTermTitles(manga, "theme");
        if (tags.Count > 0)
            metadata.Add($"Tags: {string.Join(", ", tags)}");

        var publishers = GetTermTitles(manga, "publisher");
        if (publishers.Count > 0)
            metadata.Add($"Publisher: {string.Join(", ", publishers)}");

        var contentRating = GetString(manga, "contentRating") ?? GetString(manga, "content_rating");
        if (!string.IsNullOrWhiteSpace(contentRating))
            metadata.Add($"Content rating: {FormatLabelValue(contentRating)}");

        var rank = GetInt(manga, "rank");
        if (rank.HasValue && rank.Value > 0)
            metadata.Add($"Rank: #{rank.Value}");

        var ratedCount = GetLong(manga, "ratedCount") ?? GetLong(manga, "rated_count");
        if (ratedCount.HasValue && ratedCount.Value > 0)
            metadata.Add($"Rated by: {ratedCount.Value}");

        var followsTotal = GetLong(manga, "followsTotal") ?? GetLong(manga, "follows_total");
        if (followsTotal.HasValue && followsTotal.Value > 0)
            metadata.Add($"Followed by: {followsTotal.Value}");

        var originalLanguage = GetString(manga, "originalLanguage") ?? GetString(manga, "original_language");
        if (!string.IsNullOrWhiteSpace(originalLanguage))
            metadata.Add($"Language: {originalLanguage.Trim().ToUpperInvariant()}");

        var score = GetDouble(manga, "ratedAvg") ?? GetDouble(manga, "rated_avg");
        if (score.HasValue && score.Value > 0)
            metadata.Add($"Score: {score.Value.ToString("0.##", CultureInfo.InvariantCulture)}/10");

        if (GetBooleanishInt(manga, "is_nsfw") == 1)
            metadata.Add("NSFW: Yes");

        if (metadata.Count > 0)
            sections.Add(string.Join(Environment.NewLine, metadata));

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static List<string> ParsePageImageUrls(JsonElement result)
    {
        var pageUrls = new List<string>();

        if (result.TryGetProperty("images", out var legacyImages) && legacyImages.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in legacyImages.EnumerateArray())
            {
                var imageUrl = GetString(image, "url");
                if (!string.IsNullOrWhiteSpace(imageUrl))
                    pageUrls.Add(imageUrl);
            }

            if (pageUrls.Count > 0)
                return pageUrls;
        }

        if (!result.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
            return pageUrls;

        var baseUrl = (GetString(pages, "baseUrl") ?? string.Empty).TrimEnd('/');
        if (!pages.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return pageUrls;

        foreach (var item in items.EnumerateArray())
        {
            var imagePath = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(imagePath))
                continue;

            pageUrls.Add(imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? imagePath
                : $"{baseUrl}/{imagePath.TrimStart('/')}");
        }

        return pageUrls;
    }

    internal static List<Chapter> BuildChapterList(string mangaSegment, IEnumerable<ComixChapterCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var duplicatedVariants = materialized
            .GroupBy(BuildChapterVariantKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return materialized
            .OrderByDescending(static chapter => chapter.Number)
            .ThenByDescending(static chapter => IsOfficialRelease(chapter))
            .ThenByDescending(static chapter => chapter.UpdatedAt)
            .ThenByDescending(static chapter => chapter.ChapterId)
            .Select(chapter => new Chapter
            {
                Number = chapter.Number,
                Title = BuildChapterTitle(chapter, duplicatedVariants.Contains(BuildChapterVariantKey(chapter))),
                Url = BuildChapterUrl(mangaSegment, chapter.ChapterId, chapter.SourceUrl)
            })
            .ToList();
    }

    private static string BuildChapterTitle(ComixChapterCandidate chapter, bool includeVariantLabel)
    {
        var baseTitle = chapter.Name.Trim();
        if (!includeVariantLabel)
            return baseTitle;

        var variantLabel = BuildChapterVariantLabel(chapter);
        if (string.IsNullOrWhiteSpace(baseTitle))
            return variantLabel;

        return string.IsNullOrWhiteSpace(variantLabel)
            ? baseTitle
            : $"{baseTitle} [{variantLabel}]";
    }

    private static string FormatChapterNumber(double number)
        => number.ToString("0.####################", CultureInfo.InvariantCulture);

    private static string BuildChapterVariantKey(ComixChapterCandidate chapter)
    {
        var nameKey = NormalizeDedupName(chapter.Name);
        return $"{chapter.Number.ToString("0.####################", CultureInfo.InvariantCulture)}|{nameKey}";
    }

    private static string BuildChapterVariantLabel(ComixChapterCandidate chapter)
    {
        if (IsOfficialRelease(chapter))
            return "Oficial";

        return string.IsNullOrWhiteSpace(chapter.ScanlationGroupName)
            ? "Não oficial"
            : chapter.ScanlationGroupName.Trim();
    }

    private static string NormalizeDedupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return string.Join(
            ' ',
            name.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsBetterChapter(ComixChapterCandidate candidate, ComixChapterCandidate current)
    {
        var officialCandidate = IsOfficialRelease(candidate);
        var officialCurrent = IsOfficialRelease(current);

        if (officialCandidate != officialCurrent)
            return officialCandidate;

        if (candidate.Votes != current.Votes)
            return candidate.Votes > current.Votes;

        return candidate.UpdatedAt >= current.UpdatedAt;
    }

    private static bool IsOfficialRelease(ComixChapterCandidate chapter)
        => OfficialScanlationGroupIds.Contains(chapter.ScanlationGroupId) || chapter.IsOfficial == 1;

    private static bool HasNextPage(JsonElement result)
    {
        JsonElement pagination;
        if (result.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            pagination = meta;
        }
        else if (result.TryGetProperty("pagination", out var legacyPagination) && legacyPagination.ValueKind == JsonValueKind.Object)
        {
            pagination = legacyPagination;
        }
        else
        {
            return false;
        }

        var page = GetInt(pagination, "current_page") ?? GetInt(pagination, "page") ?? 1;
        var lastPage = GetInt(pagination, "last_page") ?? GetInt(pagination, "lastPage") ?? page;
        return page < lastPage;
    }

    private static ComixChapterCandidate ParseChapterCandidate(JsonElement item)
    {
        return new ComixChapterCandidate(
            ChapterId: GetInt(item, "id") ?? GetInt(item, "chapter_id") ?? 0,
            Number: GetDouble(item, "number") ?? 0,
            SourceUrl: GetString(item, "url") ?? string.Empty,
            Name: GetString(item, "name") ?? string.Empty,
            Votes: GetInt(item, "votes") ?? 0,
            UpdatedAt: GetLong(item, "updated_at") ??
                GetLong(item, "created_at") ??
                ParseRelativeTimestamp(GetString(item, "createdAtFormatted")),
            ScanlationGroupId: GetInt(item, "groupId") ?? GetInt(item, "scanlation_group_id") ?? 0,
            ScanlationGroupName: GetNestedString(item, "group", "name") ??
                GetNestedString(item, "scanlation_group", "name") ??
                string.Empty,
            IsOfficial: GetBooleanishInt(item, "isOfficial") != 0
                ? 1
                : GetBooleanishInt(item, "is_official"));
    }

    private static long ParseRelativeTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0L;

        var match = RelativeDateRegex.Match(value.Trim().ToLowerInvariant().Replace(" ago", string.Empty));
        if (!match.Success)
            return 0L;

        var amount = int.TryParse(match.Groups["amount"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAmount)
            ? parsedAmount
            : 0;
        if (amount <= 0)
            return 0L;

        var utcNow = DateTimeOffset.UtcNow;
        var adjusted = match.Groups["unit"].Value switch
        {
            "s" or "sec" or "secs" => utcNow.AddSeconds(-amount),
            "m" or "min" or "mins" => utcNow.AddMinutes(-amount),
            "h" or "hr" or "hrs" => utcNow.AddHours(-amount),
            "d" or "day" or "days" => utcNow.AddDays(-amount),
            "w" or "week" or "weeks" => utcNow.AddDays(-7 * amount),
            "mo" or "mos" or "month" or "months" => utcNow.AddMonths(-amount),
            "y" or "yr" or "yrs" or "year" or "years" => utcNow.AddYears(-amount),
            _ => utcNow
        };

        return adjusted.ToUnixTimeSeconds();
    }
    private static string ExtractApiMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? string.Empty;

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in messages.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                    return entry.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in property.EnumerateArray())
        {
            var value = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.ToString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim());
        }

        return values;
    }

    private static List<string> GetTermTitles(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in property.EnumerateArray())
        {
            var title = GetString(item, "title");
            if (!string.IsNullOrWhiteSpace(title))
                values.Add(title);
        }

        return values;
    }

    private static string? GetNestedString(JsonElement element, string objectProperty, string nestedProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(child, nestedProperty);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static int GetBooleanishInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number when property.TryGetInt32(out var value) => value != 0 ? 1 : 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var flag) => flag ? 1 : 0,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value != 0 ? 1 : 0,
            _ => 0
        };
    }

    private static string? FormatType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "manga" => "Manga",
            "manhwa" => "Manhwa",
            "manhua" => "Manhua",
            "other" => "Other",
            null or "" => null,
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
        };
    }

    private static string? FormatStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "releasing" => "Releasing",
            "on_hiatus" => "On Hiatus",
            "finished" => "Finished",
            "discontinued" => "Discontinued",
            "not_yet_released" => "Not Yet Released",
            null or "" => null,
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
        };
    }

    private static string FormatLabelValue(string value)
    {
        var normalized = value.Trim().Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private sealed class ComixChapterDomSnapshot
    {
        public List<ComixChapterDomItem> Items { get; set; } = [];
        public bool HasNextPage { get; set; }
    }

    private sealed class ComixChapterDomItem
    {
        public string Href { get; set; } = string.Empty;
        public string ChapterLabel { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Likes { get; set; }
        public string Time { get; set; } = string.Empty;
    }

    internal readonly record struct ComixChapterCandidate(
        int ChapterId,
        double Number,
        string SourceUrl,
        string Name,
        int Votes,
        long UpdatedAt,
        int ScanlationGroupId,
        string ScanlationGroupName,
        int IsOfficial);
}
