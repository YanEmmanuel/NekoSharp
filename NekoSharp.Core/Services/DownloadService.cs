using System.IO.Compression;
using System.Net;
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
    
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(7)
    ];

    public int MaxConcurrentDownloads { get; set; } = 4;

    public DownloadService(ScraperManager scraperManager, HttpClient? httpClient = null,
        LogService? logService = null, CloudflareCredentialStore? cfStore = null, ISettingsStore? settingsStore = null)
    {
        _scraperManager = scraperManager;
        _log = logService;
        _settings = settingsStore;
        _httpClient = httpClient ?? CreateDefaultHttpClient(logService, cfStore);
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
            Timeout = TimeSpan.FromSeconds(60)
        };

        client.DefaultRequestHeaders.Add("User-Agent", UserAgentProvider.Default);
        client.DefaultRequestHeaders.Add("Accept", "image/avif,image/webp,image/apng,image/*;q=0.8");
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
            chapter.Pages = await scraper.GetPagesAsync(chapter, ct);
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

        using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);

        var tasks = chapter.Pages.Select(async page =>
        {
            await semaphore.WaitAsync(ct);
            try
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
                    await DownloadFileWithRetryAsync(page.ImageUrl, filePath, manga.Url, ct);
                }
                else
                {
                    var tempFile = Path.Combine(tempDir, $"{page.Number:D3}_tmp{originalExtension}");
                    try
                    {
                        await DownloadFileWithRetryAsync(page.ImageUrl, tempFile, manga.Url, ct);
                        await ConvertImageAsync(tempFile, filePath, targetStartFormat, compressionPercent, ct);
                    }
                    finally
                    {
                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }
                }
                
                page.LocalPath = filePath;

                var completed = Interlocked.Increment(ref completedPages);
                progress?.Report(new DownloadProgress
                {
                    CurrentPage = completed,
                    TotalPages = totalPages
                });
            }
            finally
            {
                semaphore.Release();
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

    private async Task DownloadFileWithRetryAsync(string url, string filePath, string referer, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Referer", referer);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await contentStream.CopyToAsync(fileStream, ct);
                
                return; 
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
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
}
