namespace NekoSharp.Core.Models;

public enum StitchDetectorType
{
    None = 0,

    PixelComparison = 1
}

public enum StitchWidthEnforcement
{
    None = 0,

    Automatic = 1,

    Manual = 2
}


public class SmartStitchSettings
{
    public bool Enabled { get; set; }

    public int SplitHeight { get; set; } = 5000;

    public StitchDetectorType DetectorType { get; set; } = StitchDetectorType.PixelComparison;

    public int Sensitivity { get; set; } = 90;

    public int ScanStep { get; set; } = 5;

    public int IgnorablePixels { get; set; }

    public StitchWidthEnforcement WidthEnforcement { get; set; } = StitchWidthEnforcement.None;

    public int CustomWidth { get; set; } = 720;

    public ImageFormat OutputFormat { get; set; } = ImageFormat.Png;

    public int LossyQuality { get; set; } = 100;

    public const string KeyEnabled = "SmartStitch.Enabled";
    public const string KeySplitHeight = "SmartStitch.SplitHeight";
    public const string KeyDetectorType = "SmartStitch.DetectorType";
    public const string KeySensitivity = "SmartStitch.Sensitivity";
    public const string KeyScanStep = "SmartStitch.ScanStep";
    public const string KeyIgnorablePixels = "SmartStitch.IgnorablePixels";
    public const string KeyWidthEnforcement = "SmartStitch.WidthEnforcement";
    public const string KeyCustomWidth = "SmartStitch.CustomWidth";
    public const string KeyOutputFormat = "SmartStitch.OutputFormat";
    public const string KeyLossyQuality = "SmartStitch.LossyQuality";
}
