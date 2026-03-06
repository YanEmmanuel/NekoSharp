using System.Collections.Concurrent;
using System.Net;
using System.Text;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
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

    private static DownloadService CreateService(
        HttpClient httpClient,
        TimeSpan[]? attemptTimeouts = null,
        TimeSpan[]? retryDelays = null)
    {
        var scraperManager = new ScraperManager();
        scraperManager.Register(new StubScraper());

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
