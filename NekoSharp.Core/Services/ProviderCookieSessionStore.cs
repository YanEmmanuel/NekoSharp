using Microsoft.Data.Sqlite;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed class ProviderCookieSessionStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LogService? _log;
    private SqliteConnection? _connection;
    private bool _initialized;

    public ProviderCookieSessionStore(string? dbPath = null, LogService? logService = null)
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

    public async Task<ProviderCookieSession?> TryGetAsync(string providerKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT provider_key, base_url, user_agent, cookies_json, updated_at_utc, user_display_name
                FROM provider_cookie_sessions
                WHERE provider_key = @provider_key
            """;
            cmd.Parameters.AddWithValue("@provider_key", providerKey.ToLowerInvariant());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new ProviderCookieSession
            {
                ProviderKey = reader.GetString(0),
                BaseUrl = reader.GetString(1),
                UserAgent = reader.GetString(2),
                CookiesJson = reader.GetString(3),
                UpdatedAtUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
                UserDisplayName = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ProviderCookieSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO provider_cookie_sessions
                (provider_key, base_url, user_agent, cookies_json, updated_at_utc, user_display_name)
                VALUES
                (@provider_key, @base_url, @user_agent, @cookies_json, @updated_at_utc, @user_display_name)
            """;
            cmd.Parameters.AddWithValue("@provider_key", session.ProviderKey.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@base_url", session.BaseUrl);
            cmd.Parameters.AddWithValue("@user_agent", session.UserAgent);
            cmd.Parameters.AddWithValue("@cookies_json", session.CookiesJson);
            cmd.Parameters.AddWithValue("@updated_at_utc", session.UpdatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@user_display_name", (object?)session.UserDisplayName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string providerKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM provider_cookie_sessions WHERE provider_key = @provider_key";
            cmd.Parameters.AddWithValue("@provider_key", providerKey.ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync(ct);
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

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS provider_cookie_sessions (
                    provider_key      TEXT PRIMARY KEY,
                    base_url          TEXT NOT NULL,
                    user_agent        TEXT NOT NULL,
                    cookies_json      TEXT NOT NULL,
                    updated_at_utc    TEXT NOT NULL,
                    user_display_name TEXT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _log?.Info($"[ProviderCookieSessionStore] Initialized at {_dbPath}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
