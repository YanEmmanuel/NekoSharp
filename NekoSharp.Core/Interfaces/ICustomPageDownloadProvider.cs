namespace NekoSharp.Core.Interfaces;

public interface ICustomPageDownloadProvider
{
    IReadOnlyList<string> GetPageDownloadCandidates(string imageUrl);

    void ApplyPageDownloadHeaders(HttpRequestMessage request, string imageUrl);

    Task CopyPageResponseAsync(
        HttpResponseMessage response,
        Stream destination,
        string imageUrl,
        CancellationToken ct = default);
}
