using PuppeteerSharp;
using NekoSharp.Core.Models;

namespace NekoSharp.Core.Services;

 
 
 
 
 
 
 
 
 
 
 
 
 
 
public class CloudflareHandler : DelegatingHandler
{
    private static readonly string[] ChallengeMarkers =
    [
        "Just a moment...",
        "Um momento…",
        "Attention Required! | Cloudflare",
        "Enable JavaScript and cookies to continue",
    ];

    private static readonly string[] TimeoutMarkers =
    [
        "Gateway time-out",
        "Bad gateway",
    ];

    private readonly LogService? _log;
    private readonly CloudflareCredentialStore _store;
    private readonly TimeSpan _challengeTimeout;
    private readonly TimeSpan _pollInterval;

    private static readonly SemaphoreSlim BrowserLock = new(1, 1);

    public CloudflareHandler(
        HttpMessageHandler? inner = null,
        LogService? logService = null,
        CloudflareCredentialStore? store = null,
        int challengeTimeoutSeconds = 180,
        int pollIntervalMs = 2000)
        : base(inner ?? new HttpClientHandler())
    {
        _log = logService;
        _store = store ?? new CloudflareCredentialStore(logService: logService);
        _challengeTimeout = TimeSpan.FromSeconds(challengeTimeoutSeconds);
        _pollInterval = TimeSpan.FromMilliseconds(pollIntervalMs);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host;

         
        if (host is not null)
        {
            var stored = await _store.TryGetAsync(host);
            if (stored is not null)
            {
                _log?.Debug($"[Cloudflare] Injecting saved credentials for {host}");
                InjectCredentials(request, stored);
            }
        }

        var response = await base.SendAsync(request, ct);

         
         
         
         
        if (!response.IsSuccessStatusCode && host is not null)
        {
             
            var hadStored = await _store.TryGetAsync(host);
            if (hadStored != null)
            {
                var code = (int)response.StatusCode;
                 
                 
                 
                if (code is 403 or 503 or 404 or 520 or 522 or 524)
                {
                    bool isChallenge = await IsCloudflareChallenge(response, ct);
                    if (!isChallenge)
                    {
                        _log?.Warn($"[Cloudflare] Request to {host} failed ({code}) with stored credentials but was NOT a standard challenge. Invalidating credentials.");
                        await _store.RemoveAsync(host);
                        
                         
                         
                        response.Dispose();
                        var retryNaked = await CloneRequestAsync(request);
                         
                        retryNaked.Headers.Remove("Cookie");
                        retryNaked.Headers.Remove("User-Agent");
                        
                         
                        response = await base.SendAsync(retryNaked, ct);
                    }
                }
            }
        }

        if (!await IsCloudflareChallenge(response, ct))
            return response;

        _log?.Warn($"[Cloudflare] Challenge detected for {request.RequestUri}");
        _log?.Info("[Cloudflare] Opening Chrome via CDP — please solve the CAPTCHA if prompted…");

        var uri = request.RequestUri!;

         
        await BrowserLock.WaitAsync(ct);
        try
        {
             
            if (host is not null)
            {
                var fresh = await _store.TryGetAsync(host);
                if (fresh is not null)
                {
                    _log?.Info("[Cloudflare] Another request already solved the challenge. Reusing.");
                }
                else
                {
                    await _store.RemoveAsync(host);

                    var creds = await SolveWithBrowserAsync(uri.ToString(), ct);
                    if (creds is null)
                    {
                        _log?.Error("[Cloudflare] Bypass failed — returning original response.");
                        return response;
                    }

                    await _store.SaveAsync(creds);
                    _log?.Info($"[Cloudflare] Bypass succeeded for {uri.Host}. Credentials saved to DB.");
                }
            }
        }
        finally
        {
            BrowserLock.Release();
        }

         
        response.Dispose();
        var retry = await CloneRequestAsync(request);
        var savedCreds = await _store.TryGetAsync(uri.Host);
        if (savedCreds is not null)
        {
            _log?.Debug($"[Cloudflare] Retrying {uri} with saved credentials.");
            InjectCredentials(retry, savedCreds);
        }
        else
        {
            _log?.Warn("[Cloudflare] No saved credentials found for retry — request might fail.");
        }
        return await base.SendAsync(retry, ct);
    }

     
     
     

