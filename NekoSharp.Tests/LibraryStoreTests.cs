using Microsoft.Data.Sqlite;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class LibraryStoreTests
{
    [Fact]
    public async Task LibraryStore_InitializesSchema_AndEnforcesUniqueChapterKeys()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using var store = new LibraryStore(dbPath);
            await store.InitializeAsync();

            var now = DateTime.UtcNow;
            var libraryId = await store.InsertLibraryMangaAsync(
                providerKey: "Exyaoi",
                mangaIdOrUrl: "https://3xyaoi.com/manga/teste",
                title: "Teste",
                coverUrl: string.Empty,
                localPath: Path.GetTempPath(),
                lastDownloadedChapterKey: null,
                lastDownloadedChapterNumber: null,
                nowUtc: now);

            await store.UpsertKnownChaptersAsync(
                libraryId,
                [new KnownChapterData("https://3xyaoi.com/cap-1", 1, "Capítulo 1")],
                firstSeenAtUtc: now);

            await store.UpsertKnownChaptersAsync(
                libraryId,
                [new KnownChapterData("https://3xyaoi.com/cap-1", 1, "Capítulo 1")],
                firstSeenAtUtc: now.AddMinutes(1));

            await store.UpsertDownloadedChapterAsync(new DownloadedChapterData(
                LibraryMangaId: libraryId,
                ChapterKey: "https://3xyaoi.com/cap-1",
                ChapterNumber: 1,
                ChapterTitle: "Capítulo 1",
                DownloadedAtUtc: now,
                FilePath: null,
                PageCount: 0,
                Status: "failed",
                ErrorMessage: "network"));

            await store.UpsertDownloadedChapterAsync(new DownloadedChapterData(
                LibraryMangaId: libraryId,
                ChapterKey: "https://3xyaoi.com/cap-1",
                ChapterNumber: 1,
                ChapterTitle: "Capítulo 1",
                DownloadedAtUtc: now.AddMinutes(1),
                FilePath: "/tmp/cap-1",
                PageCount: 10,
                Status: "completed",
                ErrorMessage: null));

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            await using var knownCountCmd = conn.CreateCommand();
            knownCountCmd.CommandText = "SELECT COUNT(*) FROM library_known_chapters WHERE library_manga_id = @id";
            knownCountCmd.Parameters.AddWithValue("@id", libraryId);
            var knownCount = Convert.ToInt32(await knownCountCmd.ExecuteScalarAsync());

            await using var downloadCountCmd = conn.CreateCommand();
            downloadCountCmd.CommandText = "SELECT COUNT(*) FROM downloaded_chapters WHERE library_manga_id = @id";
            downloadCountCmd.Parameters.AddWithValue("@id", libraryId);
            var downloadCount = Convert.ToInt32(await downloadCountCmd.ExecuteScalarAsync());

            await using var statusCmd = conn.CreateCommand();
            statusCmd.CommandText = "SELECT status FROM downloaded_chapters WHERE library_manga_id = @id AND chapter_key = @chapter_key";
            statusCmd.Parameters.AddWithValue("@id", libraryId);
            statusCmd.Parameters.AddWithValue("@chapter_key", "https://3xyaoi.com/cap-1");
            var status = (string?)await statusCmd.ExecuteScalarAsync();

            Assert.Equal(1, knownCount);
            Assert.Equal(1, downloadCount);
            Assert.Equal("completed", status);
        }
        finally
        {
            CleanupTempPath(dbPath);
        }
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "library.db");
    }

    private static void CleanupTempPath(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
