using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

public sealed class SmartStitchService
{
    private readonly LogService? _log;

    public SmartStitchService(LogService? logService = null)
    {
        _log = logService;
    }

    public async Task ProcessAsync(
        string inputDirectory,
        SmartStitchSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.Enabled)
            return;

        _log?.Info($"[SmartStitch] Iniciando processamento em {inputDirectory}");

        var imageFiles = GetSortedImageFiles(inputDirectory);
        if (imageFiles.Length == 0)
        {
            _log?.Warn("[SmartStitch] Nenhuma imagem encontrada. Ignorando processamento.");
            return;
        }

        _log?.Debug($"[SmartStitch] {imageFiles.Length} imagem(ns) encontrada(s) para processar.");

        var images = await LoadImagesAsync(imageFiles, ct);
        if (images.Count == 0)
        {
            _log?.Warn("[SmartStitch] Nenhuma imagem válida para processar.");
            return;
        }

        List<Image<Rgba32>> slices;
        try
        {
            images = EnforceWidth(images, settings.WidthEnforcement, settings.CustomWidth);
            ct.ThrowIfCancellationRequested();

            _log?.Debug("[SmartStitch] Combinando imagens em uma faixa única...");
            using var combined = Combine(images);

            _log?.Debug("[SmartStitch] Detectando pontos de corte...");
            var rawSlicePoints = DetectSlicePoints(combined, settings);
            var slicePoints = NormalizeSliceLocations(rawSlicePoints, combined.Height);

            _log?.Info($"[SmartStitch] {Math.Max(0, slicePoints.Count - 1)} corte(s) identificado(s).");
            slices = Slice(combined, slicePoints);
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
        }

        if (slices.Count == 0)
        {
            _log?.Warn("[SmartStitch] Nenhum recorte gerado. Mantendo imagens originais.");
            return;
        }