    private static async Task<bool> IsCloudflareChallenge(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return false;

        var status = (int)response.StatusCode;
        if (status is not (403 or 503 or 520 or 522 or 524))
            return false;

        var html = await response.Content.ReadAsStringAsync(ct);
        return IsChallengeHtml(html);
    }

    private static bool IsChallengeHtml(string html)
    {
        foreach (var marker in ChallengeMarkers)
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsErrorPage(string html)
    {
        foreach (var marker in TimeoutMarkers)
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

     
     
     
     

    private async Task<CloudflareCredentials?> SolveWithBrowserAsync(
        string targetUrl, CancellationToken ct)
    {
        IBrowser? browser = null;

        try
        {
            var chromePath = FindSystemChrome();
            if (chromePath is null)
            {
                _log?.Error("[Cloudflare] No Chrome/Chromium found on the system! Install Google Chrome or Chromium.");
                return null;
            }

            _log?.Info($"[Cloudflare] Launching Chrome: {chromePath}");

             
            var tempProfile = Path.Combine(Path.GetTempPath(), "nekosharp-cf-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempProfile);

            try
            {
                browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = false,
                    ExecutablePath = chromePath,
                    UserDataDir = tempProfile,
                    Args =
                    [
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-infobars",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--window-size=1280,850",
                    ],
                    IgnoredDefaultArgs = ["--enable-automation"],
                    DefaultViewport = null,    
                });

                var pages = await browser.PagesAsync();
                var page = pages.Length > 0 ? pages[0] : await browser.NewPageAsync();

                 
                 
                await page.EvaluateExpressionOnNewDocumentAsync("""
                     
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    
                     
                    Object.defineProperty(navigator, 'plugins', {
                        get: () => [1, 2, 3, 4, 5]
                    });
                    
                     
                    Object.defineProperty(navigator, 'languages', {
                        get: () => ['pt-BR', 'pt', 'en-US', 'en']
                    });
                    
                     
                    window.chrome = { runtime: {} };
                    
                     
                    const originalQuery = window.navigator.permissions.query;
                    window.navigator.permissions.query = (parameters) => (
                        parameters.name === 'notifications' ?
                            Promise.resolve({ state: Notification.permission }) :
                            originalQuery(parameters)
                    );
                """);

                _log?.Info($"[Cloudflare] Navigating to {targetUrl}");
                try
                {
                    await page.GoToAsync(targetUrl, new NavigationOptions
                    {
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                        Timeout = 30_000,
                    });
                }
                catch (NavigationException)
                {
                    _log?.Warn("[Cloudflare] Navigation timed out (30s), continuing to wait for challenge…");
                }

                 
                var deadline = DateTime.UtcNow + _challengeTimeout;
                var solved = false;

                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();

                    string content;
                    try
                    {
                        content = await page.GetContentAsync();
                    }
                    catch
                    {
                        await Task.Delay(_pollInterval, ct);
                        continue;
                    }

                    if (IsErrorPage(content))
                    {
                        _log?.Warn("[Cloudflare] Server error page detected. Reloading…");
                        try { await page.ReloadAsync(); } catch {   }
                        await Task.Delay(_pollInterval, ct);
                        continue;
                    }

                    if (!IsChallengeHtml(content))
                    {
                        _log?.Info("[Cloudflare] Challenge page cleared! Waiting 3s for cookies to settle…");
                        await Task.Delay(3_000, ct);
                        solved = true;
                        break;
                    }

                    var remaining = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                    _log?.Debug($"[Cloudflare] Still on challenge page… ({remaining}s left)");
                    await Task.Delay(_pollInterval, ct);
                }

                if (!solved)
                {
                    _log?.Error($"[Cloudflare] Timed out after {_challengeTimeout.TotalSeconds}s waiting for challenge.");
                    return null;
                }

                 

                var uri = new Uri(targetUrl);
                var host = uri.Host;

                var cookies = await page.GetCookiesAsync(targetUrl);

                _log?.Debug($"[Cloudflare] Raw cookies from browser ({cookies.Length} total):");
                foreach (var c in cookies)
                    _log?.Debug($"  {c.Name} = {(c.Value.Length > 20 ? c.Value[..20] + "…" : c.Value)} (domain: {c.Domain})");

                var allCookies = new Dictionary<string, string>();
                foreach (var cookie in cookies)
                    allCookies[cookie.Name] = cookie.Value;

                var hasCfClearance = allCookies.ContainsKey("cf_clearance");
                _log?.Info($"[Cloudflare] Extracted {allCookies.Count} cookies. cf_clearance present: {hasCfClearance}");

                if (!hasCfClearance)
                {
                    _log?.Warn("[Cloudflare] cf_clearance NOT found! Fetching all browser cookies via CDP…");
                     
                    var allBrowserCookies = await page.GetCookiesAsync();
                    foreach (var c in allBrowserCookies)
                    {
                        if (host.Contains(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                            || c.Domain.TrimStart('.').Contains(host, StringComparison.OrdinalIgnoreCase))
                        {
                            allCookies[c.Name] = c.Value;
                        }
                        _log?.Debug($"  ALL: {c.Name} = {(c.Value.Length > 20 ? c.Value[..20] + "…" : c.Value)} (domain: {c.Domain})");
                    }
                    hasCfClearance = allCookies.ContainsKey("cf_clearance");
                    _log?.Info($"[Cloudflare] After full scan: {allCookies.Count} cookies. cf_clearance: {hasCfClearance}");
                }

                if (!hasCfClearance)
                {
                    _log?.Warn("[Cloudflare] cf_clearance still not found. Bypass might not work.");
                }

                 
                var userAgent = await page.EvaluateExpressionAsync<string>("navigator.userAgent");
                _log?.Debug($"[Cloudflare] User-Agent: {userAgent}");

                var creds = new CloudflareCredentials
                {
                    Domain = host,
                    UserAgent = userAgent,
                    AllCookies = allCookies,
                    ObtainedAtUtc = DateTime.UtcNow,
                };

                 
                _log?.Info("[Cloudflare] Closing browser…");
                try { await browser.CloseAsync(); } catch {   }
                browser = null;

                return creds;
            }
            finally
            {
                 
                try
                {
                    if (Directory.Exists(tempProfile))
                        Directory.Delete(tempProfile, true);
                }
                catch {   }
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Warn("[Cloudflare] Bypass cancelled by user.");
            return null;
        }
        catch (Exception ex)
        {
            _log?.Error($"[Cloudflare] Browser bypass failed: {ex.Message}", ex.ToString());
            return null;
        }
        finally
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync(); } catch {   }
            }
        }
    }

     
     
     

    private static void InjectCredentials(HttpRequestMessage request, CloudflareCredentials creds)
    {
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", creds.UserAgent);

        var cookieHeader = string.Join("; ", creds.AllCookies.Select(kv => $"{kv.Key}={kv.Value}"));
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
    }

     
     
     

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

     
     
     

    private static string? FindSystemChrome()
    {
        string[] posixCandidates =
        [
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
            "/snap/bin/chromium",
            "/usr/bin/brave-browser",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Chromium.app/Contents/MacOS/Chromium",
            "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
        ];

        var windowsCandidates = new List<string>();
        foreach (var envVar in new[] { "PROGRAMFILES", "PROGRAMFILES(X86)", "LOCALAPPDATA", "PROGRAMW6432" })
        {
            var root = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(root)) continue;

            windowsCandidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            windowsCandidates.Add(Path.Combine(root, "Google", "Chrome Beta", "Application", "chrome.exe"));
            windowsCandidates.Add(Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        }

        var candidates = OperatingSystem.IsWindows() ? windowsCandidates : posixCandidates.ToList();
        return candidates.FirstOrDefault(File.Exists);
    }
}
