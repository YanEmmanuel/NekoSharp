using NekoSharp.Core.Models;

namespace NekoSharp.Core.Interfaces;

public interface IMangaLibraryService
{
    Task<IReadOnlyList<LibraryMangaEntry>> GetLibraryAsync(bool onlyFollowing = true, CancellationToken ct = default);

    Task<FollowMangaResult> FollowMangaAsync(
        string mangaUrl,
        string localPath,
        bool snapshotExisting = true,
        CancellationToken ct = default);

    Task UnfollowMangaAsync(long libraryMangaId, CancellationToken ct = default);

    Task<LibraryUpdateSummary> CheckUpdatesAsync(long? libraryMangaId = null, CancellationToken ct = default);

    Task<LibraryDownloadSummary> DownloadNewChaptersAsync(
        long? libraryMangaId = null,
        DownloadFormat format = DownloadFormat.FolderImages,
        CancellationToken ct = default);
}
