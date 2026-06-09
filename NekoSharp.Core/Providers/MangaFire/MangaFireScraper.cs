using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Templates;
using NekoSharp.Core.Services;
using PuppeteerSharp;

namespace NekoSharp.Core.Providers.MangaFire;

public sealed partial class MangaFireScraper : HtmlScraperBase
{
    private readonly CloudflareCredentialStore? _cfStore;

    public override string Name => "MangaFire";

    protected override IReadOnlyCollection<string> SupportedHosts => ["mangafire.to", "www.mangafire.to"];

    public MangaFireScraper() : this(null, null) { }

    public MangaFireScraper(LogService? logService) : this(logService, null) { }

    public MangaFireScraper(LogService? logService, CloudflareCredentialStore? cfStore)
        : base("https://mangafire.to", logService, cfStore)
    {
        _cfStore = cfStore;
    }

    public override async Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
    {
        var mangaUrl = await NormalizeMangaUrlAsync(url, ct);
        var document = await LoadDocumentAsync(mangaUrl, ct);

        var title = document.QuerySelector(".manga-detail .info h1")?.TextContent?.Trim() ?? string.Empty;
        var altTitle = document.QuerySelector(".manga-detail .info h6")?.TextContent?.Trim();
        var synopsis = document.QuerySelector("#synopsis .modal-content")?.TextContent?.Trim();
        var shortDescription = document.QuerySelector(".manga-detail .info .description")?.TextContent?.Trim();
        var status = document.QuerySelector(".manga-detail .info > p")?.TextContent?.Trim();
        var type = document.QuerySelector(".manga-detail .min-info a[href*='/type/']")?.TextContent?.Trim();
        var published = document.QuerySelector(".meta span:contains('Published:') + span")?.TextContent?.Trim();
        var author = document.QuerySelector(".meta span:contains('Author:') + span")?.TextContent?.Trim();
        var genres = document.QuerySelector(".meta span:contains('Genres:') + span")?
            .QuerySelectorAll("a")
            .Select(static a => a.TextContent.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray() ?? [];

        var descriptionSections = new List<string>();
        if (!string.IsNullOrWhiteSpace(synopsis))
            descriptionSections.Add(synopsis);
        else if (!string.IsNullOrWhiteSpace(shortDescription))
            descriptionSections.Add(shortDescription);

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(altTitle) &&
            !altTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            metadata.Add($"Alternative title: {altTitle}");
        if (!string.IsNullOrWhiteSpace(author))
            metadata.Add($"Author: {author}");
        if (!string.IsNullOrWhiteSpace(type))
            metadata.Add($"Type: {type}");
        if (!string.IsNullOrWhiteSpace(status))
            metadata.Add($"Status: {status}");
        if (!string.IsNullOrWhiteSpace(published))
            metadata.Add($"Published: {published}");
        if (genres.Length > 0)
            metadata.Add($"Genres: {string.Join(", ", genres)}");

        if (metadata.Count > 0)
            descriptionSections.Add(string.Join(Environment.NewLine, metadata));

        return new Manga
        {
            Name = title,
            CoverUrl = document.QuerySelector(".manga-detail .poster img")?.GetAttribute("src") ?? string.Empty,
            Description = string.Join(Environment.NewLine + Environment.NewLine, descriptionSections),
            Url = mangaUrl,
            SiteName = Name
        };
    }

    public override async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var mangaUrl = await NormalizeMangaUrlAsync(url, ct);
        var document = await LoadDocumentAsync(mangaUrl, ct);
        var languageCode = GetPreferredLanguageCode(document);

        var chapters = document.QuerySelectorAll(".tab-content[data-name='chapter'] .list-body li.item")
            .Select(item => CreateChapter(item, languageCode))
            .Where(static chapter => chapter is not null)
            .Cast<Chapter>()
            .ToList();

        if (chapters.Count == 0)
            throw new InvalidOperationException("Nao foi possivel localizar os capitulos do MangaFire.");

        return chapters;
    }

    public override async Task<List<NekoSharp.Core.Models.Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var chapterUrl = NormalizeChapterUrl(chapter.Url);

        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();

