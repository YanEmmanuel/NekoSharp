using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using PuppeteerSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using MangaPage = NekoSharp.Core.Models.Page;

namespace NekoSharp.Core.Providers.Comix;

public sealed class ComixScraper :
    IScraper,
    ICustomPageDownloadProvider,
    IRenderedChapterDownloadProvider,
    IRenderedPageFallbackProvider
{
    private const string ApiBaseUrl = "https://comix.to/api/v1/";
    private const string BaseUrlStatic = "https://comix.to";
    private const int EncMultiplier = 1_000_005;
    private const int EncIncrement = 1_234_567_891;
    private const int EncReadChunkSize = 8192;

    private static readonly Uri SiteRootUri = new($"{BaseUrlStatic}/");
    private static readonly TimeSpan BrowserPayloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BrowserRenderStepTimeout = TimeSpan.FromSeconds(12);
    private static readonly SemaphoreSlim BrowserRenderLock = new(1, 1);
    private static readonly Regex ScramblePathRegex = new(
        "/s?i+/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InitialDataScriptRegex = new(
        "<script[^>]*id=[\"']initial-data[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly string[] ScramblePathFallbacks = ["/si/", "/i/", "/sii/", "/ii/"];

    private const string ChapterCaptureScript =
        """
        (() => {
            const install = () => {
                if (JSON.parse.__nekosharpComixChapterCaptureInstalled) return;

                const state = window.__nekosharpComixChapterState = {
                    items: [],
                    pages: {},
                    nextClicks: {},
                    payload: ""
                };
                const rewriteUrl = (url) => {
                    if (typeof url === "string" && url.includes("/chapters") && /[?&]limit=\d+/.test(url)) {
                        return url.replace(/([?&]limit=)\d+/, "$1100");
                    }
                    return url;
                };
                const originalOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = function(method, url) {
                    arguments[1] = rewriteUrl(url);
                    return originalOpen.apply(this, arguments);
                };

                const originalParse = JSON.parse;
                const submit = () => {
                    if (!state.payload) state.payload = JSON.stringify(state.items);
                };
                const proxiedParse = new Proxy(originalParse, {
                    apply(target, thisArg, args) {
                        const parsed = Reflect.apply(target, thisArg, args);
                        try {
                            if (
                                !state.payload &&
                                parsed && parsed.result &&
                                Array.isArray(parsed.result.items) &&
                                parsed.result.items.length > 0 &&
                                parsed.result.items[0] &&
                                parsed.result.items[0].id !== undefined &&
                                parsed.result.items[0].mangaId !== undefined
                            ) {
                                const meta = parsed.result.meta || parsed.result.pagination || {};
                                const page = meta.page || 1;
                                if (!state.pages[page]) {
                                    state.pages[page] = true;
                                    state.items.push(...parsed.result.items);
                                    if (meta.hasNext && !state.nextClicks[page]) {
                                        state.nextClicks[page] = true;
                                        let tries = 0;
                                        const interval = setInterval(() => {
                                            const button = document.querySelector(".mchap-foot button[aria-label*=Next]");
                                            if (button && !button.disabled) {
                                                button.click();
                                                clearInterval(interval);
                                            } else if (++tries > 50) {
                                                clearInterval(interval);
                                                submit();
                                            }
                                        }, 100);
                                    } else {
                                        submit();
                                    }
                                }
                            }
                        } catch (_) {}
                        return parsed;
                    }
                });
                proxiedParse.__nekosharpComixChapterCaptureInstalled = true;
                JSON.parse = proxiedParse;
            };
            install();
        })();
        """;

    private const string PageCaptureScript =
        """
        (() => {
            if (JSON.parse.__nekosharpComixPageCaptureInstalled) return;
            window.__nekosharpComixPagePayload = "";
            const originalParse = JSON.parse;
            const proxiedParse = new Proxy(originalParse, {
                apply(target, thisArg, args) {
                    const parsed = Reflect.apply(target, thisArg, args);
                    try {
                        if (
                            !window.__nekosharpComixPagePayload &&
                            parsed && parsed.result && parsed.result.pages
                        ) {
                            window.__nekosharpComixPagePayload = args[0];
                        }
                    } catch (_) {}
                    return parsed;
                }
            });
            proxiedParse.__nekosharpComixPageCaptureInstalled = true;
            JSON.parse = proxiedParse;
        })();
        """;

    private const string BlobCaptureScript =
        """
        (() => {
            try {
                Object.defineProperty(navigator, "webdriver", { get: () => undefined });
            } catch {}

            if (window.__nekosharpComixBlobHookInstalled) return;
            window.__nekosharpComixBlobHookInstalled = true;
            window.__nekosharpComixBlobHits = [];
            window.__nekosharpComixBlobMap = new Map();
            window.__nekosharpComixBlobErrors = [];

            const originalCreateObjectUrl = URL.createObjectURL.bind(URL);
            URL.createObjectURL = function(blob) {
                const url = originalCreateObjectUrl(blob);
                try {
                    window.__nekosharpComixBlobMap.set(url, blob);
                    window.__nekosharpComixBlobHits.push({
                        url,
                        type: blob && blob.type ? blob.type : "",
                        size: blob && typeof blob.size === "number" ? blob.size : 0,
                        ts: Date.now()
                    });
                } catch (error) {
                    window.__nekosharpComixBlobErrors.push(String(error && error.stack || error));
                }

                return url;
            };

            URL.revokeObjectURL = function() {
                // Keep blob mapped for later extraction.
                return undefined;
            };
        })();
        """;

    private const string RenderedPageCaptureScript =
        """
        (() => {
            if (window.__nekosharpComixRenderedPageHookInstalled) return;

            const state = window.__nekosharpComixRenderedPageState = {
                blobs: Object.create(null)
            };
            const originalCreate = URL.createObjectURL.bind(URL);
            const originalRevoke = URL.revokeObjectURL.bind(URL);

            URL.createObjectURL = function(blob) {
                const url = originalCreate(blob);
                try {
                    state.blobs[url] = blob;
                } catch (_) {}
                return url;
            };

            URL.revokeObjectURL = function(url) {
                try {
                    if (!state.blobs[url]) {
                        originalRevoke(url);
                    }
                } catch (_) {}
            };

            window.__nekosharpComixRenderedPageHookInstalled = true;
        })();
        """;

    private readonly HttpClient _http;
    private readonly LogService? _log;
    private readonly CloudflareCredentialStore? _cfStore;

    public string Name => "Comix";
    public string BaseUrl => BaseUrlStatic;

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
        _http.DefaultRequestHeaders.Referrer = SiteRootUri;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", BaseUrlStatic);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
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
        var mangaUrl = BuildMangaUrl(parsed.HashId, url, parsed.MangaSegment);

        try
        {
            var document = await LoadHtmlDocumentAsync(mangaUrl, ct);
            var htmlManga = BuildMangaInfoFromHtml(document, mangaUrl, parsed.HashId, parsed.MangaSegment);
            _log?.Info($"[Comix] Manga info loaded via HTML for manga={parsed.HashId}");
            return htmlManga;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn(
                $"[Comix] HTML direto falhou para manga/{parsed.HashId}: {ex.GetType().Name}: {ex.Message}. Tentando browser.");
        }

        try
        {
            var browserManga = await GetMangaInfoFromBrowserAsync(parsed, ct);
            _log?.Info($"[Comix] Manga info loaded via browser for manga={parsed.HashId}");
            return browserManga;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn(
                $"[Comix] Fallback browser falhou para manga/{parsed.HashId}: {ex.GetType().Name}: {ex.Message}. Tentando HTML.");
        }

        try
        {
            var pageManga = await GetMangaInfoFromPageAsync(parsed, ct);
            _log?.Info($"[Comix] Manga info loaded via HTML for manga={parsed.HashId}");
            return pageManga;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn(
                $"[Comix] Fallback HTML falhou para manga/{parsed.HashId}: {ex.GetType().Name}: {ex.Message}. Tentando API.");
        }

        var result = await GetResultAsync($"manga/{Uri.EscapeDataString(parsed.HashId)}", ct);
        return BuildMangaInfo(result, parsed);
    }

    private async Task<Manga> GetMangaInfoFromPageAsync(ComixUrlRef parsed, CancellationToken ct)
    {
        var mangaUrl = $"{BaseUrlStatic}/title/{parsed.MangaSegment}";
        using var request = new HttpRequestMessage(HttpMethod.Get, mangaUrl);
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

        using var response = await _http.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var result = ParseMangaPayloadFromHtml(html);
        return BuildMangaInfo(result, parsed);
    }

    private async Task<Manga> GetMangaInfoFromBrowserAsync(ComixUrlRef parsed, CancellationToken ct)
    {
        var mangaUrl = $"{BaseUrlStatic}/title/{parsed.MangaSegment}";
        var payload = await CaptureBrowserPayloadAsync(
            mangaUrl,
            captureScript: string.Empty,
            payloadExpression: "() => document.getElementById('initial-data')?.textContent || ''",
            progressExpression: null,
            ct);

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("queries", out var queries) ||
            queries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("queries não encontrado no initial-data via browser do Comix.");
        }

        foreach (var property in queries.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object)
                continue;

            if (!string.IsNullOrWhiteSpace(GetString(value, "title")) &&
                !string.IsNullOrWhiteSpace(GetString(value, "hid")))
            {
                return BuildMangaInfo(value.Clone(), parsed);
            }
        }

        throw new InvalidOperationException("payload de manga não encontrado no initial-data via browser do Comix.");
    }

    private Manga BuildMangaInfo(JsonElement result, ComixUrlRef parsed)
    {
        var title = GetString(result, "title") ?? $"Comix {parsed.HashId}";
        var canonicalUrl = BuildMangaUrl(
            parsed.HashId,
            GetString(result, "url"),
            parsed.MangaSegment);
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
            Url = canonicalUrl,
            SiteName = Name
        };
    }

    public async Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
    {
        var parsed = ParseSupportedUrl(url);
        var mangaUrl = $"{BaseUrlStatic}/title/{parsed.MangaSegment}";
        string payload;
        try
        {
            payload = await FetchChaptersPayloadFromBrowserApiAsync(mangaUrl, parsed.HashId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn($"[Comix] Browser API de capítulos falhou: {ex.Message}. Tentando captura passiva.");
            payload = await CaptureBrowserPayloadAsync(
                mangaUrl,
                ChapterCaptureScript,
                "() => window.__nekosharpComixChapterState?.payload || ''",
                "() => Object.keys(window.__nekosharpComixChapterState?.pages || {}).length",
                ct);
        }

        var candidates = ParseChapterPayload(payload);
        var chapters = BuildChapterList(parsed.MangaSegment, candidates);
        _log?.Info($"[Comix] Loaded {chapters.Count} chapters for manga={parsed.HashId}");
        return chapters;
    }

    public async Task<List<MangaPage>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var parsed = ParseSupportedUrl(chapter.Url);
        if (parsed.Kind != ComixUrlKind.Chapter || parsed.ChapterId <= 0)
            throw new ArgumentException(
                "Capítulo inválido do Comix. Use uma URL no formato /title/<hash>/<chapterId>.",
                nameof(chapter));

        string payload;
        try
        {
            payload = await FetchChapterDetailPayloadFromBrowserApiAsync(chapter.Url, parsed.ChapterId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn($"[Comix] Browser API de páginas falhou: {ex.Message}. Tentando captura passiva.");
            payload = await CaptureBrowserPayloadAsync(
                chapter.Url,
                PageCaptureScript,
                "() => window.__nekosharpComixPagePayload || ''",
                progressExpression: null,
                ct);
        }

        var pages = ParsePagePayload(payload, chapter.Url);

        if (pages.Count == 0)
            throw new InvalidOperationException($"Capítulo {parsed.ChapterId} não possui imagens.");

        return pages;
    }

    public IReadOnlyList<string> GetPageDownloadCandidates(string imageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrl);

        var candidates = new List<string> { imageUrl };
        foreach (var fallback in ScramblePathFallbacks)
        {
            var candidate = ScramblePathRegex.Replace(imageUrl, fallback, 1);
            if (!candidate.Equals(imageUrl, StringComparison.Ordinal) &&
                !candidates.Contains(candidate, StringComparer.Ordinal))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    public void ApplyPageDownloadHeaders(HttpRequestMessage request, string imageUrl)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isScrambled = imageUrl.Contains("#scrambled", StringComparison.Ordinal);
        var requestUri = StripFragment(request.RequestUri);
        if (requestUri is not null)
            request.RequestUri = requestUri;

        request.Headers.Referrer = SiteRootUri;
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        var imageHost = requestUri?.Host ?? string.Empty;
        request.Headers.Remove("Origin");
        if (isScrambled ||
            string.IsNullOrWhiteSpace(imageHost) ||
            imageHost.Contains("comix.to", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Origin", BaseUrlStatic);
        }
    }

    public async Task CopyPageResponseAsync(
        HttpResponseMessage response,
        Stream destination,
        string imageUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(destination);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        if (!TryGetEncodingParameters(response, out var seed, out var encodedLength) ||
            seed == 0 ||
            encodedLength <= 0)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }

        await DecodeEncodedPrefixAsync(source, destination, seed, encodedLength, ct);
    }

    public bool ShouldUseRenderedPageFallback(string imageUrl)
    {
        return imageUrl.Contains("#scrambled", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyDictionary<int, RenderedPageDownload>> TryRenderChapterPagesAsync(
        Chapter chapter,
        IReadOnlyList<MangaPage> pages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count == 0)
            return new Dictionary<int, RenderedPageDownload>();

        var protectedPages = pages
            .Where(static candidate => candidate.Number > 0 && IsProtectedPageUrl(candidate.ImageUrl))
            .OrderBy(static candidate => candidate.Number)
            .ToArray();
        if (protectedPages.Length == 0)
            return new Dictionary<int, RenderedPageDownload>();

        await BrowserRenderLock.WaitAsync(ct);
        try
        {
            await using var browser = await LaunchBrowserAsync(ct);
            await using var page = await browser.NewPageAsync();
            await PrepareBrowserPageAsync(page, chapter.Url);
            await page.EvaluateExpressionOnNewDocumentAsync(BlobCaptureScript);

            await page.GoToAsync(chapter.Url, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
            });

            var renderedPages = new Dictionary<int, RenderedPageDownload>();
            foreach (var chapterPage in protectedPages)
            {
                ct.ThrowIfCancellationRequested();

                if (!await NavigateReaderToPageAsync(page, chapterPage.Number, ct))
                {
                    _log?.Warn(
                        $"[Comix] Não foi possível navegar até a página {chapterPage.Number} durante captura renderizada.");
                    continue;
                }

                var imageDataUrl = await CaptureProtectedReaderPageDataUrlAsync(
                    page,
                    chapterPage.Number,
                    ct);
                if (string.IsNullOrWhiteSpace(imageDataUrl))
                {
                    _log?.Warn(
                        $"[Comix] Página protegida {chapterPage.Number} não expôs blob/data URL capturável.");
                    continue;
                }

                try
                {
                    var sourceBytes = DecodeCanvasDataUrl(imageDataUrl);
                    var sourceExtension = DetectImageExtension(
                        sourceBytes,
                        GetMimeTypeFromDataUrl(imageDataUrl));
                    if (string.IsNullOrWhiteSpace(sourceExtension))
                        sourceExtension = GetUrlImageExtension(chapterPage.ImageUrl);

                    renderedPages[chapterPage.Number] = new RenderedPageDownload(
                        chapterPage.Number,
                        sourceBytes,
                        sourceExtension);
                }
                catch (Exception ex)
                {
                    _log?.Warn(
                        $"[Comix] Falha ao decodificar blob da página {chapterPage.Number}: {ex.Message}");
                }
            }

            _log?.Info(
                $"[Comix] Capturadas {renderedPages.Count}/{protectedPages.Length} página(s) protegidas do capítulo {chapter.Number} via blob.");
            return renderedPages;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn(
                $"[Comix] Falha ao renderizar capítulo inteiro no browser ({chapter.Url}): {ex.Message}");
            return new Dictionary<int, RenderedPageDownload>();
        }
        finally
        {
            BrowserRenderLock.Release();
        }
    }

    public async Task<bool> TryWriteRenderedPageAsync(
        string chapterUrl,
        int pageNumber,
        string imageUrl,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterUrl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrl);
        ArgumentNullException.ThrowIfNull(destination);

        await BrowserRenderLock.WaitAsync(ct);
        try
        {
            await using var browser = await LaunchBrowserAsync(ct);
            await using var page = await browser.NewPageAsync();
            await PrepareBrowserPageAsync(page, chapterUrl);
            await page.EvaluateExpressionOnNewDocumentAsync(BlobCaptureScript);

            await page.GoToAsync(chapterUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
            });

            if (!await NavigateReaderToPageAsync(page, pageNumber, ct))
            {
                _log?.Warn(
                    $"[Comix] Não foi possível navegar até a página {pageNumber} em {chapterUrl}.");
                return false;
            }

            var imageDataUrl = await CaptureProtectedReaderPageDataUrlAsync(page, pageNumber, ct);
            if (!string.IsNullOrWhiteSpace(imageDataUrl))
            {
                var blobBytes = DecodeCanvasDataUrl(imageDataUrl);
                await WriteImageBytesAsync(blobBytes, imageUrl, destination, ct);
                _log?.Info(
                    $"[Comix] Blob capturado para página {pageNumber} ({blobBytes.Length} bytes).");
                return true;
            }

            var imageRequestUrl = StripFragment(new Uri(imageUrl))?.ToString() ?? imageUrl;
            await using var imagePage = await browser.NewPageAsync();
            await PrepareBrowserPageAsync(imagePage, chapterUrl);

            var imageResponse = await imagePage.GoToAsync(imageRequestUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
            });
            if (imageResponse is null || !imageResponse.Ok)
                return false;

            var sourceBytes = await imageResponse.BufferAsync();
            if (TryGetEncodingParameters(
                    imageResponse.Headers,
                    out var seed,
                    out var encodedLength) &&
                seed != 0 &&
                encodedLength > 0)
            {
                sourceBytes = DecodeEncodedPrefix(sourceBytes, seed, encodedLength);
            }

            var sourceMimeType = TryGetHeader(
                                     imageResponse.Headers,
                                     "content-type",
                                     out var contentType)
                                 ? contentType.Split(';', 2)[0]
                                 : GetCanvasMimeType(imageUrl);
            var sourceDataUrl =
                $"data:{sourceMimeType};base64,{Convert.ToBase64String(sourceBytes)}";

            // The reader overrides canvas extraction. A clean tab preserves the native API.
            await using var renderPage = await browser.NewPageAsync();
            await renderPage.GoToAsync("about:blank");
            var canvasDataUrl = await renderPage.EvaluateFunctionAsync<string>(
                """
                async sourceDataUrl => {
                    const image = new Image();
                    image.src = sourceDataUrl;
                    await image.decode();

                    const canvas = document.createElement("canvas");
                    canvas.width = image.naturalWidth;
                    canvas.height = image.naturalHeight;
                    canvas.getContext("2d").drawImage(image, 0, 0);
                    return canvas.toDataURL("image/png");
                }
                """,
                sourceDataUrl);

            var canvasBytes = DecodeCanvasDataUrl(canvasDataUrl);
            await WriteImageBytesAsync(canvasBytes, imageUrl, destination, ct);
            _log?.Info(
                $"[Comix] Canvas capturado para página {pageNumber} ({canvasBytes.Length} bytes PNG).");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn(
                $"[Comix] Falha ao capturar página renderizada {pageNumber}: {ex.Message}");
            return false;
        }
        finally
        {
            BrowserRenderLock.Release();
        }
    }

    private static async Task<string> CaptureProtectedReaderPageDataUrlAsync(
        IPage page,
        int targetPage,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BrowserRenderStepTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var dataUrl = await page.EvaluateFunctionAsync<string>(
                """
                async targetPage => {
                    const getActivePage = () => {
                        const active = document.querySelector(".rpage-progress__seg.is-active");
                        const label = active?.getAttribute("aria-label") || "";
                        const match = label.match(/(\d+)$/);
                        return match ? Number(match[1]) : 0;
                    };

                    const isVisible = element => {
                        const rect = element.getBoundingClientRect();
                        if (rect.width < 48 || rect.height < 48) return false;

                        const style = window.getComputedStyle(element);
                        if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity || "1") === 0) {
                            return false;
                        }

                        return !(rect.bottom < 0 || rect.right < 0 || rect.top > window.innerHeight || rect.left > window.innerWidth);
                    };

                    const getCandidateSrc = () => {
                        const targetAlt = `Page ${targetPage}`;
                        const candidates = [...document.querySelectorAll("img[src]")]
                            .filter(img =>
                                isVisible(img) &&
                                img.complete &&
                                img.naturalWidth > 16 &&
                                img.naturalHeight > 16 &&
                                ((img.currentSrc || img.src || "").startsWith("blob:") ||
                                 (img.currentSrc || img.src || "").startsWith("data:")))
                            .map(img => ({
                                src: img.currentSrc || img.src || "",
                                alt: img.alt || "",
                                area: (img.naturalWidth || img.width || 0) * (img.naturalHeight || img.height || 0)
                            }))
                            .sort((left, right) => {
                                const leftExact = left.alt === targetAlt ? 1 : 0;
                                const rightExact = right.alt === targetAlt ? 1 : 0;
                                if (leftExact !== rightExact) return rightExact - leftExact;
                                return right.area - left.area;
                            });

                        return candidates[0]?.src || "";
                    };

                    if (getActivePage() !== targetPage)
                        return "";

                    const src = getCandidateSrc();
                    if (!src) return "";
                    if (src.startsWith("data:")) return src;

                    const blob = window.__nekosharpComixBlobMap?.get(src);
                    if (!blob) return "";

                    try {
                        return await new Promise(resolve => {
                            const reader = new FileReader();
                            reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
                            reader.onerror = () => resolve("");
                            reader.readAsDataURL(blob);
                        });
                    } catch (_) {
                        return "";
                    }
                }
                """,
                targetPage);

            if (!string.IsNullOrWhiteSpace(dataUrl))
                return dataUrl;

            await Task.Delay(200, ct);
        }

        return string.Empty;
    }

    private static async Task<bool> NavigateReaderToPageAsync(
        IPage page,
        int targetPage,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BrowserPayloadTimeout;
        var directNavigationAttempted = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var currentPage = await page.EvaluateFunctionAsync<int>(
                """
                () => {
                    const active = document.querySelector(
                        ".rpage-progress__seg.is-active");
                    const label = active?.getAttribute("aria-label") || "";
                    const match = label.match(/(\d+)$/);
                    return match ? Number(match[1]) : 0;
                }
                """);

            if (currentPage == targetPage)
                return true;

            if (currentPage > 0)
            {
                if (!directNavigationAttempted)
                {
                    directNavigationAttempted = true;
                    try
                    {
                        await page.ClickAsync(
                            $"button[aria-label=\"Go to page {targetPage}\"]");
                        await Task.Delay(300, ct);
                        continue;
                    }
                    catch
                    {
                        // Keyboard navigation remains available when the progress control is hidden.
                    }
                }

                await page.Keyboard.PressAsync(
                    currentPage < targetPage ? "ArrowRight" : "ArrowLeft");
            }

            await Task.Delay(150, ct);
        }

        return false;
    }

    private static async Task<byte[]> CaptureRenderedPageBytesAsync(
        IPage page,
        int pageNumber,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BrowserPayloadTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var base64 = await page.EvaluateFunctionAsync<string>(
                """
                async targetPage => {
                    const targetAlt = `Page ${targetPage}`;
                    const image = Array.from(document.querySelectorAll("img"))
                        .find(img => (img.getAttribute("alt") || "") === targetAlt && img.complete);
                    if (!image)
                        return "";

                    const source = image.currentSrc || image.src || "";
                    if (!source)
                        return "";

                    const encodeBytes = async bytes => {
                        let binary = "";
                        for (let index = 0; index < bytes.length; index += 0x8000) {
                            binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
                        }
                        return btoa(binary);
                    };

                    const blobState = window.__nekosharpComixRenderedPageState?.blobs || {};
                    let blob = source.startsWith("blob:") ? blobState[source] || null : null;

                    try {
                        if (!blob) {
                            const response = await fetch(source);
                            if (!response.ok)
                                return "";

                            const bytes = new Uint8Array(await response.arrayBuffer());
                            return await encodeBytes(bytes);
                        }

                        const bytes = new Uint8Array(await blob.arrayBuffer());
                        return await encodeBytes(bytes);
                    } catch (_) {
                        return "";
                    }
                }
                """,
                pageNumber);

            if (!string.IsNullOrWhiteSpace(base64))
                return Convert.FromBase64String(base64);

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for rendered Comix page {pageNumber}.");
    }

    internal static List<ComixChapterCandidate> ParseChapterPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var chapters = new List<ComixChapterCandidate>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = GetInt(item, "id") ?? 0;
            var number = GetDouble(item, "number") ?? 0;
            if (id <= 0)
                continue;

            var group = item.TryGetProperty("group", out var groupElement) &&
                        groupElement.ValueKind == JsonValueKind.Object
                ? groupElement
                : default;

            chapters.Add(new ComixChapterCandidate(
                ChapterId: id,
                Number: number,
                SourceUrl: GetString(item, "url") ?? string.Empty,
                Name: GetString(item, "name") ?? string.Empty,
                Votes: GetInt(item, "votes") ?? 0,
                UpdatedAt: id,
                ScanlationGroupId: group.ValueKind == JsonValueKind.Object
                    ? GetInt(group, "id") ?? 0
                    : 0,
                ScanlationGroupName: group.ValueKind == JsonValueKind.Object
                    ? GetString(group, "name") ?? string.Empty
                    : string.Empty,
                IsOfficial: GetBoolean(item, "isOfficial") || GetBoolean(item, "is_official") ? 1 : 0));
        }

        return chapters;
    }

    internal static List<MangaPage> ParsePagePayload(string json, string chapterUrl)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result))
            root = result;
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
            return [];

        var baseUrl = (GetString(pages, "baseUrl") ?? string.Empty).TrimEnd('/');
        if (!pages.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var output = new List<MangaPage>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var path = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var fullUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : $"{baseUrl}/{path.TrimStart('/')}";
            fullUrl = StripComixVersionQuery(fullUrl);
            var isScrambled = GetInt(item, "s") == 1 || (index + 1) % 4 == 0;
            if (isScrambled)
                fullUrl += "#scrambled";

            output.Add(new MangaPage
            {
                Number = index + 1,
                ImageUrl = fullUrl,
                RefererUrl = chapterUrl
            });
            index++;
        }

        return output;
    }

    internal static List<Chapter> BuildChapterList(
        string mangaSegment,
        IEnumerable<ComixChapterCandidate> candidates)
    {
        return candidates
            .Select(chapter => new Chapter
            {
                Number = chapter.Number,
                Title = BuildChapterTitle(chapter),
                Url = BuildChapterUrl(
                    mangaSegment,
                    chapter.ChapterId,
                    chapter.Number,
                    chapter.SourceUrl)
            })
            .ToList();
    }

    internal static byte[] DecodeEncodedPrefix(byte[] data, int seed, int encodedLength)
    {
        var output = data.ToArray();
        var state = unchecked((uint)seed);
        var limit = Math.Min(output.Length, Math.Max(0, encodedLength));

        for (var index = 0; index < limit; index++)
        {
            state = unchecked(state * EncMultiplier + EncIncrement);
            output[index] = (byte)(output[index] ^ (state >> 24));
        }

        return output;
    }

    internal static byte[] DecodeCanvasDataUrl(string dataUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataUrl);

        var separator = dataUrl.IndexOf(',');
        if (separator <= 0 ||
            !dataUrl.AsSpan(0, separator).Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Data URL do canvas do Comix é inválida.");
        }

        return Convert.FromBase64String(dataUrl[(separator + 1)..]);
    }

    internal static string GetCanvasMimeType(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return "image/png";

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static async Task WriteImageBytesAsync(
        byte[] sourceBytes,
        string imageUrl,
        Stream destination,
        CancellationToken ct)
    {
        var extension = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()
            : string.Empty;

        using var image = Image.Load(sourceBytes);
        if (string.IsNullOrWhiteSpace(extension))
        {
            await destination.WriteAsync(sourceBytes, ct);
            return;
        }

        switch (extension)
        {
            case ".png":
                await image.SaveAsPngAsync(
                    destination,
                    new PngEncoder(),
                    ct);
                break;
            case ".jpg":
            case ".jpeg":
                await image.SaveAsJpegAsync(
                    destination,
                    new JpegEncoder { Quality = 100 },
                    ct);
                break;
            case ".webp":
                await image.SaveAsWebpAsync(
                    destination,
                    new WebpEncoder
                    {
                        FileFormat = WebpFileFormatType.Lossless,
                        Quality = 100
                    },
                    ct);
                break;
            default:
                await destination.WriteAsync(sourceBytes, ct);
                break;
        }
    }

    internal static string DetectImageExtension(byte[] data, string? mimeType = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length >= 12 &&
            data[0] == (byte)'R' &&
            data[1] == (byte)'I' &&
            data[2] == (byte)'F' &&
            data[3] == (byte)'F' &&
            data[8] == (byte)'W' &&
            data[9] == (byte)'E' &&
            data[10] == (byte)'B' &&
            data[11] == (byte)'P')
        {
            return ".webp";
        }

        if (data.Length >= 3 &&
            data[0] == 0xFF &&
            data[1] == 0xD8 &&
            data[2] == 0xFF)
        {
            return ".jpg";
        }

        if (data.Length >= 8 &&
            data[0] == 0x89 &&
            data[1] == 0x50 &&
            data[2] == 0x4E &&
            data[3] == 0x47)
        {
            return ".png";
        }

        if (data.Length >= 6)
        {
            var header = System.Text.Encoding.ASCII.GetString(data, 0, 6);
            if (header is "GIF87a" or "GIF89a")
                return ".gif";
        }

        return mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => string.Empty
        };
    }

    internal static string GetMimeTypeFromDataUrl(string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) ||
            !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var separator = dataUrl.IndexOf(',');
        if (separator <= 5)
            return string.Empty;

        var metadata = dataUrl[5..separator];
        var mime = metadata.Split(';', 2)[0].Trim();
        return mime;
    }

    private static bool IsProtectedPageUrl(string imageUrl)
        => imageUrl.Contains("#scrambled", StringComparison.Ordinal);

    private static string GetUrlImageExtension(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return ".png";

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".png" => ".png",
            ".webp" => ".webp",
            ".gif" => ".gif",
            _ => ".png"
        };
    }

    internal static string StripComixVersionQuery(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            (!url.Contains("?v3", StringComparison.OrdinalIgnoreCase) &&
             !url.Contains("&v3", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fragmentIndex = url.IndexOf('#');
            var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
            var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
            var queryIndex = withoutFragment.IndexOf('?');
            if (queryIndex < 0)
                return url;

            var basePart = withoutFragment[..queryIndex];
            var queryPart = withoutFragment[(queryIndex + 1)..];
            var filtered = string.Join(
                "&",
                queryPart
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(static part => !part.Equals("v3", StringComparison.OrdinalIgnoreCase) &&
                                          !part.StartsWith("v3=", StringComparison.OrdinalIgnoreCase)));

            return string.IsNullOrWhiteSpace(filtered)
                ? basePart + fragment
                : $"{basePart}?{filtered}{fragment}";
        }

        var cleanedQuery = string.Join(
            "&",
            uri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(static part => !part.Equals("v3", StringComparison.OrdinalIgnoreCase) &&
                                      !part.StartsWith("v3=", StringComparison.OrdinalIgnoreCase)));

        return new UriBuilder(uri)
        {
            Query = cleanedQuery
        }.Uri.ToString();
    }

    internal static ComixUrlRef ParseSupportedUrl(string url)
    {
        if (!ComixUrlParser.TryParse(url, out var parsed))
            throw new ArgumentException(
                "URL do Comix inválida. Use /title/<hash>-slug ou /title/<hash>-slug/<chapterId>-slug.",
                nameof(url));

        return parsed;
    }

    private async Task<JsonElement> GetResultAsync(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Comix API retornou {(int)response.StatusCode} ({response.ReasonPhrase}) para '{relativeUrl}'.",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Resposta inválida da API do Comix.");
        }

        return result.Clone();
    }

    private async Task<string> FetchChaptersPayloadFromBrowserApiAsync(
        string mangaUrl,
        string hashId,
        CancellationToken ct)
    {
        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();
        await PrepareBrowserPageAsync(page, mangaUrl);

        await page.GoToAsync(mangaUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
        });

        return await page.EvaluateFunctionAsync<string>(
            """
            async targetHashId => {
                const mainScript = Array.from(document.scripts)
                    .find(script => script.type === "module" && /\/assets\/build\/.+\/dist\/main-/.test(script.src));
                if (!mainScript?.src)
                    throw new Error("Main script do Comix não encontrado.");

                const mainSource = await (await fetch(mainScript.src)).text();
                const envMatch = mainSource.match(/\.\/(env-[^"'`]+\.js)/);
                if (!envMatch)
                    throw new Error("Env script do Comix não encontrado.");

                const envUrl = new URL(envMatch[1], mainScript.src).href;
                const envModule = await import(envUrl);
                const chaptersApi = envModule?.c?.chapters;
                if (typeof chaptersApi !== "function")
                    throw new Error("envModule.c.chapters não está disponível.");

                const items = [];
                let pageNumber = 1;
                while (true) {
                    const result = await chaptersApi(targetHashId, {
                        page: pageNumber,
                        limit: 100
                    });
                    const pageItems = Array.isArray(result?.items) ? result.items : [];
                    items.push(...pageItems);
                    const meta = result?.meta || result?.pagination || {};
                    if (!meta?.hasNext)
                        break;
                    pageNumber += 1;
                    if (pageNumber > 50)
                        break;
                }

                return JSON.stringify(items);
            }
            """,
            hashId);
    }

    private async Task<string> FetchChapterDetailPayloadFromBrowserApiAsync(
        string chapterUrl,
        int chapterId,
        CancellationToken ct)
    {
        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();
        await PrepareBrowserPageAsync(page, chapterUrl);

        await page.GoToAsync(chapterUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
        });

        return await page.EvaluateFunctionAsync<string>(
            """
            async targetChapterId => {
                const mainScript = Array.from(document.scripts)
                    .find(script => script.type === "module" && /\/assets\/build\/.+\/dist\/main-/.test(script.src));
                if (!mainScript?.src)
                    throw new Error("Main script do Comix não encontrado.");

                const mainSource = await (await fetch(mainScript.src)).text();
                const envMatch = mainSource.match(/\.\/(env-[^"'`]+\.js)/);
                if (!envMatch)
                    throw new Error("Env script do Comix não encontrado.");

                const envUrl = new URL(envMatch[1], mainScript.src).href;
                const envModule = await import(envUrl);
                const result = await envModule.b.get(`/chapters/${targetChapterId}`);
                return JSON.stringify({ result });
            }
            """,
            chapterId);
    }

    private async Task<string> CaptureBrowserPayloadAsync(
        string pageUrl,
        string captureScript,
        string payloadExpression,
        string? progressExpression,
        CancellationToken ct)
    {
        await using var browser = await LaunchBrowserAsync(ct);
        await using var page = await browser.NewPageAsync();
        await PrepareBrowserPageAsync(page, pageUrl);
        if (!string.IsNullOrWhiteSpace(captureScript))
            await page.EvaluateExpressionOnNewDocumentAsync(captureScript);

        await page.GoToAsync(pageUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
        });

        var deadline = DateTimeOffset.UtcNow + BrowserPayloadTimeout;
        var lastProgress = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var payload = await page.EvaluateFunctionAsync<string>(payloadExpression);
            if (!string.IsNullOrWhiteSpace(payload))
                return payload;

            if (!string.IsNullOrWhiteSpace(progressExpression))
            {
                var progress = await page.EvaluateFunctionAsync<int>(progressExpression);
                if (progress > lastProgress)
                {
                    lastProgress = progress;
                    deadline = DateTimeOffset.UtcNow + BrowserPayloadTimeout;
                }
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for Comix browser payload from '{pageUrl}'.");
    }

    private async Task PrepareBrowserPageAsync(IPage page, string pageUrl)
    {
        var credentials = _cfStore is null ? null : await _cfStore.TryGetAsync("comix.to");
        var userAgent = string.IsNullOrWhiteSpace(credentials?.UserAgent)
            ? UserAgentProvider.Default
            : credentials.UserAgent;

        await page.SetUserAgentAsync(userAgent);
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
        {
            ["Referer"] = SiteRootUri.ToString(),
            ["Origin"] = BaseUrlStatic,
            ["Accept"] = "*/*"
        });
        await ApplyStoredCookiesAsync(page, pageUrl, credentials);
    }

    private static async Task ApplyStoredCookiesAsync(
        IPage page,
        string pageUrl,
        CloudflareCredentials? credentials)
    {
        if (credentials is null || credentials.AllCookies.Count == 0)
            return;

        var pageUri = new Uri(pageUrl);
        var origin = pageUri.GetLeftPart(UriPartial.Authority);
        var cookies = credentials.AllCookies
            .Where(static cookie => !string.IsNullOrWhiteSpace(cookie.Key))
            .Select(cookie => new CookieParam
            {
                Name = cookie.Key,
                Value = cookie.Value,
                Url = origin,
                Path = "/",
                Secure = pageUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();

        if (cookies.Length > 0)
            await page.SetCookieAsync(cookies);
    }

    private async Task<IBrowser> LaunchBrowserAsync(CancellationToken ct)
    {
        Exception? systemLaunchFailure = null;

        var systemChrome = FindSystemChrome();
        if (!string.IsNullOrWhiteSpace(systemChrome))
        {
            var tempProfile = CreateTempBrowserProfile();
            try
            {
                _log?.Info($"[Comix] Launching browser: {systemChrome}");
                var options = CreateBrowserLaunchOptions(systemChrome, tempProfile);
                ct.ThrowIfCancellationRequested();
                return await LaunchBrowserInternalAsync(options, tempProfile, ct);
            }
            catch (Exception ex)
            {
                systemLaunchFailure = ex;
                _log?.Warn(
                    $"[Comix] Failed to launch system Chromium ({systemChrome}): {ex.GetType().Name}: {ex.Message}");
                TryDeleteDirectory(tempProfile);
            }
        }
        else
        {
            _log?.Warn("[Comix] No system Chrome/Chromium found. Falling back to BrowserFetcher.");
        }

        try
        {
            var options = CreateBrowserLaunchOptions(executablePath: null, userDataDir: null);
            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
        catch (Exception directLaunchEx)
        {
            _log?.Warn(
                $"[Comix] Direct Puppeteer launch failed: {directLaunchEx.GetType().Name}: {directLaunchEx.Message}. Downloading bundled browser...");

            try
            {
                var fetcher = new BrowserFetcher();
                var installed = await fetcher.DownloadAsync();
                var downloadedBrowserPath = fetcher.GetExecutablePath(installed.BuildId);
                var tempProfile = CreateTempBrowserProfile();
                var options = CreateBrowserLaunchOptions(downloadedBrowserPath, tempProfile);

                _log?.Info($"[Comix] Launching downloaded browser: {downloadedBrowserPath}");
                ct.ThrowIfCancellationRequested();
                return await LaunchBrowserInternalAsync(options, tempProfile, ct);
            }
            catch (Exception fetcherEx)
            {
                _log?.Warn(
                    $"[Comix] BrowserFetcher launch failed: {fetcherEx.GetType().Name}: {fetcherEx.Message}");

                var message = systemLaunchFailure is null
                    ? $"Failed to launch browser. Direct launch: {directLaunchEx.GetType().Name}: {directLaunchEx.Message}. BrowserFetcher: {fetcherEx.GetType().Name}: {fetcherEx.Message}"
                    : $"Failed to launch browser. System Chromium: {systemLaunchFailure.GetType().Name}: {systemLaunchFailure.Message}. Direct launch: {directLaunchEx.GetType().Name}: {directLaunchEx.Message}. BrowserFetcher: {fetcherEx.GetType().Name}: {fetcherEx.Message}";

                throw new InvalidOperationException(message, fetcherEx);
            }
        }
    }

    private static LaunchOptions CreateBrowserLaunchOptions(string? executablePath, string? userDataDir)
    {
        return new LaunchOptions
        {
            Headless = true,
            ExecutablePath = executablePath,
            UserDataDir = userDataDir,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--disable-features=IsolateOrigins,site-per-process",
                "--start-minimized",
                "--window-size=1280,850",
            ],
            IgnoredDefaultArgs = ["--enable-automation"],
            DefaultViewport = null,
        };
    }

    private static async Task<IBrowser> LaunchBrowserInternalAsync(
        LaunchOptions options,
        string? tempProfile,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Puppeteer.LaunchAsync(options);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(tempProfile))
                TryDeleteDirectory(tempProfile);

            throw;
        }
    }

    private static string CreateTempBrowserProfile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "nekosharp-comix-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? FindSystemChrome()
    {
        var envChromePath = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(envChromePath) && File.Exists(envChromePath))
            return envChromePath;

        string[] posixCandidates =
        [
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
            "/snap/bin/chromium",
            "/usr/bin/brave-browser",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Chromium.app/Contents/MacOS/Chromium",
            "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
        ];

        var windowsCandidates = new List<string>();
        foreach (var envVar in new[] { "PROGRAMFILES", "PROGRAMFILES(X86)", "LOCALAPPDATA", "PROGRAMW6432" })
        {
            var root = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(root))
                continue;

            windowsCandidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            windowsCandidates.Add(Path.Combine(root, "Google", "Chrome Beta", "Application", "chrome.exe"));
            windowsCandidates.Add(Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        }

        var candidates = OperatingSystem.IsWindows() ? windowsCandidates : posixCandidates.ToList();
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static async Task DecodeEncodedPrefixAsync(
        Stream source,
        Stream destination,
        int seed,
        int encodedLength,
        CancellationToken ct)
    {
        var buffer = new byte[EncReadChunkSize];
        var state = unchecked((uint)seed);
        var decoded = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            var limit = Math.Min(read, Math.Max(0, encodedLength - decoded));
            for (var index = 0; index < limit; index++)
            {
                state = unchecked(state * EncMultiplier + EncIncrement);
                buffer[index] = (byte)(buffer[index] ^ (state >> 24));
            }

            decoded += limit;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static bool TryGetEncodingParameters(
        HttpResponseMessage response,
        out int seed,
        out int encodedLength)
    {
        seed = 0;
        encodedLength = 0;

        return TryGetIntHeader(response, "x-enc-seed", out seed) &&
               TryGetIntHeader(response, "x-enc-len", out encodedLength);
    }

    private static bool TryGetEncodingParameters(
        IReadOnlyDictionary<string, string> headers,
        out int seed,
        out int encodedLength)
    {
        seed = 0;
        encodedLength = 0;

        return TryGetHeader(headers, "x-enc-seed", out var seedValue) &&
               int.TryParse(
                   seedValue,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out seed) &&
               TryGetHeader(headers, "x-enc-len", out var lengthValue) &&
               int.TryParse(
                   lengthValue,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out encodedLength);
    }

    private static bool TryGetHeader(
        IReadOnlyDictionary<string, string> headers,
        string name,
        out string value)
    {
        foreach (var header in headers)
        {
            if (!header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = header.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetIntHeader(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Uri? StripFragment(Uri? uri)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Fragment))
            return uri;

        return new UriBuilder(uri) { Fragment = string.Empty }.Uri;
    }

    private static string BuildMangaUrl(string hashId, string? canonicalUrl, string fallbackSegment)
    {
        if (!string.IsNullOrWhiteSpace(canonicalUrl))
        {
            var normalized = canonicalUrl.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute) &&
                absolute.Host.Contains("comix.to", StringComparison.OrdinalIgnoreCase))
            {
                return absolute.ToString();
            }

            if (normalized.StartsWith("/title/", StringComparison.OrdinalIgnoreCase))
                return $"{BaseUrlStatic}{normalized}";
        }

        return $"{BaseUrlStatic}/title/{(string.IsNullOrWhiteSpace(fallbackSegment) ? hashId : fallbackSegment)}";
    }

    private static string BuildChapterUrl(
        string mangaSegment,
        int chapterId,
        double chapterNumber,
        string? sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var normalized = sourceUrl.Trim();
            var titleIndex = normalized.IndexOf("/title/", StringComparison.OrdinalIgnoreCase);
            if (titleIndex >= 0)
                return $"{BaseUrlStatic}{normalized[titleIndex..]}";
        }

        var number = chapterNumber.ToString("0.####################", CultureInfo.InvariantCulture);
        return $"{BaseUrlStatic}/title/{mangaSegment.Trim('/')}/{chapterId}-chapter-{number}";
    }

    private static string BuildChapterTitle(ComixChapterCandidate chapter)
    {
        var number = chapter.Number.ToString("0.####################", CultureInfo.InvariantCulture);
        var name = chapter.Name.Trim();
        return string.IsNullOrWhiteSpace(name)
            ? $"Chapter {number}"
            : $"Chapter {number}: {name}";
    }

    private async Task<IDocument> LoadHtmlDocumentAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Remove("Accept");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));

        using var response = await _http.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var browser = BrowsingContext.New(AngleSharp.Configuration.Default);
        return await browser.OpenAsync(req => req.Content(html).Address(url), ct);
    }

    internal static Manga BuildMangaInfoFromHtml(
        IDocument document,
        string requestedUrl,
        string fallbackHashId,
        string fallbackSegment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackHashId);

        var canonicalUrl = BuildMangaUrl(
            fallbackHashId,
            GetFirstAttribute(document, "href", "link[rel='canonical']") ??
            GetFirstAttribute(document, "content", "meta[property='og:url']", "meta[name='twitter:url']"),
            fallbackSegment);

        var title = NormalizeComixTitle(
            GetFirstAttribute(document, "content", "meta[property='og:title']", "meta[name='twitter:title']") ??
            GetFirstText(document, "h1"),
            document.Title,
            fallbackHashId);

        var coverUrl = ToAbsoluteUrl(
                           canonicalUrl,
                           GetFirstAttribute(document, "content", "meta[property='og:image']", "meta[name='twitter:image']")) ??
                       ExtractImageSource(document.QuerySelector("img"), canonicalUrl) ??
                       string.Empty;

        var description =
            GetFirstAttribute(
                document,
                "content",
                "meta[name='description']",
                "meta[property='og:description']",
                "meta[name='twitter:description']") ??
            string.Empty;

        return new Manga
        {
            Name = title,
            CoverUrl = coverUrl,
            Description = description.Trim(),
            Url = canonicalUrl,
            SiteName = "Comix"
        };
    }

    private static string BuildDescription(JsonElement manga)
    {
        var sections = new List<string>();
        var score = GetDouble(manga, "ratedAvg") ?? GetDouble(manga, "rated_avg");
        if (score is > 0)
            sections.Add(BuildFancyScore(score.Value));

        var synopsis = GetString(manga, "synopsis");
        if (!string.IsNullOrWhiteSpace(synopsis))
            sections.Add(synopsis);

        var extras = new List<string>();
        var year = GetInt(manga, "year");
        if (year is > 0)
            extras.Add($"Year: {year}");

        var language = GetString(manga, "originalLanguage") ?? GetString(manga, "original_language");
        if (!string.IsNullOrWhiteSpace(language))
            extras.Add($"Language: {language.ToUpperInvariant()}");

        var rating = GetString(manga, "contentRating") ?? GetString(manga, "content_rating");
        if (!string.IsNullOrWhiteSpace(rating))
            extras.Add($"Content rating: {char.ToUpperInvariant(rating[0])}{rating[1..]}");

        var rank = GetInt(manga, "rank");
        if (rank is > 0)
            extras.Add($"Rank: #{rank}");

        var ratedCount = GetLong(manga, "ratedCount") ?? GetLong(manga, "rated_count");
        if (ratedCount is > 0)
            extras.Add($"Rated by: {ratedCount}");

        var follows = GetLong(manga, "followsTotal") ?? GetLong(manga, "follows_total");
        if (follows is > 0)
            extras.Add($"Followed by: {follows}");

        if (extras.Count > 0)
            sections.Add(string.Join('\n', extras));

        return string.Join("\n\n", sections);
    }

    internal static JsonElement ParseMangaPayloadFromHtml(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);

        var match = InitialDataScriptRegex.Match(html);
        if (!match.Success)
            throw new InvalidOperationException("initial-data não encontrado no HTML do Comix.");

        var payload = WebUtility.HtmlDecode(match.Groups["json"].Value).Trim();
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("queries não encontrado no initial-data do Comix.");

        foreach (var property in queries.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object)
                continue;

            if (!string.IsNullOrWhiteSpace(GetString(value, "title")) &&
                !string.IsNullOrWhiteSpace(GetString(value, "hid")))
            {
                return value.Clone();
            }
        }

        throw new InvalidOperationException("payload de manga não encontrado no initial-data do Comix.");
    }

    private static string BuildFancyScore(double score)
    {
        var stars = (int)Math.Round(score / 2, MidpointRounding.AwayFromZero);
        stars = Math.Clamp(stars, 0, 5);
        return $"{new string('★', stars)}{new string('☆', 5 - stars)} " +
               score.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) &&
               nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var result) && result,
            _ => false
        };
    }

    private static string? GetFirstText(IParentNode parent, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var value = parent.QuerySelector(selector)?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? GetFirstAttribute(IParentNode parent, string attribute, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var value = parent.QuerySelector(selector)?.GetAttribute(attribute)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ToAbsoluteUrl(string baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        var trimmedHref = href.Trim();
        if (Uri.TryCreate(trimmedHref, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absolute.ToString();
        }

        return Uri.TryCreate(new Uri(baseUrl), trimmedHref, out var combined)
            ? combined.ToString()
            : null;
    }

    private static string? ExtractImageSource(IElement? imageNode, string refererUrl)
    {
        if (imageNode is null)
            return null;

        var rawSrc = imageNode.GetAttribute("data-src-url") ??
                     imageNode.GetAttribute("data-src") ??
                     imageNode.GetAttribute("data-lazy-src") ??
                     imageNode.GetAttribute("data-lazy") ??
                     imageNode.GetAttribute("data-url") ??
                     imageNode.GetAttribute("srcset") ??
                     imageNode.GetAttribute("src") ??
                     imageNode.GetAttribute("href");

        var normalized = NormalizeImageSource(rawSrc);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : ToAbsoluteUrl(refererUrl, normalized);
    }

    private static string? NormalizeImageSource(string? rawSrc)
    {
        if (string.IsNullOrWhiteSpace(rawSrc))
            return null;

        var value = rawSrc.Trim();
        if (value.Contains(','))
            value = value.Split(',', 2, StringSplitOptions.None)[0].Trim();

        if (value.Contains(' '))
            value = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        return value;
    }

    private static string NormalizeComixTitle(string? preferredTitle, string? documentTitle, string fallbackHashId)
    {
        var candidate = !string.IsNullOrWhiteSpace(preferredTitle)
            ? preferredTitle.Trim()
            : documentTitle?.Trim();

        if (string.IsNullOrWhiteSpace(candidate))
            return $"Comix {fallbackHashId}";

        foreach (var separator in new[] { " - ", " | " })
        {
            var suffix = $"{separator}Comix";
            if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return candidate[..^suffix.Length].Trim();
        }

        return candidate;
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
