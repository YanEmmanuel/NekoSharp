using NekoSharp.Core.Helpers;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed class MangaLibraryService : IMangaLibraryService
{
    private readonly ScraperManager _scraperManager;
    private readonly IDownloadService _downloadService;
    private readonly LibraryStore _libraryStore;
    private readonly LogService? _log;

    public MangaLibraryService(
        ScraperManager scraperManager,
        IDownloadService downloadService,
        LibraryStore libraryStore,
        LogService? logService = null)
    {
        _scraperManager = scraperManager;
        _downloadService = downloadService;
        _libraryStore = libraryStore;
        _log = logService;
    }

    public async Task<IReadOnlyList<LibraryMangaEntry>> GetLibraryAsync(bool onlyFollowing = true, CancellationToken ct = default)
    {
        await _libraryStore.InitializeAsync(ct);
        return await _libraryStore.ListLibraryMangaAsync(onlyFollowing, ct);
    }

    public async Task<FollowMangaResult> FollowMangaAsync(
        string mangaUrl,
        string localPath,
        bool snapshotExisting = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mangaUrl))
            throw new ArgumentException("A URL do mangá é obrigatória.", nameof(mangaUrl));

        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("A pasta local é obrigatória.", nameof(localPath));

        await _libraryStore.InitializeAsync(ct);

        var scraper = _scraperManager.GetScraperForUrl(mangaUrl)
                      ?? throw new InvalidOperationException($"Nenhum provider encontrado para a URL: {mangaUrl}");

        var manga = await scraper.GetMangaInfoAsync(mangaUrl, ct);
        var chapters = await scraper.GetChaptersAsync(mangaUrl, ct);

        var providerKey = scraper.Name;
        var canonicalMangaUrl = !string.IsNullOrWhiteSpace(manga.Url) ? manga.Url : mangaUrl;
        var mangaIdentity = ChapterKeyHelper.BuildMangaIdentityKey(providerKey, canonicalMangaUrl);
        var nowUtc = DateTime.UtcNow;

        var (checkpointKey, checkpointNumber) = GetHighestChapterCheckpoint(providerKey, chapters);

        var existing = await _libraryStore.TryGetLibraryMangaByIdentityAsync(providerKey, mangaIdentity, ct);
        long libraryMangaId;
        if (existing is null)
        {
            libraryMangaId = await _libraryStore.InsertLibraryMangaAsync(
                providerKey,
                mangaIdentity,
                ResolveMangaTitle(manga, canonicalMangaUrl),
                manga.CoverUrl,
                localPath,
                checkpointKey,
                checkpointNumber,
                nowUtc,
                ct);
        }
        else
        {
            libraryMangaId = existing.Id;
            await _libraryStore.UpdateLibraryMangaOnFollowAsync(
                libraryMangaId,
                ResolveMangaTitle(manga, canonicalMangaUrl),
                manga.CoverUrl,
                localPath,
                checkpointKey,
                checkpointNumber,
                nowUtc,
                ct);
        }

        var snapshotCount = 0;
        if (snapshotExisting)
        {
            var known = BuildKnownChapterData(providerKey, chapters);
            snapshotCount = await _libraryStore.UpsertKnownChaptersAsync(libraryMangaId, known, nowUtc, ct);
        }

        var entry = await _libraryStore.TryGetLibraryMangaByIdAsync(libraryMangaId, ct)
                    ?? throw new InvalidOperationException("Falha ao recuperar o item da biblioteca após seguir o mangá.");

        _log?.Info($"[Library] Following manga '{entry.Title}' ({entry.ProviderKey})");

        return new FollowMangaResult
        {
            Entry = entry,
            IsNewlyFollowed = existing is null,
            SnapshotKnownChaptersCount = snapshotCount
        };
    }

    public async Task UnfollowMangaAsync(long libraryMangaId, CancellationToken ct = default)
    {
        await _libraryStore.InitializeAsync(ct);
        await _libraryStore.MarkUnfollowedAsync(libraryMangaId, DateTime.UtcNow, ct);
    }

    public async Task<LibraryUpdateSummary> CheckUpdatesAsync(long? libraryMangaId = null, CancellationToken ct = default)
    {
        await _libraryStore.InitializeAsync(ct);

        var checkedAtUtc = DateTimeOffset.UtcNow;
        var allFollowed = await _libraryStore.ListLibraryMangaAsync(onlyFollowing: true, ct);
        var targets = FilterTargets(allFollowed, libraryMangaId);

        var results = new List<LibraryMangaUpdateResult>(targets.Count);

        foreach (var entry in targets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var scraper = ResolveScraper(entry);
                if (scraper is null)
                {
                    var msg = $"Provider '{entry.ProviderKey}' não está disponível para {entry.Title}.";
                    await _libraryStore.SetCheckStateAsync(entry.Id, checkedAtUtc.UtcDateTime, 0, ct);
                    results.Add(new LibraryMangaUpdateResult
                    {
                        Entry = await ReloadEntryAsync(entry.Id, entry, ct),
                        ErrorMessage = msg
                    });
                    _log?.Warn($"[Library] {msg}");
                    continue;
                }

                var chapters = await scraper.GetChaptersAsync(entry.MangaIdOrUrl, ct);
                var knownKeys = await _libraryStore.GetKnownChapterKeysAsync(entry.Id, ct);
                var candidates = BuildNewChapterCandidates(entry, chapters, knownKeys);

                await _libraryStore.SetCheckStateAsync(entry.Id, checkedAtUtc.UtcDateTime, candidates.Count, ct);

                results.Add(new LibraryMangaUpdateResult
                {
                    Entry = await ReloadEntryAsync(entry.Id, entry, ct),
                    NewChapters = candidates
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"Falha ao verificar updates de '{entry.Title}': {ex.Message}";
                _log?.Warn($"[Library] {msg}");

                await _libraryStore.SetCheckStateAsync(entry.Id, checkedAtUtc.UtcDateTime, 0, ct);

                results.Add(new LibraryMangaUpdateResult
                {
                    Entry = await ReloadEntryAsync(entry.Id, entry, ct),
                    ErrorMessage = msg
                });
            }
        }

        return new LibraryUpdateSummary
        {
            CheckedAtUtc = checkedAtUtc,
            MangaResults = results
        };
    }

    public async Task<LibraryDownloadSummary> DownloadNewChaptersAsync(
        long? libraryMangaId = null,
        DownloadFormat format = DownloadFormat.FolderImages,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var updateSummary = await CheckUpdatesAsync(libraryMangaId, ct);

        var mangaResults = new List<LibraryMangaDownloadResult>(updateSummary.MangaResults.Count);

        foreach (var update in updateSummary.MangaResults)
        {
            ct.ThrowIfCancellationRequested();

            var entry = update.Entry;
            if (!string.IsNullOrWhiteSpace(update.ErrorMessage))
            {
                mangaResults.Add(new LibraryMangaDownloadResult
                {
                    Entry = entry,
                    ErrorMessage = update.ErrorMessage
                });
                continue;
            }

            if (update.NewChapters.Count == 0)
            {
                mangaResults.Add(new LibraryMangaDownloadResult
                {
                    Entry = entry
                });
                continue;
            }

            try
            {
                var scraper = ResolveScraper(entry);
                if (scraper is null)
                {
                    mangaResults.Add(new LibraryMangaDownloadResult
                    {
                        Entry = entry,
                        ErrorMessage = $"Provider '{entry.ProviderKey}' não está disponível."
                    });
                    continue;
                }

                var mangaInfo = await scraper.GetMangaInfoAsync(entry.MangaIdOrUrl, ct);
                NormalizeMangaInfo(mangaInfo, entry);

                Directory.CreateDirectory(entry.LocalPath);

                var chapterResults = new List<LibraryChapterDownloadResult>(update.NewChapters.Count);

                foreach (var candidate in update.NewChapters)
                {
                    ct.ThrowIfCancellationRequested();
                    var chapter = candidate.Chapter;
                    var downloadedAt = DateTime.UtcNow;

                    try
                    {
                        await _downloadService.DownloadChapterAsync(
                            mangaInfo,
                            chapter,
                            entry.LocalPath,
                            format,
                            progress: null,
                            ct);

                        var filePath = ResolveChapterOutputPath(entry.LocalPath, mangaInfo, chapter, format);

                        await _libraryStore.UpsertDownloadedChapterAsync(new DownloadedChapterData(
                            entry.Id,
                            candidate.ChapterKey,
                            chapter.Number,
                            chapter.Title,
                            downloadedAt,
                            filePath,
                            chapter.Pages.Count,
                            Status: "completed",
                            ErrorMessage: null), ct);

                        await _libraryStore.UpsertKnownChaptersAsync(
                            entry.Id,
                            [new KnownChapterData(candidate.ChapterKey, chapter.Number, chapter.Title)],
                            downloadedAt,
                            ct);

                        chapterResults.Add(new LibraryChapterDownloadResult
                        {
                            LibraryMangaId = entry.Id,
                            ChapterKey = candidate.ChapterKey,
                            ChapterTitle = chapter.Title,
                            ChapterNumber = chapter.Number,
                            Succeeded = true,
                            FilePath = filePath
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await _libraryStore.UpsertDownloadedChapterAsync(new DownloadedChapterData(
                            entry.Id,
                            candidate.ChapterKey,
                            chapter.Number,
                            chapter.Title,
                            downloadedAt,
                            FilePath: null,
                            PageCount: chapter.Pages.Count,
                            Status: "failed",
                            ErrorMessage: ex.Message), ct);

                        chapterResults.Add(new LibraryChapterDownloadResult
                        {
                            LibraryMangaId = entry.Id,
                            ChapterKey = candidate.ChapterKey,
                            ChapterTitle = chapter.Title,
                            ChapterNumber = chapter.Number,
                            Succeeded = false,
                            ErrorMessage = ex.Message
                        });

                        _log?.Warn($"[Library] Falha ao baixar '{entry.Title}' capítulo '{chapter.Title}': {ex.Message}");
                    }
                }

                var latestDownload = await _libraryStore.GetLatestCompletedDownloadAsync(entry.Id, ct);
                if (latestDownload.HasValue)
                {
                    await _libraryStore.SetCheckpointAsync(
                        entry.Id,
                        latestDownload.Value.ChapterKey,
                        latestDownload.Value.ChapterNumber,
                        DateTime.UtcNow,
                        ct);
                }

                var remainingCount = chapterResults.Count(x => !x.Succeeded);
                await _libraryStore.SetCheckStateAsync(entry.Id, DateTime.UtcNow, remainingCount, ct);

                mangaResults.Add(new LibraryMangaDownloadResult
                {
                    Entry = await ReloadEntryAsync(entry.Id, entry, ct),
                    Chapters = chapterResults
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mangaResults.Add(new LibraryMangaDownloadResult
                {
                    Entry = await ReloadEntryAsync(entry.Id, entry, ct),
                    ErrorMessage = ex.Message
                });
            }
        }

        return new LibraryDownloadSummary
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MangaResults = mangaResults
        };
    }

    private IScraper? ResolveScraper(LibraryMangaEntry entry)
    {
        return _scraperManager.GetScraperByName(entry.ProviderKey)
               ?? _scraperManager.GetScraperForUrl(entry.MangaIdOrUrl);
    }

    private static List<LibraryMangaEntry> FilterTargets(IReadOnlyList<LibraryMangaEntry> allFollowed, long? libraryMangaId)
    {
        if (!libraryMangaId.HasValue)
            return allFollowed.ToList();

        return allFollowed
            .Where(x => x.Id == libraryMangaId.Value)
            .ToList();
    }

    private static IReadOnlyList<KnownChapterData> BuildKnownChapterData(string providerKey, IReadOnlyList<Chapter> chapters)
    {
        var byKey = new Dictionary<string, KnownChapterData>(StringComparer.Ordinal);

        foreach (var chapter in chapters)
        {
            var key = ChapterKeyHelper.BuildChapterKey(providerKey, chapter);
            if (string.IsNullOrWhiteSpace(key) || byKey.ContainsKey(key))
                continue;

            byKey[key] = new KnownChapterData(key, chapter.Number, chapter.Title);
        }

        return byKey.Values.ToList();
    }

    private static IReadOnlyList<LibraryChapterCandidate> BuildNewChapterCandidates(
        LibraryMangaEntry entry,
        IReadOnlyList<Chapter> chapters,
        HashSet<string> knownKeys)
    {
        var newCandidates = new List<LibraryChapterCandidate>();
        var seenNew = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chapter in chapters)
        {
            var chapterKey = ChapterKeyHelper.BuildChapterKey(entry.ProviderKey, chapter);
            if (string.IsNullOrWhiteSpace(chapterKey))
                continue;

            if (knownKeys.Contains(chapterKey))
                continue;

            if (!seenNew.Add(chapterKey))
                continue;

            newCandidates.Add(new LibraryChapterCandidate
            {
                LibraryMangaId = entry.Id,
                ChapterKey = chapterKey,
                Chapter = chapter
            });
        }

        return newCandidates;
    }

    private static (string? ChapterKey, double? ChapterNumber) GetHighestChapterCheckpoint(
        string providerKey,
        IReadOnlyList<Chapter> chapters)
    {
        if (chapters.Count == 0)
            return (null, null);

        var highest = chapters
            .OrderByDescending(x => x.Number)
            .ThenByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .First();

        var key = ChapterKeyHelper.BuildChapterKey(providerKey, highest);
        return (key, highest.Number);
    }

    private static string ResolveMangaTitle(Manga manga, string fallbackUrl)
    {
        if (!string.IsNullOrWhiteSpace(manga.Name))
            return manga.Name;

        return fallbackUrl;
    }

    private static void NormalizeMangaInfo(Manga manga, LibraryMangaEntry entry)
    {
        if (string.IsNullOrWhiteSpace(manga.Url))
            manga.Url = entry.MangaIdOrUrl;

        if (string.IsNullOrWhiteSpace(manga.Name))
            manga.Name = entry.Title;

        if (string.IsNullOrWhiteSpace(manga.SiteName))
            manga.SiteName = entry.ProviderKey;
    }

    private static string ResolveChapterOutputPath(string outputDirectory, Manga manga, Chapter chapter, DownloadFormat format)
    {
        return format == DownloadFormat.Cbz
            ? DownloadPaths.GetChapterArchivePath(outputDirectory, manga, chapter, ".cbz")
            : DownloadPaths.GetChapterDirectory(outputDirectory, manga, chapter);
    }

    private async Task<LibraryMangaEntry> ReloadEntryAsync(long libraryMangaId, LibraryMangaEntry fallback, CancellationToken ct)
    {
        return await _libraryStore.TryGetLibraryMangaByIdAsync(libraryMangaId, ct) ?? fallback;
    }
}
