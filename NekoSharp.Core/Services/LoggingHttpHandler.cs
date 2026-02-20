using System.Diagnostics;

namespace NekoSharp.Core.Services;

 
 
 
public class LoggingHttpHandler : DelegatingHandler
{
    private readonly LogService _log;

    public LoggingHttpHandler(LogService log, HttpMessageHandler? inner = null)
        : base(inner ?? new HttpClientHandler())
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.Method.ToString();
        var url = request.RequestUri?.ToString() ?? "?";

        _log.Info($"→ {method} {url}");

        var headerLines = new List<string>();
        foreach (var header in request.Headers)
            headerLines.Add($"  {header.Key}: {string.Join(", ", header.Value)}");
        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
                headerLines.Add($"  {header.Key}: {string.Join(", ", header.Value)}");
        }
        if (headerLines.Count > 0)
            _log.Debug($"  Request Headers:\n{string.Join("\n", headerLines)}");

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct);
            sw.Stop();

            var status = (int)response.StatusCode;
            var contentLength = response.Content.Headers.ContentLength;
            var sizeStr = contentLength.HasValue ? FormatBytes(contentLength.Value) : "?";

            if (response.IsSuccessStatusCode)
                _log.Info($"← {status} {method} {url}  ({sw.ElapsedMilliseconds}ms, {sizeStr})");
            else
                _log.Warn($"← {status} {method} {url}  ({sw.ElapsedMilliseconds}ms)", 
                    $"Reason: {response.ReasonPhrase}");

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error($"✕ {method} {url}  ({sw.ElapsedMilliseconds}ms)", ex.Message);
            throw;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
