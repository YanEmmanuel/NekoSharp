using Microsoft.Data.Sqlite;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public interface ISettingsStore
{
    Task InitializeAsync();
    
    Task<string?> GetStringAsync(string key, string? defaultValue = null);
    Task SetStringAsync(string key, string value);
    
    Task<int> GetIntAsync(string key, int defaultValue = 0);
    Task SetIntAsync(string key, int value);
    
    Task<bool> GetBoolAsync(string key, bool defaultValue = false);
    Task SetBoolAsync(string key, bool value);
    
    Task<T> GetEnumAsync<T>(string key, T defaultValue) where T : struct, Enum;
    Task SetEnumAsync<T>(string key, T value) where T : struct, Enum;
}

public sealed class SettingsStore : ISettingsStore, IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SqliteConnection? _connection;
    private bool _initialized;
    private readonly LogService? _log;

    public SettingsStore(string? dbPath = null, LogService? logService = null)
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

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            _connection = new SqliteConnection(builder.ConnectionString);
            await _connection.OpenAsync();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT
                )
            """;
            await cmd.ExecuteNonQueryAsync();

            _initialized = true;
            _log?.Debug($"[SettingsStore] Initialized DB at {_dbPath}");
        }
        catch (Exception ex)
        {
            _log?.Error($"[SettingsStore] Initialization failed: {ex.Message}");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_initialized)
            await InitializeAsync();
    }

    public async Task<string?> GetStringAsync(string key, string? defaultValue = null)
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            
            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
                return defaultValue;
            
            return result.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetStringAsync(string key, string value)
    {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO app_settings (key, value)
                VALUES (@key, @value)
            """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> GetIntAsync(string key, int defaultValue = 0)
    {
        var str = await GetStringAsync(key);
        if (int.TryParse(str, out var val))
            return val;
        return defaultValue;
    }

    public async Task SetIntAsync(string key, int value)
    {
        await SetStringAsync(key, value.ToString());
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var str = await GetStringAsync(key);
        if (bool.TryParse(str, out var val))
            return val;
        return defaultValue;
    }

    public async Task SetBoolAsync(string key, bool value)
    {
        await SetStringAsync(key, value.ToString());
    }

    public async Task<T> GetEnumAsync<T>(string key, T defaultValue) where T : struct, Enum
    {
        var str = await GetStringAsync(key);
        if (Enum.TryParse<T>(str, true, out var val))
            return val;
        return defaultValue;
    }

    public async Task SetEnumAsync<T>(string key, T value) where T : struct, Enum
    {
        await SetStringAsync(key, value.ToString());
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
