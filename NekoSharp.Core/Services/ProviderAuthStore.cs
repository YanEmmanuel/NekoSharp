using Microsoft.Data.Sqlite;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed class ProviderAuthStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LogService? _log;
    private SqliteConnection? _connection;
    private bool _initialized;

    public ProviderAuthStore(string? dbPath = null, LogService? logService = null)
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

    public async Task<ProviderAuthCredentials?> TryGetAsync(string providerKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT provider_key, access_token, refresh_token, user_agent, origin, referer, x_app_key,
                       obtained_at_utc, expires_at_utc, user_json
                FROM provider_auth_credentials
                WHERE provider_key = @provider_key
            """;
            cmd.Parameters.AddWithValue("@provider_key", providerKey.ToLowerInvariant());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new ProviderAuthCredentials
            {
                ProviderKey = reader.GetString(0),
                AccessToken = reader.GetString(1),
                RefreshToken = reader.IsDBNull(2) ? null : reader.GetString(2),
                UserAgent = reader.GetString(3),
                Origin = reader.GetString(4),
                Referer = reader.GetString(5),
                XAppKey = reader.GetString(6),
                ObtainedAtUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
                ExpiresAtUtc = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)).ToUniversalTime(),
                UserJson = reader.IsDBNull(9) ? null : reader.GetString(9)
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ProviderAuthCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        await EnsureInitializedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO provider_auth_credentials
                (provider_key, access_token, refresh_token, user_agent, origin, referer, x_app_key,
                 obtained_at_utc, expires_at_utc, user_json)
                VALUES
                (@provider_key, @access_token, @refresh_token, @user_agent, @origin, @referer, @x_app_key,
                 @obtained_at_utc, @expires_at_utc, @user_json)
            """;

            cmd.Parameters.AddWithValue("@provider_key", credentials.ProviderKey.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@access_token", credentials.AccessToken);
            cmd.Parameters.AddWithValue("@refresh_token", (object?)credentials.RefreshToken ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_agent", credentials.UserAgent);
            cmd.Parameters.AddWithValue("@origin", credentials.Origin);
            cmd.Parameters.AddWithValue("@referer", credentials.Referer);
            cmd.Parameters.AddWithValue("@x_app_key", credentials.XAppKey);
            cmd.Parameters.AddWithValue("@obtained_at_utc", credentials.ObtainedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@expires_at_utc", credentials.ExpiresAtUtc?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@user_json", (object?)credentials.UserJson ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            _log?.Debug($"[ProviderAuth] Saved auth credentials for provider={credentials.ProviderKey}");
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
            cmd.CommandText = "DELETE FROM provider_auth_credentials WHERE provider_key = @provider_key";
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
                CREATE TABLE IF NOT EXISTS provider_auth_credentials (
                    provider_key    TEXT PRIMARY KEY,
                    access_token    TEXT NOT NULL,
                    refresh_token   TEXT NULL,
                    user_agent      TEXT NOT NULL,
                    origin          TEXT NOT NULL,
                    referer         TEXT NOT NULL,
                    x_app_key       TEXT NOT NULL,
                    obtained_at_utc TEXT NOT NULL,
                    expires_at_utc  TEXT NULL,
                    user_json       TEXT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _log?.Info($"[ProviderAuth] ProviderAuthStore initialized at {_dbPath}");
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
