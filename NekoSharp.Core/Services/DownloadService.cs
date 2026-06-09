using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace NekoSharp.Core.Services;

 
 
 
public class DownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly ScraperManager _scraperManager;
    private readonly LogService? _log;
    private readonly ISettingsStore? _settings;
    private readonly TimeSpan[] _attemptTimeouts;
    private readonly TimeSpan[] _retryDelays;
    private readonly ConcurrentDictionary<string, HostThrottleState> _hostThrottles = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransientFailureGate _transientFailureGate = new();
    private readonly SemaphoreSlim _downloadSlotSignal = new(0);
    private int _waitingDownloadSlots;
    private int _activeDownloads;
    private int _maxConcurrentDownloads = 4;
    private int _adaptiveMaxConcurrentDownloads = int.MaxValue;
    private int _successfulDownloadsSinceThrottle;

    private static readonly TimeSpan[] DefaultAttemptTimeouts = [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(25),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(60)
    ];

    private static readonly TimeSpan[] DefaultRetryDelays = [
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    public int MaxConcurrentDownloads
    {
        get => Volatile.Read(ref _maxConcurrentDownloads);
        set
        {
            var normalized = Math.Max(1, value);
            Volatile.Write(ref _maxConcurrentDownloads, normalized);

            var waiting = Volatile.Read(ref _waitingDownloadSlots);
            if (waiting > 0)
                _downloadSlotSignal.Release(Math.Min(waiting, normalized));
        }
    }

    public DownloadService(ScraperManager scraperManager, HttpClient? httpClient = null,
        LogService? logService = null, CloudflareCredentialStore? cfStore = null, ISettingsStore? settingsStore = null,
        TimeSpan[]? attemptTimeouts = null, TimeSpan[]? retryDelays = null)
    {
        _scraperManager = scraperManager;
        _log = logService;
        _settings = settingsStore;
        _httpClient = httpClient ?? CreateDefaultHttpClient(logService, cfStore);
        _attemptTimeouts = attemptTimeouts is { Length: > 0 } ? attemptTimeouts : DefaultAttemptTimeouts;
        _retryDelays = retryDelays is { Length: > 0 } ? retryDelays : DefaultRetryDelays;
    }

    private static HttpClient CreateDefaultHttpClient(LogService? logService, CloudflareCredentialStore? cfStore)
    {
        var innerHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        HttpMessageHandler handler = new CloudflareHandler(
            inner: innerHandler,
            logService: logService,
            store: cfStore);

        if (logService is not null)
            handler = new LoggingHttpHandler(logService, handler);

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.Add("User-Agent", UserAgentProvider.Default);
        client.DefaultRequestHeaders.Add("Accept", "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        return client;
    }

    public async Task DownloadChapterAsync(
        Manga manga,
        Chapter chapter,
        string outputDirectory,
        DownloadFormat format,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scraper = _scraperManager.GetScraperForUrl(manga.Url)
                      ?? throw new InvalidOperationException($"Nenhum provedor encontrado para a URL: {manga.Url}");
                      
        var targetStartFormat = _settings != null 
            ? await _settings.GetEnumAsync("Download.ImageFormat", ImageFormat.Original)
            : ImageFormat.Original;

        var compressionPercent = _settings != null
            ? await _settings.GetIntAsync("Download.ImageCompressionPercent", 0)
            : 0;
        compressionPercent = Math.Clamp(compressionPercent, 0, 100);

        if (chapter.Pages.Count == 0)
        {
            chapter.Pages = await ExecuteProviderOperationWithRetryAsync(
                () => scraper.GetPagesAsync(chapter, ct),
                scraper.Name,
                "carregar páginas do capítulo",
                ct);
        }

        var mangaDir = DownloadPaths.GetMangaDirectory(outputDirectory, manga);
        Directory.CreateDirectory(mangaDir);

        var chapterDir = DownloadPaths.GetChapterDirectory(outputDirectory, manga, chapter);
        var tempDir = chapterDir;

        if (format == DownloadFormat.Cbz)
        {
            tempDir = chapterDir + "__tmp";
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        Directory.CreateDirectory(tempDir);

        var totalPages = chapter.Pages.Count;
        var completedPages = 0;

        var tasks = chapter.Pages.Select(async page =>
        {
            var originalExtension = GetFileExtension(page.ImageUrl);
                
            string targetExtension;
            if (targetStartFormat == ImageFormat.Original)
            {
                targetExtension = originalExtension;
            }
            else
            {
                targetExtension = targetStartFormat switch
                {
                    ImageFormat.Jpeg => ".jpg",
                    ImageFormat.Png => ".png",
                    ImageFormat.WebP => ".webp",
                    _ => originalExtension
                };
            }
                
            var fileName = $"{page.Number:D3}{targetExtension}";
            var filePath = Path.Combine(tempDir, fileName);

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
            {
                page.LocalPath = filePath;
                ReportPageCompleted();
                return;
            }

            bool needsConversion = targetStartFormat != ImageFormat.Original &&
                                   !string.Equals(originalExtension, targetExtension, StringComparison.OrdinalIgnoreCase) && 
                                   !(originalExtension == ".jpeg" && targetExtension == ".jpg");

            if (targetStartFormat == ImageFormat.Original && compressionPercent > 0)
            {
                needsConversion = CanReencodeExtension(originalExtension);
            }

            if (targetStartFormat != ImageFormat.Original && compressionPercent > 0)
            {
                needsConversion = true;
            }

            if (!needsConversion)
            {
                await DownloadFileWithRetryAsync(
                    page.ImageUrl,
                    filePath,
                    page.RefererUrl ?? chapter.Url,
                    scraper,
                    ct);
            }
            else
            {
                var tempFile = Path.Combine(tempDir, $"{page.Number:D3}_tmp{originalExtension}");
                try
                {
                    await DownloadFileWithRetryAsync(
                        page.ImageUrl,
                        tempFile,
                        page.RefererUrl ?? chapter.Url,
                        scraper,
                        ct);
                    await ConvertImageAsync(tempFile, filePath, targetStartFormat, compressionPercent, ct);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            }
                
            page.LocalPath = filePath;
            ReportPageCompleted();

            void ReportPageCompleted()
            {
                var completed = Interlocked.Increment(ref completedPages);
                progress?.Report(new DownloadProgress
                {
                    CurrentPage = completed,
                    TotalPages = totalPages
                });
            }
        });

        await Task.WhenAll(tasks);

        if (_settings != null)
        {
            var stitchSettings = await SmartStitchService.LoadSettingsAsync(_settings);
            if (stitchSettings.Enabled)
            {
                progress?.Report(new DownloadProgress
                {
                    CurrentPage = totalPages,
                    TotalPages = totalPages,
                    IsStitching = true,
                    StitchingStatus = "SmartStitch: Processando..."
                });

                _log?.Info($"[SmartStitch] Executando pós-processamento no capítulo {chapter.Number}...");
                var stitcher = new SmartStitchService(_log);

                try
                {
                    await stitcher.ProcessAsync(tempDir, stitchSettings, ct);

                    progress?.Report(new DownloadProgress
                    {
                        CurrentPage = totalPages,
                        TotalPages = totalPages,
                        IsStitching = true,
                        StitchingStatus = "SmartStitch: Concluído ✓"
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[SmartStitch] Falhou no capítulo {chapter.Number}. Mantendo imagens originais. Motivo: {ex.Message}");
                    progress?.Report(new DownloadProgress
                    {
                        CurrentPage = totalPages,
                        TotalPages = totalPages,
                        IsStitching = true,
                        StitchingStatus = "SmartStitch: Falhou, mantendo imagens originais."
                    });
                }
            }
        }

        if (format == DownloadFormat.Cbz)
        {
            var archivePath = DownloadPaths.GetChapterArchivePath(outputDirectory, manga, chapter, ".cbz");
            if (File.Exists(archivePath))
                File.Delete(archivePath);

            ZipFile.CreateFromDirectory(tempDir, archivePath, CompressionLevel.Optimal, false);

            try { Directory.Delete(tempDir, true); }
            catch {   }
        }
    }

    public async Task DownloadChaptersAsync(
        Manga manga,
        IEnumerable<Chapter> chapters,
        string outputDirectory,
        DownloadFormat format,
        IProgress<(Chapter chapter, DownloadProgress progress)>? progress = null,
        CancellationToken ct = default)
    {
        foreach (var chapter in chapters)
        {
            ct.ThrowIfCancellationRequested();

            var chapterProgress = new Progress<DownloadProgress>(p =>
            {
                progress?.Report((chapter, p));
            });

            await DownloadChapterAsync(manga, chapter, outputDirectory, format, chapterProgress, ct);
        }
    }

    private async Task ConvertImageAsync(string inputPath, string outputPath, ImageFormat format, int compressionPercent, CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(inputPath, ct);
            var quality = Math.Clamp(100 - compressionPercent, 1, 100);
            var pngLevel = Math.Clamp((int)Math.Round(compressionPercent / 100.0 * 9), 0, 9);
            var maxColors = Math.Clamp(256 - (int)Math.Round(compressionPercent * 2.4), 16, 256);

            ApplyAggressiveResize(image, compressionPercent);
            
            switch (format)
            {
                case ImageFormat.Jpeg:
                    await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = quality }, ct);
                    break;
                case ImageFormat.Png:
                    if (compressionPercent > 0)
                    {
                        image.Mutate(ctx => ctx.Quantize(new WuQuantizer(new QuantizerOptions
                        {
                            MaxColors = maxColors,
                            Dither = null
                        })));
                    }

                    await image.SaveAsPngAsync(outputPath, new PngEncoder
                    {
                        CompressionLevel = (PngCompressionLevel)pngLevel
                    }, ct);
                    break;
                case ImageFormat.WebP:
                    await image.SaveAsWebpAsync(outputPath, new WebpEncoder
                    {
                        Quality = quality,
                        Method = WebpEncodingMethod.BestQuality,
                        FileFormat = WebpFileFormatType.Lossy
                    }, ct);
                    break;
                default:
                    File.Copy(inputPath, outputPath, true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"Falha ao converter imagem {inputPath} para {format}: {ex}");
            File.Copy(inputPath, outputPath, true);
        }
    }

    private async Task DownloadFileWithRetryAsync(
        string url,
        string filePath,
        string referer,
        IScraper scraper,
        CancellationToken ct)
    {
        var currentUrl = url;
        var attempt = 0;

        while (true)
        {
            var attemptNumber = attempt + 1;
            var timeout = GetAttemptTimeout(attempt);
            var host = GetHost(currentUrl);
            var hostThrottle = _hostThrottles.GetOrAdd(host, static _ => new HostThrottleState());
            var partialFilePath = filePath + ".part";

            try
            {
                await _transientFailureGate.WaitBeforeAttemptAsync(ct);
                await hostThrottle.WaitBeforeAttemptAsync(ct);
                await using var _ = await AcquireDownloadSlotAsync(ct);
                await _transientFailureGate.WaitBeforeAttemptAsync(ct);
                await hostThrottle.WaitBeforeAttemptAsync(ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                    request.Headers.Referrer = refererUri;
                else
                    request.Headers.TryAddWithoutValidation("Referer", referer);

                request.Headers.Add("Accept", "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5");

                if (scraper is IAuthenticatedRequestProvider authenticatedProvider)
                    await authenticatedProvider.ApplyRequestAuthenticationAsync(request, ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (!response.IsSuccessStatusCode && IsTransientStatusCode(response.StatusCode))
                    throw new HttpRequestException(
                        $"Servidor retornou {(int)response.StatusCode} ({response.StatusCode}) para {currentUrl}.",
                        inner: null,
                        response.StatusCode);

                response.EnsureSuccessStatusCode();

                TryDeleteFile(partialFilePath);
                await using var contentStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                await using (var fileStream = new FileStream(
                                 partialFilePath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 8192,
                                 true))
                {
                    await contentStream.CopyToAsync(fileStream, timeoutCts.Token);
                }

                File.Move(partialFilePath, filePath, overwrite: true);

                hostThrottle.ReportSuccess();
                ReportDownloadSuccess();
                return;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                TryDeleteFile(partialFilePath);
                var timeoutException = new TimeoutException(
                    $"Tempo esgotado ao baixar {currentUrl} na tentativa {attemptNumber}.",
                    ex);

                currentUrl = await TryRefreshTransientDownloadUrlAsync(currentUrl, referer, ct);
                var cooldown = hostThrottle.ReportFailure(timeoutLike: true);
                await PauseAndRetryAsync(currentUrl, attemptNumber, cooldown, timeout, timeoutException, ct);
            }
            catch (Exception ex) when (IsTransientDownloadException(ex))
            {
                TryDeleteFile(partialFilePath);
                currentUrl = await TryRefreshTransientDownloadUrlAsync(currentUrl, referer, ct);

                var timeoutLike = ex is TimeoutException ||
                                  ex is TaskCanceledException ||
                                  ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.RequestTimeout;

                var cooldown = hostThrottle.ReportFailure(timeoutLike);
                await PauseAndRetryAsync(currentUrl, attemptNumber, cooldown, timeout, ex, ct);
            }
            catch
            {
                TryDeleteFile(partialFilePath);
                throw;
            }

            attempt++;
        }
    }

    private async Task PauseAndRetryAsync(
        string url,
        int attemptNumber,
        TimeSpan hostCooldown,
        TimeSpan timeout,
        Exception exception,
        CancellationToken ct)
    {
        var retryDelay = GetRetryDelay(attemptNumber - 1);
        var waitTime = retryDelay > hostCooldown ? retryDelay : hostCooldown;
        _transientFailureGate.Pause(waitTime);
        ReduceAdaptiveConcurrency();

        _log?.Warn(
            $"[Download] Falha transitória em {GetHost(url)}: {exception.Message} " +
            $"Pausando downloads por {waitTime.TotalSeconds:0.##}s antes da tentativa {attemptNumber + 1} " +
            $"(timeout atual {timeout.TotalSeconds:0}s, concorrência {GetEffectiveMaxConcurrentDownloads()}).");

        await _transientFailureGate.WaitBeforeAttemptAsync(ct);
    }

    private async Task<T> ExecuteProviderOperationWithRetryAsync<T>(
        Func<Task<T>> operation,
        string providerName,
        string operationName,
        CancellationToken ct)
    {
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await _transientFailureGate.WaitBeforeAttemptAsync(ct);

            try
            {
                return await operation();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                var timeout = new TimeoutException(
                    $"Tempo esgotado ao {operationName} no provider {providerName}.",
                    ex);
                await PauseProviderOperationAsync(providerName, operationName, attempt, timeout, ct);
            }
            catch (Exception ex) when (IsTransientDownloadException(ex))
            {
                await PauseProviderOperationAsync(providerName, operationName, attempt, ex, ct);
            }

            attempt++;
        }
    }

    private async Task PauseProviderOperationAsync(
        string providerName,
        string operationName,
        int attempt,
        Exception exception,
        CancellationToken ct)
    {
        var waitTime = GetRetryDelay(attempt);
        _transientFailureGate.Pause(waitTime);
        ReduceAdaptiveConcurrency();

        _log?.Warn(
            $"[Download] Provider {providerName} falhou ao {operationName}: {exception.Message} " +
            $"Nova tentativa em {waitTime.TotalSeconds:0.##}s.");

        await _transientFailureGate.WaitBeforeAttemptAsync(ct);
    }

    private async ValueTask<DownloadSlotLease> AcquireDownloadSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (TryAcquireDownloadSlot())
                return new DownloadSlotLease(this);

            Interlocked.Increment(ref _waitingDownloadSlots);
            try
            {
                if (TryAcquireDownloadSlot())
                    return new DownloadSlotLease(this);

                await _downloadSlotSignal.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingDownloadSlots);
            }
        }
    }

    private bool TryAcquireDownloadSlot()
    {
        while (true)
        {
            var active = Volatile.Read(ref _activeDownloads);
            var limit = GetEffectiveMaxConcurrentDownloads();
            if (active >= limit)
                return false;

            if (Interlocked.CompareExchange(ref _activeDownloads, active + 1, active) == active)
                return true;
        }
    }

    private void ReleaseDownloadSlot()
    {
        Interlocked.Decrement(ref _activeDownloads);

        if (Volatile.Read(ref _waitingDownloadSlots) > 0)
            _downloadSlotSignal.Release();
    }

    private int GetEffectiveMaxConcurrentDownloads()
    {
        var configured = MaxConcurrentDownloads;
        var adaptive = Volatile.Read(ref _adaptiveMaxConcurrentDownloads);
        return adaptive == int.MaxValue ? configured : Math.Min(configured, adaptive);
    }

    private void ReduceAdaptiveConcurrency()
    {
        while (true)
        {
            var configured = MaxConcurrentDownloads;
            var current = Volatile.Read(ref _adaptiveMaxConcurrentDownloads);
            var effective = current == int.MaxValue ? configured : Math.Min(configured, current);
            var reduced = Math.Max(1, effective / 2);

            if (reduced >= effective)
                break;

            if (Interlocked.CompareExchange(ref _adaptiveMaxConcurrentDownloads, reduced, current) == current)
                break;
        }

        Interlocked.Exchange(ref _successfulDownloadsSinceThrottle, 0);
    }

    private void ReportDownloadSuccess()
    {
        var current = Volatile.Read(ref _adaptiveMaxConcurrentDownloads);
        if (current == int.MaxValue)
            return;

        var requiredSuccesses = Math.Max(4, current * 4);
        if (Interlocked.Increment(ref _successfulDownloadsSinceThrottle) < requiredSuccesses)
            return;

        Interlocked.Exchange(ref _successfulDownloadsSinceThrottle, 0);

        while (true)
        {
            current = Volatile.Read(ref _adaptiveMaxConcurrentDownloads);
            if (current == int.MaxValue)
                return;

            var configured = MaxConcurrentDownloads;
            var increased = current + 1;
            var replacement = increased >= configured ? int.MaxValue : increased;
            if (Interlocked.CompareExchange(ref _adaptiveMaxConcurrentDownloads, replacement, current) != current)
                continue;

            var waiting = Volatile.Read(ref _waitingDownloadSlots);
            if (waiting > 0)
                _downloadSlotSignal.Release(Math.Min(waiting, GetEffectiveMaxConcurrentDownloads()));

            _log?.Debug(
                $"[Download] Concorrência recuperada para {GetEffectiveMaxConcurrentDownloads()} após downloads estáveis.");
            return;
        }
    }

    private TimeSpan GetAttemptTimeout(int attempt)
    {
        var index = Math.Min(attempt, _attemptTimeouts.Length - 1);
        return _attemptTimeouts[index];
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var index = Math.Min(attempt, _retryDelays.Length - 1);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(100, 350));
        return _retryDelays[index] + jitter;
    }

    private static bool IsTransientDownloadException(Exception ex)
    {
        return ex switch
        {
            TimeoutException => true,
            TaskCanceledException => true,
            HttpRequestException httpEx => !httpEx.StatusCode.HasValue || IsTransientStatusCode(httpEx.StatusCode.Value),
            _ => false
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or (HttpStatusCode)429
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.InternalServerError;
    }

    private static string GetHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "host-desconhecido";
        }
    }

    private async Task<string> TryRefreshTransientDownloadUrlAsync(string currentUrl, string referer, CancellationToken ct)
    {
        if (!TryParseMangaDexAtHomeReferer(referer, out var atHomeUri))
            return currentUrl;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EnsureMangaDexForcePort443(atHomeUri));
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: ct);

            var root = document.RootElement;
            var baseUrl = root.GetProperty("baseUrl").GetString();
            var chapter = root.GetProperty("chapter");
            var hash = chapter.GetProperty("hash").GetString();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(hash))
                return currentUrl;

            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
                return currentUrl;

            var segments = currentUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3)
                return currentUrl;

            var modeSegment = string.Equals(segments[0], "data-saver", StringComparison.OrdinalIgnoreCase)
                ? "data-saver"
                : "data";
            var fileName = segments[^1];
            var refreshedUrl = $"{baseUrl.TrimEnd('/')}/{modeSegment}/{hash}/{fileName}";

            if (!string.Equals(refreshedUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                _log?.Debug($"[Download] MangaDex atualizou URL da página para novo host: {GetHost(currentUrl)} -> {GetHost(refreshedUrl)}");

            return refreshedUrl;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return currentUrl;
        }
        catch (Exception ex)
        {
            _log?.Debug($"[Download] Falha ao renovar URL do MangaDex para retry: {ex.Message}");
            return currentUrl;
        }
    }

    private static bool TryParseMangaDexAtHomeReferer(string referer, out Uri atHomeUri)
    {
        if (Uri.TryCreate(referer, UriKind.Absolute, out atHomeUri!) &&
            atHomeUri.Host.Equals("api.mangadex.org", StringComparison.OrdinalIgnoreCase) &&
            atHomeUri.AbsolutePath.StartsWith("/at-home/server/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        atHomeUri = null!;
        return false;
    }

    private static Uri EnsureMangaDexForcePort443(Uri atHomeUri)
    {
        var query = atHomeUri.Query.AsSpan().TrimStart('?');
        if (query.Contains("forcePort443=true", StringComparison.OrdinalIgnoreCase))
            return atHomeUri;

        var separator = string.IsNullOrEmpty(atHomeUri.Query) ? "?" : "&";
        return new Uri($"{atHomeUri}{separator}forcePort443=true");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string GetFileExtension(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".avif" or ".bmp" => ext,
                _ => ".jpg"
            };
        }
        catch
        {
            return ".jpg";
        }
    }

    private static bool CanReencodeExtension(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".webp";
    }

    private static void ApplyAggressiveResize(Image image, int compressionPercent)
    {
        if (compressionPercent <= 0)
            return;

        if (compressionPercent < 45)
            return;

        var intensity = (compressionPercent - 45) / 55.0;
        var scale = 1.0 - (0.75 * intensity);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        if (width == image.Width && height == image.Height)
            return;

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic
        }));
    }

    private sealed class DownloadSlotLease : IAsyncDisposable
    {
        private DownloadService? _owner;

        public DownloadSlotLease(DownloadService owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseDownloadSlot();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostThrottleState
    {
        private readonly object _sync = new();
        private DateTimeOffset _blockedUntilUtc = DateTimeOffset.MinValue;
        private int _consecutiveFailures;

        public async Task WaitBeforeAttemptAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan delay;
                lock (_sync)
                {
                    delay = _blockedUntilUtc - DateTimeOffset.UtcNow;
                }

                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, ct);
            }
        }

        public void ReportSuccess()
        {
            lock (_sync)
            {
                _consecutiveFailures = 0;
                _blockedUntilUtc = DateTimeOffset.MinValue;
            }
        }

        public TimeSpan ReportFailure(bool timeoutLike)
        {
            lock (_sync)
            {
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);

                var baseDelayMs = timeoutLike ? 450 : 250;
                var scale = Math.Pow(2, _consecutiveFailures - 1);
                var jitterMs = Random.Shared.Next(75, 250);
                var cooldown = TimeSpan.FromMilliseconds(Math.Min(4_000, baseDelayMs * scale) + jitterMs);
                var candidate = DateTimeOffset.UtcNow + cooldown;

                if (candidate > _blockedUntilUtc)
                    _blockedUntilUtc = candidate;

                return cooldown;
            }
        }
    }

    private sealed class TransientFailureGate
    {
        private readonly object _sync = new();
        private DateTimeOffset _blockedUntilUtc = DateTimeOffset.MinValue;

        public void Pause(TimeSpan delay)
        {
            var candidate = DateTimeOffset.UtcNow + delay;
            lock (_sync)
            {
                if (candidate > _blockedUntilUtc)
                    _blockedUntilUtc = candidate;
            }
        }

        public async Task WaitBeforeAttemptAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan delay;
                lock (_sync)
                {
                    delay = _blockedUntilUtc - DateTimeOffset.UtcNow;
                }

                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, ct);
            }
        }
    }
}
