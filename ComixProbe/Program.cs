using NekoSharp.Core.Providers.Comix;
using NekoSharp.Core.Services;
using System.Reflection;

return await ProbeAsync(args);

static async Task<int> ProbeAsync(string[] args)
{
    var url = args.Length > 0
        ? args[0]
        : "https://comix.to/title/my91m-ruthless-s-2-uncensored";

    var log = new LogService();
    var settings = new SettingsStore(logService: log);
    var cfStore = new CloudflareCredentialStore(logService: log);
    var scraper = new ComixScraper(log, cfStore);
    Console.WriteLine($"SCRAPER={scraper.Name}");

    try
    {
        var manga = await scraper.GetMangaInfoAsync(url);
        Console.WriteLine($"MANGA={manga.Name}");
        Console.WriteLine($"COVER={manga.CoverUrl}");
        Console.WriteLine($"DESC_LEN={manga.Description.Length}");

        if (args.Contains("--debug"))
        {
            var parseMethod = typeof(ComixScraper).GetMethod(
                "ParseSupportedUrl",
                BindingFlags.NonPublic | BindingFlags.Static);
            var parsed = parseMethod!.Invoke(null, [url])!;

            var mangaSegment = parsed.GetType().GetProperty("MangaSegment")!.GetValue(parsed)?.ToString();
            var hashId = parsed.GetType().GetProperty("HashId")!.GetValue(parsed)?.ToString();
            var mangaUrl = $"https://comix.to/title/{mangaSegment}";

            await ProbePrivateAsync(
                scraper,
                "FetchChaptersPayloadFromBrowserApiAsync",
                [mangaUrl, hashId!, CancellationToken.None]);

            await ProbePrivateAsync(
                scraper,
                "CaptureBrowserPayloadAsync",
                [
                    mangaUrl,
                    GetConst("ChapterCaptureScript"),
                    "() => window.__nekosharpComixChapterState?.payload || ''",
                    "() => Object.keys(window.__nekosharpComixChapterState?.pages || {}).length",
                    CancellationToken.None
                ]);
        }

        var chapters = await scraper.GetChaptersAsync(url);
        Console.WriteLine($"CHAPTERS={chapters.Count}");
        foreach (var chapter in chapters.Take(5))
            Console.WriteLine($"CH={chapter.Number}|{chapter.Title}|{chapter.Url}");

        var chapter13 = chapters.FirstOrDefault(ch => Math.Abs(ch.Number - 13) < 0.0001);
        if (chapter13 is not null)
        {
            var pages = await scraper.GetPagesAsync(chapter13);
            Console.WriteLine($"PAGES_CH13={pages.Count}");
            Console.WriteLine($"PAGE1={pages.FirstOrDefault()?.ImageUrl}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("EXCEPTION");
        Console.WriteLine(ex.GetType().FullName);
        Console.WriteLine(ex.Message);
        Console.WriteLine(ex);
        return 1;
    }
}

static async Task ProbePrivateAsync(ComixScraper scraper, string methodName, object?[] args)
{
    var method = typeof(ComixScraper).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
    Console.WriteLine($"PROBE={methodName}");
    try
    {
        var task = (Task<string>)method!.Invoke(scraper, args)!;
        var payload = await task;
        Console.WriteLine($"PROBE_OK={methodName}|LEN={payload.Length}");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        Console.WriteLine($"PROBE_FAIL={methodName}|{ex.InnerException.GetType().FullName}|{ex.InnerException.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PROBE_FAIL={methodName}|{ex.GetType().FullName}|{ex.Message}");
    }
}

static string GetConst(string name) =>
    (string)(typeof(ComixScraper).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)
        ?? throw new InvalidOperationException($"Const '{name}' not found."));
