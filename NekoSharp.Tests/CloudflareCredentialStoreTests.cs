using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class CloudflareCredentialStoreTests
{
    [Fact]
    public async Task ClearAllAsync_RemovesAllEntries_AndCountAsyncReflectsState()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using var store = new CloudflareCredentialStore(dbPath);

            await store.SaveAsync(new CloudflareCredentials
            {
                Domain = "example.com",
                UserAgent = "ua-test",
                AllCookies = new Dictionary<string, string>
                {
                    ["cf_clearance"] = "token-1"
                },
                ObtainedAtUtc = DateTime.UtcNow
            });

            await store.SaveAsync(new CloudflareCredentials
            {
                Domain = "example.org",
                UserAgent = "ua-test",
                AllCookies = new Dictionary<string, string>
                {
                    ["cf_clearance"] = "token-2"
                },
                ObtainedAtUtc = DateTime.UtcNow
            });

            var before = await store.CountAsync();
            var removed = await store.ClearAllAsync();
            var after = await store.CountAsync();

            Assert.Equal(2, before);
            Assert.Equal(2, removed);
            Assert.Equal(0, after);
        }
        finally
        {
            CleanupTempDatabasePath(dbPath);
        }
    }

    private static string CreateTempDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NekoSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "cf.db");
    }

    private static void CleanupTempDatabasePath(string dbPath)
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
