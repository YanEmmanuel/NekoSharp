using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using PuppeteerSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using MangaPage = NekoSharp.Core.Models.Page;

namespace NekoSharp.Core.Providers.Comix;

public sealed class ComixScraper :
    IScraper,
    ICustomPageDownloadProvider,
    IRenderedPageFallbackProvider
{
    private const string ApiBaseUrl = "https://comix.to/api/v1/";
    private const string BaseUrlStatic = "https://comix.to";
    private const int EncMultiplier = 1_000_005;
    private const int EncIncrement = 1_234_567_891;
    private const int EncReadChunkSize = 8192;

    private static readonly Uri SiteRootUri = new($"{BaseUrlStatic}/");
    private static readonly TimeSpan BrowserPayloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim RenderedPageFallbackLock = new(1, 1);
    private static readonly Regex ScramblePathRegex = new(
        "/s?i+/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
        var result = await GetResultAsync($"manga/{Uri.EscapeDataString(parsed.HashId)}", ct);

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
        var payload = await CaptureBrowserPayloadAsync(
            mangaUrl,
            ChapterCaptureScript,
            "() => window.__nekosharpComixChapterState?.payload || ''",
            "() => Object.keys(window.__nekosharpComixChapterState?.pages || {}).length",
            ct);

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

        var payload = await CaptureBrowserPayloadAsync(
            chapter.Url,
            PageCaptureScript,
            "() => window.__nekosharpComixPagePayload || ''",
            progressExpression: null,
            ct);
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

        await RenderedPageFallbackLock.WaitAsync(ct);
        try
        {
            await using var browser = await LaunchBrowserAsync(ct);
            await using var page = await browser.NewPageAsync();
            await PrepareBrowserPageAsync(page, chapterUrl);

            await page.GoToAsync(chapterUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)BrowserPayloadTimeout.TotalMilliseconds
            });

            var mimeType = GetCanvasMimeType(imageUrl);
            if (!await NavigateReaderToPageAsync(page, pageNumber, ct))
            {
                _log?.Warn(
                    $"[Comix] Não foi possível navegar até a página {pageNumber} em {chapterUrl}.");
                return false;
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
                                 : mimeType;
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
            await WriteCanvasImageAsync(canvasBytes, imageUrl, destination, ct);
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
                $"[Comix] Falha ao renderizar canvas da página {pageNumber}: {ex.Message}");
            return false;
        }
        finally
        {
            RenderedPageFallbackLock.Release();
        }
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

    private static async Task WriteCanvasImageAsync(
        byte[] pngBytes,
        string imageUrl,
        Stream destination,
        CancellationToken ct)
    {
        var extension = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()
            : string.Empty;

        if (extension == ".png")
        {
            await destination.WriteAsync(pngBytes, ct);
            return;
        }

        using var image = Image.Load(pngBytes);
        switch (extension)
        {
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
                await destination.WriteAsync(pngBytes, ct);
                break;
        }
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

    private static async Task<IBrowser> LaunchBrowserAsync(CancellationToken ct)
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
