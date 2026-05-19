using System.Text.Json.Serialization;

namespace NekoSharp.Core.Providers.Webtoons;

internal sealed class WebtoonsResultDto<T>
{
    [JsonPropertyName("result")]
    public T Result { get; set; } = default!;
}

internal sealed class WebtoonsEpisodeListDto
{
    [JsonPropertyName("episodeList")]
    public List<WebtoonsEpisodeDto> EpisodeList { get; set; } = [];
}

internal sealed class WebtoonsEpisodeDto
{
    [JsonPropertyName("episodeTitle")]
    public string EpisodeTitle { get; set; } = string.Empty;

    [JsonPropertyName("viewerLink")]
    public string ViewerLink { get; set; } = string.Empty;

    [JsonPropertyName("exposureDateMillis")]
    public long ExposureDateMillis { get; set; }

    [JsonPropertyName("hasBgm")]
    public bool HasBgm { get; set; }
}

internal sealed class WebtoonsMotionToonDto
{
    [JsonPropertyName("assets")]
    public WebtoonsMotionToonAssetsDto Assets { get; set; } = new();
}

internal sealed class WebtoonsMotionToonAssetsDto
{
    [JsonPropertyName("images")]
    public Dictionary<string, string> Images { get; set; } = [];
}
