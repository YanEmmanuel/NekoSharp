using Microsoft.Data.Sqlite;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

 
 
 
 
 
public sealed class CloudflareCredentialStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SqliteConnection? _connection;
    private bool _initialized;
    private readonly LogService? _log;

    public CloudflareCredentialStore(string? dbPath = null, LogService? logService = null)
    {
        _log = logService;

        if (dbPath is not null)
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

     
     
     

     
     
     
    public async Task<CloudflareCredentials?> TryGetAsync(string domain)
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT domain, user_agent, cookies_json, obtained_at_utc
                FROM cf_credentials
                WHERE domain = @domain
            """;
            cmd.Parameters.AddWithValue("@domain", domain.ToLowerInvariant());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var creds = new CloudflareCredentials
            {
                Domain = reader.GetString(0),
                UserAgent = reader.GetString(1),
                CookiesJson = reader.GetString(2),
                ObtainedAtUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
            };

            if (creds.IsExpired)
            {
                _log?.Debug($"[CF Store] Credentials for {domain} expired. Removing.");
                await DeleteInternalAsync(domain);
                return null;
            }

            _log?.Debug($"[CF Store] Loaded valid credentials for {domain} (age: {(DateTime.UtcNow - creds.ObtainedAtUtc).TotalMinutes:F1} min)");
            return creds;
        }
        finally
        {
            _lock.Release();
        }
    }

     
     
     
    public async Task SaveAsync(CloudflareCredentials credentials)
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO cf_credentials (domain, user_agent, cookies_json, obtained_at_utc)
                VALUES (@domain, @user_agent, @cookies_json, @obtained_at_utc)
            """;
            cmd.Parameters.AddWithValue("@domain", credentials.Domain.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@user_agent", credentials.UserAgent);
            cmd.Parameters.AddWithValue("@cookies_json", credentials.CookiesJson);
            cmd.Parameters.AddWithValue("@obtained_at_utc", credentials.ObtainedAtUtc.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
            _log?.Info($"[CF Store] Saved credentials for {credentials.Domain} → {_dbPath}");
            _log?.Debug($"[CF Store] cf_clearance present: {credentials.AllCookies.ContainsKey("cf_clearance")}");
            _log?.Debug($"[CF Store] Total cookies: {credentials.AllCookies.Count}");
            _log?.Debug($"[CF Store] User-Agent: {credentials.UserAgent[..Math.Min(60, credentials.UserAgent.Length)]}…");
        }
        catch (Exception ex)
        {
            _log?.Error($"[CF Store] Failed to save credentials: {ex.Message}");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

     
     
     
    public async Task RemoveAsync(string domain)
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            await DeleteInternalAsync(domain);
        }
        finally
        {
            _lock.Release();
        }
    }

     
     
     
    public async Task<List<(string Domain, DateTime ObtainedAt, bool Expired)>> ListAllAsync()
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            var results = new List<(string, DateTime, bool)>();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT domain, obtained_at_utc FROM cf_credentials";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var domain = reader.GetString(0);
                var obtained = DateTime.Parse(reader.GetString(1)).ToUniversalTime();
                var expired = DateTime.UtcNow - obtained > TimeSpan.FromMinutes(25);
                results.Add((domain, obtained, expired));
            }
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

     
     
     

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString();

            _connection = new SqliteConnection(connStr);
            await _connection.OpenAsync();

             
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cf_credentials (
                    domain          TEXT PRIMARY KEY NOT NULL,
                    user_agent      TEXT NOT NULL,
                    cookies_json    TEXT NOT NULL,
                    obtained_at_utc TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();

             
            await using var cleanCmd = _connection.CreateCommand();
            cleanCmd.CommandText = "SELECT domain, obtained_at_utc FROM cf_credentials";
            var toDelete = new List<string>();
            await using var reader = await cleanCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var obtained = DateTime.Parse(reader.GetString(1)).ToUniversalTime();
                if (DateTime.UtcNow - obtained > TimeSpan.FromMinutes(25))
                    toDelete.Add(reader.GetString(0));
            }

            foreach (var domain in toDelete)
            {
                await using var delCmd = _connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM cf_credentials WHERE domain = @domain";
                delCmd.Parameters.AddWithValue("@domain", domain);
                await delCmd.ExecuteNonQueryAsync();
            }

            if (toDelete.Count > 0)
                _log?.Debug($"[CF Store] Cleaned up {toDelete.Count} expired credential(s) on startup.");

            _log?.Info($"[CF Store] SQLite database ready at {_dbPath}");
            _initialized = true;
        }
        catch (Exception ex)
        {
            _log?.Error($"[CF Store] Failed to initialize database: {ex.Message}", ex.ToString());
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

     
    private async Task DeleteInternalAsync(string domain)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM cf_credentials WHERE domain = @domain";
        cmd.Parameters.AddWithValue("@domain", domain.ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
