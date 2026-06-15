namespace NekoSharp.Core.Models;

public sealed record RenderedPageDownload(
    int PageNumber,
    byte[] Bytes,
    string Extension);