        var creds = _cfStore is null ? null : await _cfStore.TryGetAsync("mangafire.to");
        var userAgent = string.IsNullOrWhiteSpace(creds?.UserAgent)
            ? UserAgentProvider.Default
            : creds!.UserAgent;

        await page.SetUserAgentAsync(userAgent);
        await page.EvaluateExpressionOnNewDocumentAsync(
            """
            (() => {
              const noop = () => {};
              const names = ['log', 'debug', 'info', 'warn', 'error', 'dir', 'dirxml', 'trace'];
              for (const name of names) {
                try { console[name] = noop; } catch {}
              }
            })();
            """);
        await ApplyStoredCookiesAsync(page, chapterUrl, creds);

        try
        {
            var domPages = await TryExtractPagesFromDomAsync(page, chapterUrl, ct);
            if (domPages.Count > 0)
                return domPages;

            throw new InvalidOperationException(
                "Nao foi possivel localizar as imagens do capitulo no DOM do leitor do MangaFire.");
        }
        finally
        {
        }
    }

    private async Task<List<NekoSharp.Core.Models.Page>> TryExtractPagesFromDomAsync(IPage page, string chapterUrl, CancellationToken ct)
    {
        await page.GoToAsync(chapterUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 60000
        });

        try
        {
            await page.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
            {
                IdleTime = 1_000,
                Concurrency = 2,
                Timeout = 10_000
            });
        }
        catch (Exception ex) when (ex is WaitTaskTimeoutException or TimeoutException)
        {
            Log?.Debug("[MangaFire] Network idle wait timed out after chapter load. Continuing with DOM polling.");
        }

        var bestUrls = Array.Empty<string>();
        var stableRounds = 0;
        var bottomRounds = 0;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            ReaderDomSnapshot snapshot;
            try
            {
                snapshot = await CaptureReaderDomSnapshotAsync(page);
            }
            catch (Exception ex) when (IsTransientReaderExecutionError(ex))
            {
                await Task.Delay(500, ct);
                continue;
            }

            if (snapshot.ImageUrls.Length > bestUrls.Length)
            {
                bestUrls = snapshot.ImageUrls;
                stableRounds = 0;
            }
            else if (snapshot.ImageUrls.Length == bestUrls.Length && bestUrls.Length > 0)
            {
                stableRounds++;
            }

            bottomRounds = snapshot.AtBottom ? bottomRounds + 1 : 0;

            if (bestUrls.Length > 0 && snapshot.ExpectedCount > 0 && bestUrls.Length >= snapshot.ExpectedCount)
            {
                Log?.Debug($"[MangaFire] Extracted {bestUrls.Length} page(s) directly from reader DOM (expected {snapshot.ExpectedCount}).");
                return CreatePagesFromImageUrls(bestUrls, chapterUrl);
            }

            if (bestUrls.Length > 0 && stableRounds >= 3 && bottomRounds >= 2)
            {
                Log?.Debug($"[MangaFire] Extracted {bestUrls.Length} page(s) directly from reader DOM after stabilization.");
                return CreatePagesFromImageUrls(bestUrls, chapterUrl);
            }

            await ScrollReaderAsync(page, ct);

            await Task.Delay(750, ct);
        }

        Log?.Debug("[MangaFire] Reader DOM did not expose page images in time.");
        return bestUrls.Length > 0
            ? CreatePagesFromImageUrls(bestUrls, chapterUrl)
            : [];
    }

    private async Task<string> CapturePagesApiUrlAsync(IPage page, Chapter chapter, string chapterUrl, CancellationToken ct)
    {
        var directApiUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerApiUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void CaptureApiRequest(object? _, RequestEventArgs args)
        {
            if (!Uri.TryCreate(args.Request.Url, UriKind.Absolute, out var uri))
                return;

            if (!uri.Host.Contains("mangafire.to", StringComparison.OrdinalIgnoreCase))
                return;

            if (!HasVrfQuery(uri))
                return;

            if (DirectPagesApiPathRegex().IsMatch(uri.AbsolutePath))
            {
                directApiUrlTcs.TrySetResult(uri.ToString());
                return;
            }

            if (ReaderPagesApiPathRegex().IsMatch(uri.AbsolutePath))
                readerApiUrlTcs.TrySetResult(uri.ToString());
        }

        page.Request += CaptureApiRequest;

        try
        {
            await page.GoToAsync(chapterUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = 60000
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

            var completed = await Task.WhenAny(directApiUrlTcs.Task, readerApiUrlTcs.Task, delayTask);
            if (completed == directApiUrlTcs.Task)
            {
                timeoutCts.Cancel();
                return await directApiUrlTcs.Task;
            }

            if (completed == delayTask)
            {
                throw new InvalidOperationException(
                    "Nao foi possivel capturar a request de paginas do MangaFire. " +
                    "Se o site exigir Cloudflare clearance, atualize os cookies e tente novamente.");
            }

            timeoutCts.Cancel();
            var capturedApiUrl = await readerApiUrlTcs.Task;
            return await ResolvePagesApiUrlAsync(capturedApiUrl, chapter, chapterUrl, ct);
        }
        finally
        {
            page.Request -= CaptureApiRequest;
        }
    }

    private async Task<string> ResolvePagesApiUrlAsync(string capturedApiUrl, Chapter chapter, string chapterUrl, CancellationToken ct)
    {
        if (Uri.TryCreate(capturedApiUrl, UriKind.Absolute, out var capturedUri) &&
            capturedUri.AbsolutePath.StartsWith("/ajax/read/chapter/", StringComparison.OrdinalIgnoreCase))
        {
            return capturedApiUrl;
        }

        if (!Uri.TryCreate(capturedApiUrl, UriKind.Absolute, out var readerUri))
            throw new InvalidOperationException("URL capturada do MangaFire invalida.");

        var vrf = GetQueryValue(readerUri, "vrf");
        if (string.IsNullOrWhiteSpace(vrf))
            throw new InvalidOperationException("A URL capturada do MangaFire nao contem o parametro 'vrf'.");

        using var request = new HttpRequestMessage(HttpMethod.Get, readerUri.ToString());
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(chapterUrl);

        var json = await SendForStringAsync(request, ct);
        using var responseDocument = JsonDocument.Parse(json);
        var root = responseDocument.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("html", out var htmlNode))
        {
            throw new InvalidOperationException("Nao foi possivel ler a lista AJAX de capitulos do MangaFire.");
        }

        var html = htmlNode.GetString();
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("A lista AJAX de capitulos do MangaFire veio vazia.");

        var fragment = await OpenDocumentAsync($"<body>{html}</body>", chapterUrl, ct);
        var chapterId = ResolveChapterIdFromAjax(fragment, chapter);
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            throw new InvalidOperationException(
                $"Nao foi possivel localizar o data-id do capitulo '{chapter.Title}' no payload AJAX do MangaFire.");
        }

        return $"{BaseUrl}/ajax/read/chapter/{chapterId}?vrf={Uri.EscapeDataString(vrf)}";
    }

    private async Task<List<NekoSharp.Core.Models.Page>> FetchPagesFromApiAsync(IPage page, string apiUrl, string chapterUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(chapterUrl);
        await ApplyPageCookiesToRequestAsync(page, request, chapterUrl);

        var json = await SendForStringAsync(request, ct);
        Log?.Debug($"[MangaFire] Pages API payload snippet: {BuildPayloadSnippet(json)}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Resposta de paginas do MangaFire nao contem o array 'result.images'. Payload: {BuildPayloadSnippet(json)}");
        }

        var pages = new List<NekoSharp.Core.Models.Page>();
        var index = 1;

        foreach (var imageEntry in images.EnumerateArray())
        {
            if (imageEntry.ValueKind != JsonValueKind.Array || imageEntry.GetArrayLength() == 0)
                continue;

            var imageUrl = TryGetImageUrl(imageEntry);
            if (!TryNormalizeImageUrl(imageUrl, out var normalizedImageUrl))
                continue;

            var offset = TryGetScrambleOffset(imageEntry);
            if (offset > 0)
                normalizedImageUrl = $"{normalizedImageUrl}#scrambled_{offset}";

            pages.Add(new NekoSharp.Core.Models.Page
            {
                Number = index++,
                ImageUrl = normalizedImageUrl,
                RefererUrl = chapterUrl
            });
        }

        if (pages.Count == 0)
            throw new InvalidOperationException("Nenhuma imagem valida foi encontrada na resposta do MangaFire.");

        return pages;
    }

    private static async Task<ReaderDomSnapshot> CaptureReaderDomSnapshotAsync(IPage page)
    {
        var snapshotJson = await page.EvaluateFunctionAsync<string>(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('.pages .page img, .pages .img img, .page img, img[data-number]'));
              const entries = [];
              for (const node of nodes) {
                const dataNumber = parseInt(
                  node.getAttribute('data-number') ||
                  node.closest('[data-number]')?.getAttribute('data-number') ||
                  '',
                  10
                );
                const url =
                  node.currentSrc ||
                  node.getAttribute('src') ||
                  node.getAttribute('data-src') ||
                  node.getAttribute('data-lazy-src') ||
                  node.getAttribute('data-src-url');
                if (!url) continue;
                if (!/^https?:\/\//i.test(url)) continue;
                entries.push({
                  url: url.trim(),
                  number: Number.isFinite(dataNumber) ? dataNumber : 0,
                  index: entries.length,
                });
              }

              entries.sort((a, b) => {
                if (a.number > 0 && b.number > 0 && a.number !== b.number) return a.number - b.number;
                if (a.number > 0 && b.number <= 0) return -1;
                if (a.number <= 0 && b.number > 0) return 1;
                return a.index - b.index;
              });

              const seen = new Set();
              const imageUrls = [];
              let expectedCount = 0;
              for (const entry of entries) {
                if (entry.number > expectedCount) expectedCount = entry.number;
                if (seen.has(entry.url)) continue;
                seen.add(entry.url);
                imageUrls.push(entry.url);
              }

              const pageNodesCount = document.querySelectorAll('.pages .page, .page').length;
              if (pageNodesCount > expectedCount) expectedCount = pageNodesCount;

              const scrollRoot = document.scrollingElement || document.documentElement || document.body;
              const scrollTop =
                window.scrollY ||
                scrollRoot?.scrollTop ||
                document.documentElement?.scrollTop ||
                document.body?.scrollTop ||
                0;
              const viewportHeight =
                window.innerHeight ||
                document.documentElement?.clientHeight ||
                document.body?.clientHeight ||
                0;
              const scrollHeight =
                scrollRoot?.scrollHeight ||
                document.documentElement?.scrollHeight ||
                document.body?.scrollHeight ||
                0;

              return JSON.stringify({
                imageUrls,
                expectedCount,
                scrollTop,
                scrollHeight,
                atBottom: scrollHeight <= 0 ? true : scrollTop + viewportHeight >= scrollHeight - 8,
              });
            }
            """);

        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;

        var imageUrls = root.TryGetProperty("imageUrls", out var imageUrlsNode) &&
                        imageUrlsNode.ValueKind == JsonValueKind.Array
            ? imageUrlsNode.EnumerateArray()
                .Select(static node => node.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        var expectedCount = root.TryGetProperty("expectedCount", out var expectedCountNode) &&
                            expectedCountNode.TryGetInt32(out var parsedExpectedCount)
            ? parsedExpectedCount
            : 0;

        var atBottom = root.TryGetProperty("atBottom", out var atBottomNode) &&
                       atBottomNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                       atBottomNode.GetBoolean();

        return new ReaderDomSnapshot(imageUrls, expectedCount, atBottom);
    }

    private static List<NekoSharp.Core.Models.Page> CreatePagesFromImageUrls(IEnumerable<string> imageUrls, string chapterUrl)
    {
        var pages = new List<NekoSharp.Core.Models.Page>();
        var index = 1;

        foreach (var imageUrl in imageUrls)
        {
            if (!TryNormalizeImageUrl(imageUrl, out var normalizedImageUrl))
                continue;

            pages.Add(new NekoSharp.Core.Models.Page
            {
                Number = index++,
                ImageUrl = normalizedImageUrl,
                RefererUrl = chapterUrl
            });
        }

        return pages;
    }

    private static async Task ScrollReaderAsync(IPage page, CancellationToken ct)
    {
        const string scrollScript =
            """
            (() => {
              const root = document.scrollingElement || document.documentElement || document.body;
              if (!root) return '0:0';

              const currentY =
                window.scrollY ||
                root.scrollTop ||
                document.documentElement?.scrollTop ||
                document.body?.scrollTop ||
                0;
              const viewportHeight =
                window.innerHeight ||
                document.documentElement?.clientHeight ||
                document.body?.clientHeight ||
                600;
              const maxY = Math.max(root.scrollHeight || 0, currentY, viewportHeight);
              const nextY = Math.min(currentY + Math.max(viewportHeight, 600), maxY);
              window.scrollTo(0, nextY);
              return `${nextY}:${maxY}`;
            })()
            """;

        var stableSteps = 0;
        var previousState = string.Empty;

        while (stableSteps < 2)
        {
            ct.ThrowIfCancellationRequested();

            string state;
            try
            {
                state = await page.EvaluateExpressionAsync<string>(scrollScript);
            }
            catch (Exception ex) when (IsTransientReaderExecutionError(ex))
            {
                await Task.Delay(400, ct);
                continue;
            }

            if (string.Equals(state, previousState, StringComparison.Ordinal))
                stableSteps++;
            else
                stableSteps = 0;

            previousState = state;
            await Task.Delay(400, ct);
        }
    }

    private static bool IsTransientReaderExecutionError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot read properties of null", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReaderDomSnapshot(string[] ImageUrls, int ExpectedCount, bool AtBottom);

    private static async Task ApplyPageCookiesToRequestAsync(IPage page, HttpRequestMessage request, string chapterUrl)
    {
        var pageCookies = await page.GetCookiesAsync();
        if (pageCookies.Length == 0)
            return;

        if (!Uri.TryCreate(chapterUrl, UriKind.Absolute, out var chapterUri))
            return;

        var cookiePairs = pageCookies
            .Where(cookie =>
                !string.IsNullOrWhiteSpace(cookie.Name) &&
                (string.IsNullOrWhiteSpace(cookie.Domain) ||
                 chapterUri.Host.Contains(cookie.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)))
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();

        if (cookiePairs.Length == 0)
            return;

        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookiePairs));
    }

    private static string? TryGetImageUrl(JsonElement imageEntry)
    {
        if (imageEntry.ValueKind != JsonValueKind.Array || imageEntry.GetArrayLength() == 0)
            return null;

        var node = imageEntry[0];
        return node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static int TryGetScrambleOffset(JsonElement imageEntry)
    {
        if (imageEntry.ValueKind != JsonValueKind.Array || imageEntry.GetArrayLength() < 3)
            return 0;

        var node = imageEntry[2];
        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetInt32(out var offset) => offset,
            JsonValueKind.String when int.TryParse(node.GetString(), out var offset) => offset,
            _ => 0
        };
    }

    private static string BuildPayloadSnippet(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "<empty>";

        var normalized = payload.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 280
            ? normalized
            : normalized[..280] + "...";
    }

    private static string? ResolveChapterIdFromAjax(IDocument document, Chapter chapter)
    {
        var targetUrl = NormalizePathForComparison(chapter.Url);
        foreach (var link in document.QuerySelectorAll("a[data-id]"))
        {
            var href = link.GetAttribute("href");
            var absoluteHref = ToAbsoluteUrl("https://mangafire.to", href);
            if (!string.IsNullOrWhiteSpace(absoluteHref) &&
                NormalizePathForComparison(absoluteHref).Equals(targetUrl, StringComparison.OrdinalIgnoreCase))
            {
                return link.GetAttribute("data-id");
            }
        }

        var fallbackByNumber = document.QuerySelector($"a[data-number='{chapter.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}']");
        return fallbackByNumber?.GetAttribute("data-id");
    }

    private async Task<string> NormalizeMangaUrlAsync(string url, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.StartsWith("/manga/", StringComparison.OrdinalIgnoreCase))
        {
            return $"{BaseUrl}{uri.AbsolutePath}".TrimEnd('/');
        }

        var document = await LoadDocumentAsync(url, ct);
        var syncData = document.QuerySelector("#syncData")?.TextContent;
        if (!string.IsNullOrWhiteSpace(syncData))
        {
            using var json = JsonDocument.Parse(syncData);
            if (json.RootElement.TryGetProperty("manga_url", out var mangaUrlElement))
            {
                var mangaUrl = mangaUrlElement.GetString();
                if (!string.IsNullOrWhiteSpace(mangaUrl))
                    return mangaUrl.Trim().TrimEnd('/');
            }
        }

        var mangaHref = document.QuerySelector("a[href^='/manga/']")?.GetAttribute("href");
        var absolute = ToAbsoluteUrl(BaseUrl, mangaHref);
        if (!string.IsNullOrWhiteSpace(absolute))
            return absolute.TrimEnd('/');

        throw new InvalidOperationException("Nao foi possivel normalizar a URL do manga no MangaFire.");
    }

    private Chapter? CreateChapter(IElement item, string languageCode)
    {
        var link = item.QuerySelector("a");
        var href = link?.GetAttribute("href");
        var absoluteUrl = ToAbsoluteUrl(BaseUrl, href);
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return null;

        var rawNumber = item.GetAttribute("data-number") ?? ExtractChapterNumberFromUrl(absoluteUrl);
        var number = ParseChapterNumber(rawNumber);
        var title = link?.QuerySelector("span:first-child")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(title))
            title = $"Chapter {rawNumber}";

        absoluteUrl = NormalizeChapterUrl(NormalizeChapterLanguage(absoluteUrl, languageCode));

        return new Chapter
        {
            Title = title,
            Number = number,
            Url = absoluteUrl
        };
    }

    private static string GetPreferredLanguageCode(IDocument document)
    {
        var active = document.QuerySelector(".tab-content[data-name='chapter'] .dropdown-menu .dropdown-item.active");
        var code = active?.GetAttribute("data-code");
        if (string.IsNullOrWhiteSpace(code))
            return "en";

        return code.Trim().ToLowerInvariant();
    }

    private static string NormalizeChapterLanguage(string chapterUrl, string languageCode)
    {
        if (!Uri.TryCreate(chapterUrl, UriKind.Absolute, out var uri))
            return chapterUrl;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count >= 3 &&
            segments[0].Equals("read", StringComparison.OrdinalIgnoreCase))
        {
            segments[2] = languageCode;
            return $"{uri.Scheme}://{uri.Authority}/{string.Join('/', segments)}";
        }

        return chapterUrl;
    }

    private string NormalizeChapterUrl(string? chapterUrl)
    {
        if (string.IsNullOrWhiteSpace(chapterUrl))
            throw new InvalidOperationException("URL de capitulo vazia no MangaFire.");

        var trimmed = chapterUrl.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return absoluteUri.ToString();

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return $"https:{trimmed}";

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
            return $"{BaseUrl}{trimmed}";

        return $"{BaseUrl}/{trimmed.TrimStart('/')}";
    }

    private static bool TryNormalizeImageUrl(string? rawUrl, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        imageUrl = uri.ToString();
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseChapterNumber(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return 0d;

        return double.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
    }

    private static string ExtractChapterNumberFromUrl(string url)
    {
        var match = ChapterNumberRegex().Match(url);
        return match.Success ? match.Groups["number"].Value : "0";
    }

    private static bool HasVrfQuery(Uri uri)
    {
        return !string.IsNullOrWhiteSpace(GetQueryValue(uri, "vrf"));
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query.AsSpan().TrimStart('?');
        foreach (var pair in query.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var currentKey = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value);
        }

        return null;
    }

    private static string NormalizePathForComparison(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim();

        return uri.AbsolutePath.TrimEnd('/');
    }

    [GeneratedRegex(@"^/ajax/read/chapter/\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DirectPagesApiPathRegex();

    [GeneratedRegex(@"^/ajax/read/[^/]+/(chapter|volume)/[^/]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ReaderPagesApiPathRegex();

    private static async Task ApplyStoredCookiesAsync(IPage page, string pageUrl, CloudflareCredentials? creds)
    {
        if (creds is null || creds.AllCookies.Count == 0)
            return;

        var pageUri = new Uri(pageUrl);
        var origin = pageUri.GetLeftPart(UriPartial.Authority);

        var cookies = creds.AllCookies
            .Where(static cookie => !string.IsNullOrWhiteSpace(cookie.Key))
            .Select(cookie => new CookieParam
            {
                Name = cookie.Key,
                Value = cookie.Value,
                Url = origin,
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
            Args = ["--no-sandbox", "--disable-setuid-sandbox"]
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

    [GeneratedRegex(@"chapter-(?<number>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterNumberRegex();
}
