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
        if (!settings.Enabled) return;

        _log?.Info($"[SmartStitch] Starting processing in {inputDirectory}");

        var imageFiles = GetSortedImageFiles(inputDirectory);
        if (imageFiles.Length == 0)
        {
            _log?.Warn("[SmartStitch] No images found, skipping.");
            return;
        }

        _log?.Debug($"[SmartStitch] Found {imageFiles.Length} images to process.");

        var images = await LoadImagesAsync(imageFiles, ct);
        try
        {
            var slices = await Task.Run(() =>
            {
                images = EnforceWidth(images, settings.WidthEnforcement, settings.CustomWidth);

                _log?.Debug("[SmartStitch] Combining images into single strip...");
                var combined = Combine(images);

                foreach (var img in images) img.Dispose();
                images.Clear();

                ct.ThrowIfCancellationRequested();

                _log?.Debug("[SmartStitch] Detecting slice points...");
                var slicePoints = DetectSlicePoints(combined, settings);

                _log?.Info($"[SmartStitch] Found {slicePoints.Count - 1} slices.");

                var result = Slice(combined, slicePoints);
                combined.Dispose();

                ct.ThrowIfCancellationRequested();
                return result;
            }, ct);

            _log?.Debug("[SmartStitch] Saving output images...");
            foreach (var file in imageFiles)
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }

            await SaveSlicesAsync(slices, inputDirectory, settings, ct);

            foreach (var slice in slices) slice.Dispose();

            _log?.Info("[SmartStitch] Processing complete.");
        }
        catch
        {
            foreach (var img in images) img.Dispose();
            throw;
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
            var img = await Image.LoadAsync<Rgba32>(file, ct);
            images.Add(img);
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

        int targetWidth;
        if (enforcement == StitchWidthEnforcement.Automatic)
        {
            targetWidth = images.Min(img => img.Width);
        }
        else 
        {
            targetWidth = Math.Max(1, customWidth);
        }

        _log?.Debug($"[SmartStitch] Enforcing width to {targetWidth}px ({enforcement})");

        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (img.Width == targetWidth) continue;

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
                combined, settings.SplitHeight, settings.Sensitivity,
                settings.ScanStep, settings.IgnorablePixels),
            _ => DetectDirectSlicing(combined.Height, settings.SplitHeight)
        };
    }

    private static List<int> DetectDirectSlicing(int totalHeight, int splitHeight)
    {
        var locations = new List<int> { 0 };
        var row = splitHeight;
        while (row < totalHeight)
        {
            locations.Add(row);
            row += splitHeight;
        }
        if (locations[^1] != totalHeight - 1)
            locations.Add(totalHeight - 1);
        return locations;
    }

   
    private List<int> DetectPixelComparison(
        Image<Rgba32> combined, int splitHeight,
        int sensitivity, int scanStep, int ignorablePixels)
    {
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
            var startX = ignorablePixels + 1;
            var endX = width - ignorablePixels;

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

        if (sliceLocations[^1] != height - 1)
            sliceLocations.Add(height - 1);

        return sliceLocations;
    }

    private static int ToGray(Rgba32 pixel)
    {
        // ITU-R BT.601 luma
        return (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
    }

    #endregion

    #region Slicing

    private static List<Image<Rgba32>> Slice(Image<Rgba32> combined, List<int> sliceLocations)
    {
        var slices = new List<Image<Rgba32>>();
        var maxWidth = combined.Width;

        for (int i = 1; i < sliceLocations.Count; i++)
        {
            var upper = sliceLocations[i - 1];
            var lower = sliceLocations[i];
            var sliceHeight = lower - upper;

            if (sliceHeight <= 0) continue;

            var rect = new Rectangle(0, upper, maxWidth, sliceHeight);
            var slice = combined.Clone(ctx => ctx.Crop(rect));
            slices.Add(slice);
        }

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
        var quality = Math.Clamp(settings.LossyQuality, 1, 100);

        for (int i = 0; i < slices.Count; i++)
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
                default: // PNG
                    await slices[i].SaveAsPngAsync(filePath, new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.DefaultCompression
                    }, ct);
                    break;
            }
        }

        _log?.Debug($"[SmartStitch] Saved {slices.Count} sliced images to {outputDirectory}");
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
