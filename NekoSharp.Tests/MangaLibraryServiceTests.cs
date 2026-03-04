using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class MangaLibraryServiceTests
{
    [Fact]
    public async Task Follow_WithSnapshot_DetectsOnlyFutureChapters()
    {
        using var ctx = CreateContext();
        ctx.Scraper.SetChapters(
            Chapter(1, "Capítulo 1", "https://fake.example/manga/neko/cap-1"),
            Chapter(2, "Capítulo 2", "https://fake.example/manga/neko/cap-2"));

        var follow = await ctx.Service.FollowMangaAsync(ctx.MangaUrl, ctx.OutputDirectory);
        Assert.True(follow.Entry.IsFollowing);
        Assert.Equal(2, follow.SnapshotKnownChaptersCount);

        var firstCheck = await ctx.Service.CheckUpdatesAsync();
        Assert.Equal(0, firstCheck.TotalNewChapters);

        ctx.Scraper.SetChapters(
            Chapter(1, "Capítulo 1", "https://fake.example/manga/neko/cap-1"),
            Chapter(2, "Capítulo 2", "https://fake.example/manga/neko/cap-2"),
            Chapter(3, "Capítulo 3", "https://fake.example/manga/neko/cap-3"));

        var secondCheck = await ctx.Service.CheckUpdatesAsync();
        Assert.Equal(1, secondCheck.TotalNewChapters);

        var download = await ctx.Service.DownloadNewChaptersAsync(format: DownloadFormat.FolderImages);
        Assert.Equal(1, download.TotalAttempted);
        Assert.Equal(1, download.TotalDownloaded);
        Assert.Equal(0, download.TotalFailed);
        Assert.Single(ctx.DownloadService.DownloadedChapterUrls);

        var afterDownloadCheck = await ctx.Service.CheckUpdatesAsync();
        Assert.Equal(0, afterDownloadCheck.TotalNewChapters);
    }

    [Fact]
    public async Task DownloadNewChapters_WhenFailure_KeepsChapterAsPending()
    {
        using var ctx = CreateContext();
        ctx.Scraper.SetChapters(Chapter(1, "Capítulo 1", "https://fake.example/manga/neko/cap-1"));

        var follow = await ctx.Service.FollowMangaAsync(ctx.MangaUrl, ctx.OutputDirectory);

        var failingChapterUrl = "https://fake.example/manga/neko/cap-2";
        ctx.Scraper.SetChapters(
            Chapter(1, "Capítulo 1", "https://fake.example/manga/neko/cap-1"),
            Chapter(2, "Capítulo 2", failingChapterUrl));

        ctx.DownloadService.FailChapterUrls.Add(failingChapterUrl);

        var download = await ctx.Service.DownloadNewChaptersAsync(follow.Entry.Id, DownloadFormat.FolderImages);
        Assert.Equal(1, download.TotalAttempted);
        Assert.Equal(0, download.TotalDownloaded);
        Assert.Equal(1, download.TotalFailed);

        var check = await ctx.Service.CheckUpdatesAsync(follow.Entry.Id);
        Assert.Equal(1, check.TotalNewChapters);
    }

    [Fact]
    public async Task Unfollow_RemovesFromFollowingList_ButKeepsHistory()
    {
        using var ctx = CreateContext();
        ctx.Scraper.SetChapters(Chapter(1, "Capítulo 1", "https://fake.example/manga/neko/cap-1"));

        var follow = await ctx.Service.FollowMangaAsync(ctx.MangaUrl, ctx.OutputDirectory);
        await ctx.Service.UnfollowMangaAsync(follow.Entry.Id);

        var followed = await ctx.Service.GetLibraryAsync(onlyFollowing: true);
        var all = await ctx.Service.GetLibraryAsync(onlyFollowing: false);

        Assert.Empty(followed);
        Assert.Single(all);
        Assert.False(all[0].IsFollowing);
    }

    private static Chapter Chapter(double number, string title, string url)
    {
        return new Chapter
        {
            Number = number,
            Title = title,
            Url = url
        };
    }

    private static TestContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var dbPath = Path.Combine(root, "library.db");
        var outputDirectory = Path.Combine(root, "downloads");
        Directory.CreateDirectory(outputDirectory);

        var scraper = new FakeScraper(
            providerName: "FakeProvider",
            baseUrl: "https://fake.example",
            mangaUrl: "https://fake.example/manga/neko",
            mangaTitle: "Neko Fake");

        var manager = new ScraperManager();
        manager.Register(scraper);

        var downloadService = new FakeDownloadService();
        var store = new LibraryStore(dbPath);
        var service = new MangaLibraryService(manager, downloadService, store);

        return new TestContext(root, outputDirectory, service, scraper, downloadService, store);
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext(
            string root,
            string outputDirectory,
            MangaLibraryService service,
            FakeScraper scraper,
            FakeDownloadService downloadService,
            LibraryStore store)
        {
            Root = root;
            OutputDirectory = outputDirectory;
            Service = service;
            Scraper = scraper;
            DownloadService = downloadService;
            Store = store;
        }

        public string Root { get; }
        public string OutputDirectory { get; }
        public string MangaUrl => "https://fake.example/manga/neko";
        public MangaLibraryService Service { get; }
        public FakeScraper Scraper { get; }
        public FakeDownloadService DownloadService { get; }
        public LibraryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();

            if (!Directory.Exists(Root))
                return;

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeScraper : IScraper
    {
        private readonly object _gate = new();
        private readonly string _mangaUrl;
        private readonly string _mangaTitle;
        private List<Chapter> _chapters = [];

        public FakeScraper(string providerName, string baseUrl, string mangaUrl, string mangaTitle)
        {
            Name = providerName;
            BaseUrl = baseUrl;
            _mangaUrl = mangaUrl;
            _mangaTitle = mangaTitle;
        }

        public string Name { get; }
        public string BaseUrl { get; }

        public bool CanHandle(string url)
            => !string.IsNullOrWhiteSpace(url) && url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);

        public void SetChapters(params Chapter[] chapters)
        {
            lock (_gate)
            {
                _chapters = chapters.Select(CloneChapter).ToList();
            }
        }

        public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)
        {
            var manga = new Manga
            {
                Name = _mangaTitle,
                Url = _mangaUrl,
                SiteName = Name,
                CoverUrl = string.Empty,
                Description = "Fake manga"
            };

            return Task.FromResult(manga);
        }

        public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_chapters.Select(CloneChapter).ToList());
            }
        }

        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
        {
            return Task.FromResult(new List<Page>
            {
                new()
                {
                    Number = 1,
                    ImageUrl = "https://fake.example/img/001.jpg"
                }
            });
        }

        private static Chapter CloneChapter(Chapter chapter)
        {
            return new Chapter
            {
                Number = chapter.Number,
                Title = chapter.Title,
                Url = chapter.Url,
                Pages = chapter.Pages.Select(p => new Page
                {
                    Number = p.Number,
                    ImageUrl = p.ImageUrl,
                    LocalPath = p.LocalPath
                }).ToList()
            };
        }
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public HashSet<string> FailChapterUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DownloadedChapterUrls { get; } = [];

        public int MaxConcurrentDownloads { get; set; } = 4;

        public Task DownloadChapterAsync(
            Manga manga,
            Chapter chapter,
            string outputDirectory,
            DownloadFormat format,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (FailChapterUrls.Contains(chapter.Url))
                throw new InvalidOperationException("forced-download-failure");

            DownloadedChapterUrls.Add(chapter.Url);
            chapter.Pages =
            [
                new Page
                {
                    Number = 1,
                    ImageUrl = "https://fake.example/img/001.jpg",
                    LocalPath = Path.Combine(outputDirectory, "001.jpg")
                }
            ];

            return Task.CompletedTask;
        }

        public async Task DownloadChaptersAsync(
            Manga manga,
            IEnumerable<Chapter> chapters,
            string outputDirectory,
            DownloadFormat format,
            IProgress<(Chapter chapter, DownloadProgress progress)>? progress = null,
            CancellationToken ct = default)
        {
            foreach (var chapter in chapters)
            {
                await DownloadChapterAsync(manga, chapter, outputDirectory, format, null, ct);
            }
        }
    }
}
