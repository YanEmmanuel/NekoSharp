using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NekoSharp.Core.Services;

public static class MangaDexHttpTransport
{
    private static readonly TimeSpan SystemDnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AddressConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DnsCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly Uri[] DnsOverHttpsEndpoints =
    [
        new("https://1.1.1.1/dns-query"),
        new("https://8.8.8.8/resolve"),
    ];

    private static readonly HttpClient DnsOverHttpsClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly ConcurrentDictionary<string, DnsCacheEntry> FallbackDnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> LoggedFallbackHosts =
        new(StringComparer.OrdinalIgnoreCase);

    public static SocketsHttpHandler CreateHandler(
        LogService? logService = null,
        DecompressionMethods automaticDecompression = DecompressionMethods.None)
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = automaticDecompression,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = (context, ct) => ConnectAsync(context, logService, ct),
        };
    }

    internal static bool IsMangaDexHost(string host)
    {
        return host.Equals("mangadex.org", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".mangadex.org", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("mangadex.network", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".mangadex.network", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<IPAddress[]> ResolveMangaDexAddressesAsync(
        string host,
        CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? systemResolver = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? fallbackResolver = null)
    {
        systemResolver ??= static (name, token) =>
            Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, token);
        fallbackResolver ??= ResolveUsingDnsOverHttpsAsync;

        using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dnsCts.CancelAfter(SystemDnsTimeout);

        try
        {
            var addresses = await systemResolver(host, dnsCts.Token);
            if (addresses.Length > 0)
                return addresses;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }

        ct.ThrowIfCancellationRequested();
        return await fallbackResolver(host, ct);
    }

    internal static IPAddress[] ParseDnsOverHttpsResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Answer", out var answers) ||
            answers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var addresses = new List<IPAddress>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) ||
                type.GetInt32() != 1 ||
                !answer.TryGetProperty("data", out var data) ||
                !IPAddress.TryParse(data.GetString(), out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            addresses.Add(address);
        }

        return addresses.Distinct().ToArray();
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        LogService? logService,
        CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        if (!IsMangaDexHost(endpoint.Host))
            return await ConnectToEndpointAsync(endpoint, ct);

        var usedFallback = false;
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveMangaDexAddressesAsync(
                endpoint.Host,
                ct,
                fallbackResolver: async (host, token) =>
                {
                    usedFallback = true;
                    return await ResolveUsingDnsOverHttpsAsync(host, token);
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"Não foi possível resolver o host MangaDex {endpoint.Host}.",
                ex);
        }

        if (usedFallback && LoggedFallbackHosts.TryAdd(endpoint.Host, 0))
        {
            logService?.Warn(
                $"[MangaDex] DNS do sistema falhou para {endpoint.Host}; usando DNS-over-HTTPS.");
        }

        return await ConnectToAddressesAsync(endpoint, addresses, ct);
    }

    private static async Task<IPAddress[]> ResolveUsingDnsOverHttpsAsync(
        string host,
        CancellationToken ct)
    {
        if (FallbackDnsCache.TryGetValue(host, out var cached) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return cached.Addresses;
        }

        Exception? lastException = null;
        foreach (var endpoint in DnsOverHttpsEndpoints)
        {
            try
            {
                var separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
                var requestUri = new Uri(
                    $"{endpoint}{separator}name={Uri.EscapeDataString(host)}&type=A");
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd("application/dns-json");

                using var response = await DnsOverHttpsClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var addresses = ParseDnsOverHttpsResponse(json);
                if (addresses.Length == 0)
                    continue;

                FallbackDnsCache[host] = new DnsCacheEntry(
                    addresses,
                    DateTimeOffset.UtcNow + DnsCacheLifetime);
                return addresses;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException(
            $"DNS-over-HTTPS não retornou endereços IPv4 para {host}.",
            lastException);
    }

    private static async ValueTask<Stream> ConnectToEndpointAsync(
        DnsEndPoint endpoint,
        CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectToAddressesAsync(
        DnsEndPoint endpoint,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken ct)
    {
        Exception? lastException = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(AddressConnectTimeout);
                await socket.ConnectAsync(address, endpoint.Port, connectCts.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastException = ex;
            }
        }

        throw new HttpRequestException(
            $"Não foi possível conectar ao host MangaDex {endpoint.Host}:{endpoint.Port}.",
            lastException);
    }

    private sealed record DnsCacheEntry(
        IPAddress[] Addresses,
        DateTimeOffset ExpiresAtUtc);
}