        var stagingDirectory = Path.Combine(inputDirectory, "__nekosharp_stitch_tmp");
        try
        {
            PrepareStagingDirectory(stagingDirectory);
            await SaveSlicesAsync(slices, stagingDirectory, settings, ct);
            ReplaceOutputFiles(imageFiles, stagingDirectory, inputDirectory);
            _log?.Info("[SmartStitch] Processamento concluído.");
        }
        finally
        {
            foreach (var slice in slices)
                slice.Dispose();

            TryDeleteDirectory(stagingDirectory);
        }
    }

    #region Image Loading

    private static string[] GetSortedImageFiles(string directory)
    {
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tga", ".gif"
        };

        return Directory.GetFiles(directory)
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<List<Image<Rgba32>>> LoadImagesAsync(string[] files, CancellationToken ct)
    {
        var images = new List<Image<Rgba32>>(files.Length);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var img = await Image.LoadAsync<Rgba32>(file, ct);
                images.Add(img);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[SmartStitch] Ignorando imagem inválida '{Path.GetFileName(file)}': {ex.Message}");
            }
        }

        return images;
    }

    #endregion

    #region Width Enforcement

    private List<Image<Rgba32>> EnforceWidth(
        List<Image<Rgba32>> images,
        StitchWidthEnforcement enforcement,
        int customWidth)
    {
        if (enforcement == StitchWidthEnforcement.None || images.Count == 0)
            return images;

        var targetWidth = enforcement == StitchWidthEnforcement.Automatic
            ? images.Min(img => img.Width)
            : Math.Max(1, customWidth);

        _log?.Debug($"[SmartStitch] Forçando largura para {targetWidth}px ({enforcement})");

        foreach (var img in images)
        {
            if (img.Width == targetWidth)
                continue;

            var ratio = (double)img.Height / img.Width;
            var newHeight = Math.Max(1, (int)(ratio * targetWidth));
            img.Mutate(ctx => ctx.Resize(targetWidth, newHeight, KnownResamplers.Bicubic));
        }

        return images;
    }

    #endregion

    #region Combine

    private static Image<Rgba32> Combine(List<Image<Rgba32>> images)
    {
        var maxWidth = images.Max(img => img.Width);
        var totalHeight = images.Sum(img => img.Height);

        var combined = new Image<Rgba32>(maxWidth, totalHeight);
        var yOffset = 0;

        foreach (var img in images)
        {
            combined.Mutate(ctx => ctx.DrawImage(img, new Point(0, yOffset), 1f));
            yOffset += img.Height;
        }

        return combined;
    }

    #endregion

    #region Slice Detection

    private List<int> DetectSlicePoints(Image<Rgba32> combined, SmartStitchSettings settings)
    {
        return settings.DetectorType switch
        {
            StitchDetectorType.PixelComparison => DetectPixelComparison(
                combined,
                settings.SplitHeight,
                settings.Sensitivity,
                settings.ScanStep,
                settings.IgnorablePixels),
            _ => DetectDirectSlicing(combined.Height, settings.SplitHeight)
        };
    }

    private static List<int> DetectDirectSlicing(int totalHeight, int splitHeight)
    {
        splitHeight = Math.Max(1, splitHeight);

        var locations = new List<int> { 0 };
        var row = splitHeight;
        while (row < totalHeight)
        {
            locations.Add(row);
            row += splitHeight;
        }

        if (locations[^1] != totalHeight)
            locations.Add(totalHeight);

        return locations;
    }

    private List<int> DetectPixelComparison(
        Image<Rgba32> combined,
        int splitHeight,
        int sensitivity,
        int scanStep,
        int ignorablePixels)
    {
        splitHeight = Math.Max(1, splitHeight);
        scanStep = Math.Clamp(scanStep, 1, 100);
        sensitivity = Math.Clamp(sensitivity, 0, 100);

        var threshold = (int)(255 * (1.0 - sensitivity / 100.0));
        var width = combined.Width;
        var height = combined.Height;

        var sliceLocations = new List<int> { 0 };

        combined.ProcessPixelRows(accessor =>
        {
            var row = splitHeight;
            var moveUp = true;
            var startX = Math.Max(1, ignorablePixels + 1);
            var endX = width - Math.Max(0, ignorablePixels);

            while (row > 0 && row < height)
            {
                if (startX >= endX)
                {
                    sliceLocations.Add(row);
                    row += splitHeight;
                    moveUp = true;
                    continue;
                }

                var canSlice = true;
                var rowSpan = accessor.GetRowSpan(row);
                for (int x = startX; x < endX; x++)
                {
                    var prevGray = ToGray(rowSpan[x - 1]);
                    var currGray = ToGray(rowSpan[x]);
                    var diff = currGray - prevGray;
                    if (diff > threshold || diff < -threshold)
                    {
                        canSlice = false;
                        break;
                    }
                }

                if (canSlice)
                {
                    sliceLocations.Add(row);
                    row += splitHeight;
                    moveUp = true;
                    continue;
                }

                if (row - sliceLocations[^1] <= 0.4 * splitHeight)
                {
                    row = sliceLocations[^1] + splitHeight;
                    moveUp = false;
                }

                row += moveUp ? -scanStep : scanStep;
            }
        });

        sliceLocations.Add(height);
        return sliceLocations;
    }

    private static List<int> NormalizeSliceLocations(IEnumerable<int> rawLocations, int totalHeight)
    {
        var normalized = rawLocations
            .Where(point => point > 0 && point < totalHeight)
            .Distinct()
            .OrderBy(point => point)
            .ToList();

        normalized.Insert(0, 0);

        if (normalized[^1] != totalHeight)
            normalized.Add(totalHeight);

        return normalized;
    }

    private static int ToGray(Rgba32 pixel)
    {
        return (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
    }

    #endregion

    #region Slicing

    private static List<Image<Rgba32>> Slice(Image<Rgba32> combined, List<int> sliceLocations)
    {
        if (sliceLocations.Count < 2)
            return [combined.Clone()];

        var slices = new List<Image<Rgba32>>();
        var maxWidth = combined.Width;

        for (var i = 1; i < sliceLocations.Count; i++)
        {
            var upper = Math.Clamp(sliceLocations[i - 1], 0, combined.Height);
            var lower = Math.Clamp(sliceLocations[i], 0, combined.Height);
            var sliceHeight = lower - upper;

            if (sliceHeight <= 0)
                continue;

            var rect = new Rectangle(0, upper, maxWidth, sliceHeight);
            slices.Add(combined.Clone(ctx => ctx.Crop(rect)));
        }

        if (slices.Count == 0)
            slices.Add(combined.Clone());

        return slices;
    }

    #endregion

    #region Saving

    private async Task SaveSlicesAsync(
        List<Image<Rgba32>> slices,
        string outputDirectory,
        SmartStitchSettings settings,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        var quality = Math.Clamp(settings.LossyQuality, 1, 100);

        for (var i = 0; i < slices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var ext = settings.OutputFormat switch
            {
                ImageFormat.Jpeg => ".jpg",
                ImageFormat.WebP => ".webp",
                _ => ".png"
            };

            var fileName = $"{i + 1:D3}{ext}";
            var filePath = Path.Combine(outputDirectory, fileName);

            switch (settings.OutputFormat)
            {
                case ImageFormat.Jpeg:
                    await slices[i].SaveAsJpegAsync(filePath, new JpegEncoder { Quality = quality }, ct);
                    break;
                case ImageFormat.WebP:
                    await slices[i].SaveAsWebpAsync(filePath, new WebpEncoder
                    {
                        Quality = quality,
                        FileFormat = WebpFileFormatType.Lossy
                    }, ct);
                    break;
                default:
                    await slices[i].SaveAsPngAsync(filePath, new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.DefaultCompression
                    }, ct);
                    break;
            }
        }

        _log?.Debug($"[SmartStitch] {slices.Count} imagem(ns) recortada(s) salva(s) em {outputDirectory}");
    }

    private static void PrepareStagingDirectory(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, true);

        Directory.CreateDirectory(stagingDirectory);
    }

    private static void ReplaceOutputFiles(string[] originalFiles, string stagingDirectory, string outputDirectory)
    {
        var stagedFiles = Directory.GetFiles(stagingDirectory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (stagedFiles.Length == 0)
            throw new InvalidOperationException("Nenhum arquivo foi gerado pelo SmartStitch.");

        var stagedNames = stagedFiles
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stagedPath in stagedFiles)
        {
            var fileName = Path.GetFileName(stagedPath);
            var targetPath = Path.Combine(outputDirectory, fileName);
            File.Move(stagedPath, targetPath, true);
        }

        foreach (var originalPath in originalFiles)
        {
            var fileName = Path.GetFileName(originalPath);
            if (!stagedNames.Contains(fileName))
            {
                try { File.Delete(originalPath); } catch { }
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try { Directory.Delete(path, true); } catch { }
    }

    #endregion

    #region Settings I/O

    public static async Task<SmartStitchSettings> LoadSettingsAsync(ISettingsStore store)
    {
        return new SmartStitchSettings
        {
            Enabled = await store.GetBoolAsync(SmartStitchSettings.KeyEnabled, false),
            SplitHeight = await store.GetIntAsync(SmartStitchSettings.KeySplitHeight, 5000),
            DetectorType = await store.GetEnumAsync(SmartStitchSettings.KeyDetectorType, StitchDetectorType.PixelComparison),
            Sensitivity = Math.Clamp(await store.GetIntAsync(SmartStitchSettings.KeySensitivity, 90), 0, 100),
            ScanStep = Math.Clamp(await store.GetIntAsync(SmartStitchSettings.KeyScanStep, 5), 1, 100),
            IgnorablePixels = Math.Max(0, await store.GetIntAsync(SmartStitchSettings.KeyIgnorablePixels, 0)),
            WidthEnforcement = await store.GetEnumAsync(SmartStitchSettings.KeyWidthEnforcement, StitchWidthEnforcement.None),
            CustomWidth = Math.Max(1, await store.GetIntAsync(SmartStitchSettings.KeyCustomWidth, 720)),
            OutputFormat = await store.GetEnumAsync(SmartStitchSettings.KeyOutputFormat, ImageFormat.Png),
            LossyQuality = Math.Clamp(await store.GetIntAsync(SmartStitchSettings.KeyLossyQuality, 100), 1, 100)
        };
    }

    public static async Task SaveSettingsAsync(ISettingsStore store, SmartStitchSettings settings)
    {
        await store.SetBoolAsync(SmartStitchSettings.KeyEnabled, settings.Enabled);
        await store.SetIntAsync(SmartStitchSettings.KeySplitHeight, settings.SplitHeight);
        await store.SetEnumAsync(SmartStitchSettings.KeyDetectorType, settings.DetectorType);
        await store.SetIntAsync(SmartStitchSettings.KeySensitivity, settings.Sensitivity);
        await store.SetIntAsync(SmartStitchSettings.KeyScanStep, settings.ScanStep);
        await store.SetIntAsync(SmartStitchSettings.KeyIgnorablePixels, settings.IgnorablePixels);
        await store.SetEnumAsync(SmartStitchSettings.KeyWidthEnforcement, settings.WidthEnforcement);
        await store.SetIntAsync(SmartStitchSettings.KeyCustomWidth, settings.CustomWidth);
        await store.SetEnumAsync(SmartStitchSettings.KeyOutputFormat, settings.OutputFormat);
        await store.SetIntAsync(SmartStitchSettings.KeyLossyQuality, settings.LossyQuality);
    }

    #endregion
}
