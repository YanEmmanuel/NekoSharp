using System.Collections.Concurrent;
using System.Net;
using System.Text;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Providers.Comix;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class DownloadServiceTests
{
    [Fact]
    public async Task DownloadChapterAsync_UsesGlobalPageConcurrencyAcrossConcurrentChapters()
    {
        var handler = new TrackingHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(120, cancellationToken);
            return CreateImageResponse();
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var service = CreateService(httpClient);
        service.MaxConcurrentDownloads = 2;

        var manga = CreateManga();
        var chapter1 = CreateChapter(1, 4);
        var chapter2 = CreateChapter(2, 4);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await Task.WhenAll(
                service.DownloadChapterAsync(manga, chapter1, outputDirectory, DownloadFormat.FolderImages),
                service.DownloadChapterAsync(manga, chapter2, outputDirectory, DownloadFormat.FolderImages));

            Assert.True(handler.MaxObservedConcurrency <= 2,
                $"Concorrência máxima observada: {handler.MaxObservedConcurrency}");
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_WhenRequestTimesOut_RetriesAndSucceeds()
    {
        var handler = new TrackingHttpMessageHandler(async (request, attempt, cancellationToken) =>
        {
            if (attempt == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CreateImageResponse();
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var service = CreateService(
            httpClient,
            attemptTimeouts: [TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(200)],
            retryDelays: [TimeSpan.FromMilliseconds(15)]);

        var manga = CreateManga();
        var chapter = CreateChapter(1, 1);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(manga, chapter, outputDirectory, DownloadFormat.FolderImages);

            Assert.Equal(2, handler.GetAttempts(chapter.Pages[0].ImageUrl));
            Assert.True(File.Exists(chapter.Pages[0].LocalPath));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_WhenTransientFailuresExceedRetrySchedule_KeepsRetrying()
    {
        var handler = new TrackingHttpMessageHandler((_, attempt, _) =>
        {
            if (attempt <= 4)
            {
                throw new HttpRequestException(
                    "servidor temporariamente indisponível",
                    null,
                    HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult(CreateImageResponse());
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var service = CreateService(
            httpClient,
            attemptTimeouts: [TimeSpan.FromMilliseconds(200)],
            retryDelays: [TimeSpan.FromMilliseconds(10)]);

        var manga = CreateManga();
        var chapter = CreateChapter(1, 1);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(manga, chapter, outputDirectory, DownloadFormat.FolderImages);

            Assert.Equal(5, handler.GetAttempts(chapter.Pages[0].ImageUrl));
            Assert.True(File.Exists(chapter.Pages[0].LocalPath));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_WhenPageDiscoveryTimesOut_RetriesProviderOperation()
    {
        var handler = new TrackingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateImageResponse()));

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var scraper = new RetryingPagesScraper();
        var service = CreateService(
            httpClient,
            retryDelays: [TimeSpan.FromMilliseconds(10)],
            scraper: scraper);
        var manga = CreateManga();
        var chapter = new Chapter
        {
            Number = 1,
            Title = "Capítulo 1",
            Url = "https://manga.example/series/teste/1"
        };
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(manga, chapter, outputDirectory, DownloadFormat.FolderImages);

            Assert.Equal(2, scraper.PageDiscoveryAttempts);
            Assert.Single(chapter.Pages);
            Assert.True(File.Exists(chapter.Pages[0].LocalPath));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_WhenMangaDexImageHostFails_RefreshesAtHomeUrlForRetry()
    {
        const string staleImageUrl = "https://old-cdn.mangadex.network/data/oldhash/001.png";
        const string refreshedImageUrl = "https://new-cdn.mangadex.network/data/newhash/001.png";
        const string atHomeUrl = "https://api.mangadex.org/at-home/server/chapter-id?forcePort443=true";

        var handler = new TrackingHttpMessageHandler((request, attempt, cancellationToken) =>
        {
            var url = request.RequestUri?.ToString();
            if (string.Equals(url, staleImageUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("cdn indisponível", null, HttpStatusCode.GatewayTimeout);
            }

            if (string.Equals(url, atHomeUrl, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "baseUrl": "https://new-cdn.mangadex.network",
                          "chapter": {
                            "hash": "newhash"
                          }
                        }
                        """)
                });
            }

            if (string.Equals(url, refreshedImageUrl, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(CreateImageResponse());

            throw new InvalidOperationException($"URL inesperada no teste: {url}");
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var service = CreateService(
            httpClient,
            attemptTimeouts: [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)],
            retryDelays: [TimeSpan.FromMilliseconds(15)]);

        var manga = CreateManga();
        var chapter = new Chapter
        {
            Number = 1,
            Title = "Capítulo 1",
            Url = atHomeUrl,
            Pages =
            [
                new Page
                {
                    Number = 1,
                    ImageUrl = staleImageUrl,
                    RefererUrl = atHomeUrl
                }
            ]
        };
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(manga, chapter, outputDirectory, DownloadFormat.FolderImages);

            Assert.Equal(1, handler.GetAttempts(staleImageUrl));
            Assert.Equal(1, handler.GetAttempts(atHomeUrl));
            Assert.Equal(1, handler.GetAttempts(refreshedImageUrl));
            Assert.True(File.Exists(chapter.Pages[0].LocalPath));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_AppliesProviderAuthenticationToImageRequests()
    {
        const string expectedCookie = "wordpress_logged_in_hash=session-value";
        var authenticationCalls = 0;

        var handler = new TrackingHttpMessageHandler((request, _, _) =>
        {
            Assert.True(request.Headers.TryGetValues("Cookie", out var cookieValues));
            Assert.Contains(expectedCookie, string.Join("; ", cookieValues));
            Assert.Equal("LittleTyrantBrowser/1.0", request.Headers.UserAgent.ToString());
            return Task.FromResult(CreateImageResponse());
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var scraper = new AuthenticatedStubScraper(request =>
        {
            Interlocked.Increment(ref authenticationCalls);
            request.Headers.TryAddWithoutValidation("Cookie", expectedCookie);
            request.Headers.UserAgent.ParseAdd("LittleTyrantBrowser/1.0");
        });
        var service = CreateService(httpClient, scraper: scraper);
        var manga = CreateManga();
        var chapter = CreateChapter(1, 1);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(manga, chapter, outputDirectory, DownloadFormat.FolderImages);

            Assert.Equal(1, authenticationCalls);
            Assert.True(File.Exists(chapter.Pages[0].LocalPath));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_ComixUsesFallbackAndDecodesResponse()
    {
        const int seed = 78123;
        const int encodedLength = 24;
        var original = CreatePngBytes();
        var encoded = ComixScraper.DecodeEncodedPrefix(original, seed, encodedLength);

        var handler = new TrackingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path?.Contains("/si/", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            Assert.Contains("/i/", path);
            Assert.Equal("https://comix.to", request.Headers.GetValues("Origin").Single());

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(encoded)
            };
            response.Headers.TryAddWithoutValidation("x-enc-seed", seed.ToString());
            response.Headers.TryAddWithoutValidation("x-enc-len", encodedLength.ToString());
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var scraper = new ComixScraper();
        var service = CreateService(httpClient, scraper: scraper);
        var manga = new Manga
        {
            Name = "Comix Test",
            Url = "https://comix.to/title/abc-test",
            SiteName = "Comix"
        };
        var chapter = new Chapter
        {
            Number = 1,
            Title = "Chapter 1",
            Url = "https://comix.to/title/abc-test/123-chapter-1",
            Pages =
            [
                new Page
                {
                    Number = 1,
                    ImageUrl = "https://wowpic.example/si/chapter/001.jpg#scrambled"
                }
            ]
        };
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(
                manga,
                chapter,
                outputDirectory,
                DownloadFormat.FolderImages);

            Assert.Equal(
                1,
                handler.GetAttempts("https://wowpic.example/si/chapter/001.jpg"));
            Assert.Equal(
                1,
                handler.GetAttempts("https://wowpic.example/i/chapter/001.jpg"));
            Assert.Equal(original, await File.ReadAllBytesAsync(chapter.Pages[0].LocalPath!));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_InvalidCanvasImage_UsesRenderedFallback()
    {
        var renderedImage = CreatePngBytes();
        var handler = new TrackingHttpMessageHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("broken-image"))
            }));

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var scraper = new CanvasFallbackStubScraper(renderedImage);
        var service = CreateService(httpClient, scraper: scraper);
        var manga = CreateManga();
        var chapter = CreateChapter(7, 1);
        chapter.Pages[0].ImageUrl = "https://img.example/007/001.png#scrambled";
        chapter.Pages[0].RefererUrl = chapter.Url;
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(
                manga,
                chapter,
                outputDirectory,
                DownloadFormat.FolderImages);

            Assert.Equal(1, scraper.RenderedFallbackCalls);
            Assert.Equal(1, scraper.LastPageNumber);
            Assert.Equal(chapter.Url, scraper.LastChapterUrl);
            Assert.Equal(renderedImage, await File.ReadAllBytesAsync(chapter.Pages[0].LocalPath!));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task DownloadChapterAsync_NormalImage_DoesNotUseRenderedFallback()
    {
        var directImage = Encoding.UTF8.GetBytes("normal-direct-response");
        var handler = new TrackingHttpMessageHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(directImage)
            }));

        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var scraper = new CanvasFallbackStubScraper(CreatePngBytes());
        var service = CreateService(httpClient, scraper: scraper);
        var manga = CreateManga();
        var chapter = CreateChapter(8, 1);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await service.DownloadChapterAsync(
                manga,
                chapter,
                outputDirectory,
                DownloadFormat.FolderImages);

            Assert.Equal(0, scraper.RenderedFallbackCalls);
            Assert.Equal(directImage, await File.ReadAllBytesAsync(chapter.Pages[0].LocalPath!));
        }
        finally
        {
            CleanupTempDirectory(outputDirectory);
        }
    }

    private static DownloadService CreateService(
        HttpClient httpClient,
        TimeSpan[]? attemptTimeouts = null,
        TimeSpan[]? retryDelays = null,
        IScraper? scraper = null)
    {
        var scraperManager = new ScraperManager();
        scraperManager.Register(scraper ?? new StubScraper());

        return new DownloadService(
            scraperManager,
            httpClient: httpClient,
            attemptTimeouts: attemptTimeouts,
            retryDelays: retryDelays);
    }

    private static Manga CreateManga()
    {
        return new Manga
        {
            Name = "Teste",
            Url = "https://manga.example/series/teste",
            SiteName = "Teste"
        };
    }

    private static Chapter CreateChapter(int number, int pageCount)
    {
        return new Chapter
        {
            Number = number,
            Title = $"Capítulo {number}",
            Url = $"https://manga.example/series/teste/{number}",
            Pages = Enumerable.Range(1, pageCount)
                .Select(pageNumber => new Page
                {
                    Number = pageNumber,
                    ImageUrl = $"https://img.example/{number:D3}/{pageNumber:D3}.jpg"
                })
                .ToList()
        };
    }

    private static HttpResponseMessage CreateImageResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("image-bytes"))
        };
    }

    private static byte[] CreatePngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class StubScraper : IScraper
    {
        public string Name => "Stub";
        public string BaseUrl => "https://manga.example";

        public bool CanHandle(string url) => url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);

        public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
        {
            return Task.FromResult(CreateManga());
        }

        public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
        {
            return Task.FromResult(new List<Chapter>());
        }

        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
        {
            return Task.FromResult(chapter.Pages);
        }
    }

    private sealed class AuthenticatedStubScraper : IScraper, IAuthenticatedRequestProvider
    {
        private readonly Action<HttpRequestMessage> _applyAuthentication;

        public AuthenticatedStubScraper(Action<HttpRequestMessage> applyAuthentication)
        {
            _applyAuthentication = applyAuthentication;
        }

        public string Name => "Authenticated Stub";
        public string BaseUrl => "https://manga.example";

        public bool CanHandle(string url) => url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);

        public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult(CreateManga());

        public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new List<Chapter>());

        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
            => Task.FromResult(chapter.Pages);

        public Task ApplyRequestAuthenticationAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            _applyAuthentication(request);
            return Task.CompletedTask;
        }
    }

    private sealed class CanvasFallbackStubScraper :
        IScraper,
        ICustomPageDownloadProvider,
        IRenderedPageFallbackProvider
    {
        private readonly byte[] _renderedImage;

        public CanvasFallbackStubScraper(byte[] renderedImage)
        {
            _renderedImage = renderedImage;
        }

        public int RenderedFallbackCalls { get; private set; }
        public int LastPageNumber { get; private set; }
        public string? LastChapterUrl { get; private set; }
        public string Name => "Canvas Fallback Stub";
        public string BaseUrl => "https://manga.example";

        public bool CanHandle(string url) => url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);

        public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult(CreateManga());

        public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new List<Chapter>());

        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
            => Task.FromResult(chapter.Pages);

        public IReadOnlyList<string> GetPageDownloadCandidates(string imageUrl) => [imageUrl];

        public void ApplyPageDownloadHeaders(HttpRequestMessage request, string imageUrl)
        {
        }

        public async Task CopyPageResponseAsync(
            HttpResponseMessage response,
            Stream destination,
            string imageUrl,
            CancellationToken ct = default)
        {
            await response.Content.CopyToAsync(destination, ct);
        }

        public bool ShouldUseRenderedPageFallback(string imageUrl)
            => imageUrl.Contains("#scrambled", StringComparison.Ordinal);

        public async Task<bool> TryWriteRenderedPageAsync(
            string chapterUrl,
            int pageNumber,
            string imageUrl,
            Stream destination,
            CancellationToken ct = default)
        {
            RenderedFallbackCalls++;
            LastPageNumber = pageNumber;
            LastChapterUrl = chapterUrl;
            await destination.WriteAsync(_renderedImage, ct);
            return true;
        }
    }

    private sealed class RetryingPagesScraper : IScraper
    {
        private int _pageDiscoveryAttempts;

        public int PageDiscoveryAttempts => Volatile.Read(ref _pageDiscoveryAttempts);
        public string Name => "Retrying Pages";
        public string BaseUrl => "https://manga.example";

        public bool CanHandle(string url) => url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);

        public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult(CreateManga());

        public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new List<Chapter>());

        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _pageDiscoveryAttempts) == 1)
                throw new TimeoutException("provider demorou para responder");

            return Task.FromResult(new List<Page>
            {
                new()
                {
                    Number = 1,
                    ImageUrl = "https://img.example/001/001.jpg",
                    RefererUrl = chapter.Url
                }
            });
        }
    }

    private sealed class TrackingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _responder;
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private int _inFlight;
        private int _maxObservedConcurrency;

        public TrackingHttpMessageHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public int GetAttempts(string url)
        {
            return _attempts.TryGetValue(url, out var count) ? count : 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var attempt = _attempts.AddOrUpdate(url, 1, static (_, current) => current + 1);
            var inFlight = Interlocked.Increment(ref _inFlight);
            UpdateMaxConcurrency(inFlight);

            try
            {
                return await _responder(request, attempt, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMaxConcurrency(int observed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (observed <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, observed, current) == current)
                    return;
            }
        }
    }
}
