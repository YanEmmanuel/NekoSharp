using System.Globalization;
using Microsoft.Data.Sqlite;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed record KnownChapterData(string ChapterKey, double? ChapterNumber, string? ChapterTitle);

public sealed record DownloadedChapterData(
    long LibraryMangaId,
    string ChapterKey,
    double? ChapterNumber,
    string ChapterTitle,
    DateTime DownloadedAtUtc,
    string? FilePath,
    int PageCount,
    string Status,
    string? ErrorMessage);

public sealed class LibraryStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LogService? _log;
    private SqliteConnection? _connection;
    private bool _initialized;

    public LibraryStore(string? dbPath = null, LogService? logService = null)
    {
        _log = logService;

        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            _dbPath = dbPath;
        }
        else
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NekoSharp");
            Directory.CreateDirectory(configDir);
            _dbPath = Path.Combine(configDir, "nekosharp.db");
        }
    }

    public string DatabasePath => _dbPath;

    public Task InitializeAsync(CancellationToken ct = default) => EnsureInitializedAsync(ct);

    public async Task<IReadOnlyList<LibraryMangaEntry>> ListLibraryMangaAsync(bool onlyFollowing, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = onlyFollowing
                ? """
                  SELECT id, provider_key, manga_id_or_url, title, cover_url, local_path, is_following,
                         last_checked_at_utc, last_downloaded_chapter_key, last_downloaded_chapter_number,
                         new_chapters_count, created_at_utc, updated_at_utc
                  FROM library_manga
                  WHERE is_following = 1
                  ORDER BY updated_at_utc DESC
                  """
                : """
                  SELECT id, provider_key, manga_id_or_url, title, cover_url, local_path, is_following,
                         last_checked_at_utc, last_downloaded_chapter_key, last_downloaded_chapter_number,
                         new_chapters_count, created_at_utc, updated_at_utc
                  FROM library_manga
                  ORDER BY updated_at_utc DESC
                  """;

            var list = new List<LibraryMangaEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(MapLibraryManga(reader));

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LibraryMangaEntry?> TryGetLibraryMangaByIdAsync(long libraryMangaId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT id, provider_key, manga_id_or_url, title, cover_url, local_path, is_following,
                       last_checked_at_utc, last_downloaded_chapter_key, last_downloaded_chapter_number,
                       new_chapters_count, created_at_utc, updated_at_utc
                FROM library_manga
                WHERE id = @id
            """;
            cmd.Parameters.AddWithValue("@id", libraryMangaId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return MapLibraryManga(reader);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LibraryMangaEntry?> TryGetLibraryMangaByIdentityAsync(
        string providerKey,
        string mangaIdOrUrl,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT id, provider_key, manga_id_or_url, title, cover_url, local_path, is_following,
                       last_checked_at_utc, last_downloaded_chapter_key, last_downloaded_chapter_number,
                       new_chapters_count, created_at_utc, updated_at_utc
                FROM library_manga
                WHERE provider_key = @provider_key AND manga_id_or_url = @manga_id_or_url
            """;
            cmd.Parameters.AddWithValue("@provider_key", providerKey);
            cmd.Parameters.AddWithValue("@manga_id_or_url", mangaIdOrUrl);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return MapLibraryManga(reader);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> InsertLibraryMangaAsync(
        string providerKey,
        string mangaIdOrUrl,
        string title,
        string coverUrl,
        string localPath,
        string? lastDownloadedChapterKey,
        double? lastDownloadedChapterNumber,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO library_manga
                (provider_key, manga_id_or_url, title, cover_url, local_path, is_following,
                 last_checked_at_utc, last_downloaded_chapter_key, last_downloaded_chapter_number,
                 new_chapters_count, created_at_utc, updated_at_utc)
                VALUES
                (@provider_key, @manga_id_or_url, @title, @cover_url, @local_path, 1,
                 @last_checked_at_utc, @last_downloaded_chapter_key, @last_downloaded_chapter_number,
                 0, @created_at_utc, @updated_at_utc)
            """;

            cmd.Parameters.AddWithValue("@provider_key", providerKey);
            cmd.Parameters.AddWithValue("@manga_id_or_url", mangaIdOrUrl);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@cover_url", (object?)coverUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@local_path", localPath);
            cmd.Parameters.AddWithValue("@last_checked_at_utc", nowUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_key", (object?)lastDownloadedChapterKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_number", lastDownloadedChapterNumber.HasValue
                ? lastDownloadedChapterNumber.Value
                : DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at_utc", nowUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@updated_at_utc", nowUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);

            await using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var idObj = await idCmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(idObj, CultureInfo.InvariantCulture);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateLibraryMangaOnFollowAsync(
        long libraryMangaId,
        string title,
        string coverUrl,
        string localPath,
        string? lastDownloadedChapterKey,
        double? lastDownloadedChapterNumber,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE library_manga
                SET title = @title,
                    cover_url = @cover_url,
                    local_path = @local_path,
                    is_following = 1,
                    last_checked_at_utc = @last_checked_at_utc,
                    last_downloaded_chapter_key = @last_downloaded_chapter_key,
                    last_downloaded_chapter_number = @last_downloaded_chapter_number,
                    new_chapters_count = 0,
                    updated_at_utc = @updated_at_utc
                WHERE id = @id
            """;

            cmd.Parameters.AddWithValue("@id", libraryMangaId);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@cover_url", (object?)coverUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@local_path", localPath);
            cmd.Parameters.AddWithValue("@last_checked_at_utc", nowUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_key", (object?)lastDownloadedChapterKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_number", lastDownloadedChapterNumber.HasValue
                ? lastDownloadedChapterNumber.Value
                : DBNull.Value);
            cmd.Parameters.AddWithValue("@updated_at_utc", nowUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkUnfollowedAsync(long libraryMangaId, DateTime nowUtc, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE library_manga
                SET is_following = 0,
                    new_chapters_count = 0,
                    updated_at_utc = @updated_at_utc
                WHERE id = @id
            """;
            cmd.Parameters.AddWithValue("@id", libraryMangaId);
            cmd.Parameters.AddWithValue("@updated_at_utc", nowUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetCheckStateAsync(
        long libraryMangaId,
        DateTime checkedAtUtc,
        int newChaptersCount,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE library_manga
                SET last_checked_at_utc = @last_checked_at_utc,
                    new_chapters_count = @new_chapters_count,
                    updated_at_utc = @updated_at_utc
                WHERE id = @id
            """;

            cmd.Parameters.AddWithValue("@id", libraryMangaId);
            cmd.Parameters.AddWithValue("@last_checked_at_utc", checkedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@new_chapters_count", Math.Max(0, newChaptersCount));
            cmd.Parameters.AddWithValue("@updated_at_utc", checkedAtUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetCheckpointAsync(
        long libraryMangaId,
        string? lastDownloadedChapterKey,
        double? lastDownloadedChapterNumber,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE library_manga
                SET last_downloaded_chapter_key = @last_downloaded_chapter_key,
                    last_downloaded_chapter_number = @last_downloaded_chapter_number,
                    updated_at_utc = @updated_at_utc
                WHERE id = @id
            """;

            cmd.Parameters.AddWithValue("@id", libraryMangaId);
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_key", (object?)lastDownloadedChapterKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_downloaded_chapter_number", lastDownloadedChapterNumber.HasValue
                ? lastDownloadedChapterNumber.Value
                : DBNull.Value);
            cmd.Parameters.AddWithValue("@updated_at_utc", nowUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> UpsertKnownChaptersAsync(
        long libraryMangaId,
        IEnumerable<KnownChapterData> knownChapters,
        DateTime firstSeenAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(knownChapters);

        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var inserted = 0;
            await using var tx = (SqliteTransaction)await _connection!.BeginTransactionAsync(ct);

            foreach (var chapter in knownChapters)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO library_known_chapters
                    (library_manga_id, chapter_key, chapter_number, chapter_title, first_seen_at_utc)
                    VALUES
                    (@library_manga_id, @chapter_key, @chapter_number, @chapter_title, @first_seen_at_utc)
                    ON CONFLICT(library_manga_id, chapter_key) DO NOTHING
                """;

                cmd.Parameters.AddWithValue("@library_manga_id", libraryMangaId);
                cmd.Parameters.AddWithValue("@chapter_key", chapter.ChapterKey);
                cmd.Parameters.AddWithValue("@chapter_number", (object?)chapter.ChapterNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@chapter_title", (object?)chapter.ChapterTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@first_seen_at_utc", firstSeenAtUtc.ToString("O"));

                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 0)
                    inserted += affected;
            }

            await tx.CommitAsync(ct);
            return inserted;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HashSet<string>> GetKnownChapterKeysAsync(long libraryMangaId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT chapter_key
                FROM library_known_chapters
                WHERE library_manga_id = @library_manga_id
            """;
            cmd.Parameters.AddWithValue("@library_manga_id", libraryMangaId);

            var keys = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(key))
                    keys.Add(key);
            }

            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertDownloadedChapterAsync(DownloadedChapterData chapterData, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO downloaded_chapters
                (library_manga_id, chapter_key, chapter_number, chapter_title, downloaded_at_utc,
                 file_path, page_count, status, error_message)
                VALUES
                (@library_manga_id, @chapter_key, @chapter_number, @chapter_title, @downloaded_at_utc,
                 @file_path, @page_count, @status, @error_message)
                ON CONFLICT(library_manga_id, chapter_key) DO UPDATE SET
                    chapter_number = excluded.chapter_number,
                    chapter_title = excluded.chapter_title,
                    downloaded_at_utc = excluded.downloaded_at_utc,
                    file_path = excluded.file_path,
                    page_count = excluded.page_count,
                    status = excluded.status,
                    error_message = excluded.error_message
            """;

            cmd.Parameters.AddWithValue("@library_manga_id", chapterData.LibraryMangaId);
            cmd.Parameters.AddWithValue("@chapter_key", chapterData.ChapterKey);
            cmd.Parameters.AddWithValue("@chapter_number", (object?)chapterData.ChapterNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@chapter_title", chapterData.ChapterTitle);
            cmd.Parameters.AddWithValue("@downloaded_at_utc", chapterData.DownloadedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@file_path", (object?)chapterData.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@page_count", Math.Max(0, chapterData.PageCount));
            cmd.Parameters.AddWithValue("@status", chapterData.Status);
            cmd.Parameters.AddWithValue("@error_message", (object?)chapterData.ErrorMessage ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(string? ChapterKey, double? ChapterNumber)?> GetLatestCompletedDownloadAsync(
        long libraryMangaId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT chapter_key, chapter_number
                FROM downloaded_chapters
                WHERE library_manga_id = @library_manga_id
                  AND status = 'completed'
                ORDER BY
                    CASE WHEN chapter_number IS NULL THEN 1 ELSE 0 END,
                    chapter_number DESC,
                    downloaded_at_utc DESC
                LIMIT 1
            """;

            cmd.Parameters.AddWithValue("@library_manga_id", libraryMangaId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var key = reader.IsDBNull(0) ? null : reader.GetString(0);
            double? number = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            return (key, number);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _connection = new SqliteConnection(connStr);
            await _connection.OpenAsync(ct);

            await using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS library_manga (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider_key TEXT NOT NULL,
                        manga_id_or_url TEXT NOT NULL,
                        title TEXT NOT NULL,
                        cover_url TEXT NULL,
                        local_path TEXT NOT NULL,
                        is_following INTEGER NOT NULL DEFAULT 1,
                        last_checked_at_utc TEXT NULL,
                        last_downloaded_chapter_key TEXT NULL,
                        last_downloaded_chapter_number REAL NULL,
                        new_chapters_count INTEGER NOT NULL DEFAULT 0,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        UNIQUE(provider_key, manga_id_or_url)
                    );

                    CREATE TABLE IF NOT EXISTS library_known_chapters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        library_manga_id INTEGER NOT NULL,
                        chapter_key TEXT NOT NULL,
                        chapter_number REAL NULL,
                        chapter_title TEXT NULL,
                        first_seen_at_utc TEXT NOT NULL,
                        FOREIGN KEY(library_manga_id) REFERENCES library_manga(id) ON DELETE CASCADE,
                        UNIQUE(library_manga_id, chapter_key)
                    );

                    CREATE TABLE IF NOT EXISTS downloaded_chapters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        library_manga_id INTEGER NOT NULL,
                        chapter_key TEXT NOT NULL,
                        chapter_number REAL NULL,
                        chapter_title TEXT NOT NULL,
                        downloaded_at_utc TEXT NOT NULL,
                        file_path TEXT NULL,
                        page_count INTEGER NOT NULL DEFAULT 0,
                        status TEXT NOT NULL,
                        error_message TEXT NULL,
                        FOREIGN KEY(library_manga_id) REFERENCES library_manga(id) ON DELETE CASCADE,
                        UNIQUE(library_manga_id, chapter_key),
                        CHECK(status IN ('completed','failed'))
                    );

                    CREATE INDEX IF NOT EXISTS idx_library_manga_following ON library_manga(is_following);
                    CREATE INDEX IF NOT EXISTS idx_downloaded_chapters_library ON downloaded_chapters(library_manga_id);
                    CREATE INDEX IF NOT EXISTS idx_known_chapters_library ON library_known_chapters(library_manga_id);
                """;

                await cmd.ExecuteNonQueryAsync(ct);
            }

            _initialized = true;
            _log?.Info($"[LibraryStore] Initialized at {_dbPath}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static LibraryMangaEntry MapLibraryManga(SqliteDataReader reader)
    {
        return new LibraryMangaEntry
        {
            Id = reader.GetInt64(0),
            ProviderKey = reader.GetString(1),
            MangaIdOrUrl = reader.GetString(2),
            Title = reader.GetString(3),
            CoverUrl = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            LocalPath = reader.GetString(5),
            IsFollowing = reader.GetInt64(6) != 0,
            LastCheckedAtUtc = reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            LastDownloadedChapterKey = reader.IsDBNull(8) ? null : reader.GetString(8),
            LastDownloadedChapterNumber = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            NewChaptersCount = reader.GetInt32(10),
            CreatedAtUtc = ParseUtc(reader.GetString(11)),
            UpdatedAtUtc = ParseUtc(reader.GetString(12))
        };
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
