namespace NekoSharp.Core.Interfaces;

public interface IRenderedPageFallbackProvider
{
    bool ShouldUseRenderedPageFallback(string imageUrl);

    Task<bool> TryWriteRenderedPageAsync(
        string chapterUrl,
        int pageNumber,
        string imageUrl,
        Stream destination,
        CancellationToken ct = default);
}
