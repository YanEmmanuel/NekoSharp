namespace NekoSharp.Core.Models;

public sealed class LibraryMangaEntry
{
    public long Id { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
    public string MangaIdOrUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string CoverUrl { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public bool IsFollowing { get; init; }
    public DateTime? LastCheckedAtUtc { get; init; }
    public string? LastDownloadedChapterKey { get; init; }
    public double? LastDownloadedChapterNumber { get; init; }
    public int NewChaptersCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LibraryChapterCandidate
{
    public long LibraryMangaId { get; init; }
    public string ChapterKey { get; init; } = string.Empty;
    public Chapter Chapter { get; init; } = new();
}

public sealed class FollowMangaResult
{
    public LibraryMangaEntry Entry { get; init; } = new();
    public bool IsNewlyFollowed { get; init; }
    public int SnapshotKnownChaptersCount { get; init; }
}

public sealed class LibraryMangaUpdateResult
{
    public LibraryMangaEntry Entry { get; init; } = new();
    public IReadOnlyList<LibraryChapterCandidate> NewChapters { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public int NewChaptersCount => NewChapters.Count;
}

public sealed class LibraryUpdateSummary
{
    public DateTimeOffset CheckedAtUtc { get; init; }
    public IReadOnlyList<LibraryMangaUpdateResult> MangaResults { get; init; } = [];
    public int TotalMangaChecked => MangaResults.Count;
    public int TotalNewChapters => MangaResults.Sum(x => x.NewChaptersCount);
}

public sealed class LibraryChapterDownloadResult
{
    public long LibraryMangaId { get; init; }
    public string ChapterKey { get; init; } = string.Empty;
    public string ChapterTitle { get; init; } = string.Empty;
    public double ChapterNumber { get; init; }
    public bool Succeeded { get; init; }
    public string? FilePath { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class LibraryMangaDownloadResult
{
    public LibraryMangaEntry Entry { get; init; } = new();
    public IReadOnlyList<LibraryChapterDownloadResult> Chapters { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public int AttemptedChapters => Chapters.Count;
    public int DownloadedChapters => Chapters.Count(x => x.Succeeded);
    public int FailedChapters => Chapters.Count(x => !x.Succeeded);
}

public sealed class LibraryDownloadSummary
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public IReadOnlyList<LibraryMangaDownloadResult> MangaResults { get; init; } = [];
    public int TotalAttempted => MangaResults.Sum(x => x.AttemptedChapters);
    public int TotalDownloaded => MangaResults.Sum(x => x.DownloadedChapters);
    public int TotalFailed => MangaResults.Sum(x => x.FailedChapters);
}
