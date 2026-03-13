using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly ConcurrentDictionary<string, DateTime> FailedChallengeCooldownUntilUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> BrowserTransportPreferredHosts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, BrowserTransportSession> BrowserTransportSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ResolvedChallengeUrl> ResolvedChallengeUrls =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan FailedChallengeCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResolvedChallengeUrlLifetime = TimeSpan.FromMinutes(10);

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
        var originalUri = request.RequestUri;
        if (originalUri is not null && request.Method == HttpMethod.Get)
        {
            var resolvedChallengeUri = TryGetResolvedChallengeUri(originalUri);
            if (resolvedChallengeUri is not null && !UrisMatch(originalUri, resolvedChallengeUri))
            {
                _log?.Debug($"[Cloudflare] Reusing resolved challenge URL for {originalUri} -> {resolvedChallengeUri}");
                request.RequestUri = resolvedChallengeUri;
            }
        }

        var host = request.RequestUri?.Host;
        CloudflareCredentials? storedCreds = null;

         
        if (host is not null)
        {
            storedCreds = await _store.TryGetAsync(host);
            if (storedCreds is not null)
            {
                if (BrowserTransportPreferredHosts.ContainsKey(host) &&
                    request.Method == HttpMethod.Get &&
                    IsBrowserTransportCandidate(request))
                {
                    var browserFirst = await TrySendWithBrowserTransportAsync(request, host, storedCreds, ct);
                    if (browserFirst is { IsSuccessStatusCode: true })
                    {
                        _log?.Info($"[Cloudflare] Using browser-backed transport for {request.RequestUri}");
                        return browserFirst;
                    }

                    _log?.Warn($"[Cloudflare] Browser-backed transport for {host} failed. Falling back to HTTP transport.");
                    BrowserTransportPreferredHosts.TryRemove(host, out _);
                    DisposeBrowserTransportSession(host);
                    browserFirst?.Dispose();
                }

                _log?.Debug($"[Cloudflare] Injecting saved credentials for {host}");
                InjectCredentials(request, storedCreds);
            }
        }

        var response = await base.SendAsync(request, ct);

        if (storedCreds is not null && host is not null &&
            ShouldInvalidateStoredCredentialsAfterSuccess(response, originalUri))
        {
            _log?.Warn($"[Cloudflare] Suspicious redirect/response for {host} with stored credentials. Invalidating and retrying naked.");
            await _store.RemoveAsync(host);
            ForgetResolvedChallengeUrl(originalUri);
            BrowserTransportPreferredHosts.TryRemove(host, out _);
            DisposeBrowserTransportSession(host);
            response.Dispose();

            var retryNaked = await CloneRequestAsync(request);
            if (originalUri is not null)
                retryNaked.RequestUri = originalUri;
            retryNaked.Headers.Remove("Cookie");
            retryNaked.Headers.Remove("User-Agent");

            response = await base.SendAsync(retryNaked, ct);
            storedCreds = null;
        }

         
         
         
         
        if (!response.IsSuccessStatusCode && host is not null)
        {
             
            if (storedCreds != null)
            {
                var code = (int)response.StatusCode;
                 
                 
                 
                if (code is 403 or 404 or 429 or 503 or 520 or 522 or 524)
                {
                    bool isChallenge = await IsCloudflareChallenge(response, ct);
                    if (!isChallenge)
                    {
                        HttpResponseMessage? browserFallback = null;
                        if (IsBrowserTransportCandidate(request))
                            browserFallback = await TrySendWithBrowserTransportAsync(request, host, storedCreds, ct);
                        if (browserFallback is { IsSuccessStatusCode: true })
                        {
                            _log?.Info($"[Cloudflare] Browser-backed fallback succeeded for {request.RequestUri}");
                            BrowserTransportPreferredHosts[host] = 0;
                            response.Dispose();
                            return browserFallback;
                        }

                        browserFallback?.Dispose();
                        _log?.Warn($"[Cloudflare] Request to {host} failed ({code}) with stored credentials but was NOT a standard challenge. Invalidating credentials.");
                        await _store.RemoveAsync(host);
                        ForgetResolvedChallengeUrl(originalUri);
                        BrowserTransportPreferredHosts.TryRemove(host, out _);
                        DisposeBrowserTransportSession(host);
                        
                         
                         
                        response.Dispose();
                        var retryNaked = await CloneRequestAsync(request);
                        if (originalUri is not null)
                            retryNaked.RequestUri = originalUri;
                         
                        retryNaked.Headers.Remove("Cookie");
                        retryNaked.Headers.Remove("User-Agent");
                        
                         
                        response = await base.SendAsync(retryNaked, ct);
                    }
                }
            }
        }

        if (!await IsCloudflareChallenge(response, ct))
            return response;

        if (originalUri is not null && request.RequestUri is not null && !UrisMatch(originalUri, request.RequestUri))
            ForgetResolvedChallengeUrl(originalUri);

        if (host is not null &&
            FailedChallengeCooldownUntilUtc.TryGetValue(host, out var cooldownUntilUtc) &&
            DateTime.UtcNow < cooldownUntilUtc)
        {
            var waitSeconds = (int)Math.Ceiling((cooldownUntilUtc - DateTime.UtcNow).TotalSeconds);
            _log?.Warn($"[Cloudflare] Challenge bypass for {host} is cooling down for {waitSeconds}s after previous failure.");
            return response;
        }

        _log?.Warn($"[Cloudflare] Challenge detected for {request.RequestUri}");
        _log?.Info("[Cloudflare] Opening Chrome via CDP — please solve the CAPTCHA if prompted…");

        var uri = originalUri ?? request.RequestUri!;

         
        await BrowserLock.WaitAsync(ct);
        try
        {
             
            if (host is not null)
            {
                var fresh = await _store.TryGetAsync(host);
                if (fresh is not null && IsNewerCredentialSet(storedCreds, fresh))
                {
                    _log?.Info("[Cloudflare] Another request already solved the challenge. Reusing.");
                }
                else
                {
                    await _store.RemoveAsync(host);
                    ForgetResolvedChallengeUrl(originalUri);
                    BrowserTransportPreferredHosts.TryRemove(host, out _);
                    DisposeBrowserTransportSession(host);

                    var solveResult = await SolveWithBrowserAsync(uri.ToString(), ct);
                    if (solveResult is null)
                    {
                        if (host is not null)
                            FailedChallengeCooldownUntilUtc[host] = DateTime.UtcNow.Add(FailedChallengeCooldown);

                        _log?.Error("[Cloudflare] Bypass failed — returning original response.");
                        return response;
                    }

                    var mergedCreds = MergeCredentials(fresh ?? storedCreds, solveResult.Credentials);
                    await _store.SaveAsync(mergedCreds);
                    RememberResolvedChallengeUrl(uri, solveResult.FinalUrl);
                    if (host is not null)
                        FailedChallengeCooldownUntilUtc.TryRemove(host, out _);

                    _log?.Info($"[Cloudflare] Bypass succeeded for {uri.Host}. Credentials saved to DB.");

                    if (IsBrowserTransportCandidate(request) && !string.IsNullOrWhiteSpace(solveResult.HtmlContent))
                    {
                        response.Dispose();
                        return CreateDocumentResponse(request, solveResult.HtmlContent);
                    }
                }
            }
        }
        finally
        {
            BrowserLock.Release();
        }

         
        response.Dispose();
        var retry = await CloneRequestAsync(request);
        if (originalUri is not null)
            retry.RequestUri = TryGetResolvedChallengeUri(originalUri) ?? originalUri;
        var savedCreds = await _store.TryGetAsync(uri.Host);
        if (savedCreds is not null)
        {
            _log?.Debug($"[Cloudflare] Retrying {uri} with saved credentials.");
            InjectCredentials(retry, savedCreds);

            if (BrowserTransportPreferredHosts.ContainsKey(uri.Host) &&
                retry.Method == HttpMethod.Get &&
                IsBrowserTransportCandidate(retry))
            {
                var browserFirst = await TrySendWithBrowserTransportAsync(retry, uri.Host, savedCreds, ct);
                if (browserFirst is { IsSuccessStatusCode: true })
                {
                    _log?.Info($"[Cloudflare] Browser-backed retry succeeded for {uri}");
                    return browserFirst;
                }

                _log?.Warn($"[Cloudflare] Browser-backed retry for {uri.Host} failed. Falling back to HTTP transport.");
                BrowserTransportPreferredHosts.TryRemove(uri.Host, out _);
                DisposeBrowserTransportSession(uri.Host);
                browserFirst?.Dispose();
            }
        }
        else
        {
            _log?.Warn("[Cloudflare] No saved credentials found for retry — request might fail.");
        }
        var retryResponse = await base.SendAsync(retry, ct);

        if (savedCreds is not null &&
            !retryResponse.IsSuccessStatusCode &&
            IsBrowserTransportCandidate(retry))
        {
            var browserFallback = await TrySendWithBrowserTransportAsync(retry, uri.Host, savedCreds, ct);
            if (browserFallback is { IsSuccessStatusCode: true })
            {
                _log?.Info($"[Cloudflare] Browser-backed fallback succeeded for {uri}");
                BrowserTransportPreferredHosts[uri.Host] = 0;
                retryResponse.Dispose();
                return browserFallback;
            }

            browserFallback?.Dispose();
        }

        return retryResponse;
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

     
     
     
     

    private async Task<BrowserSolveResult?> SolveWithBrowserAsync(
        string targetUrl, CancellationToken ct)
    {
        IBrowser? browser = null;
        var keepTempProfile = false;
        string? tempProfile = null;

        try
        {
            var chromePath = FindSystemChrome();
            if (chromePath is null)
            {
                _log?.Error("[Cloudflare] No Chrome/Chromium found on the system! Install Google Chrome or Chromium.");
                return null;
            }

            _log?.Info($"[Cloudflare] Launching Chrome: {chromePath}");

             
            tempProfile = Path.Combine(Path.GetTempPath(), "nekosharp-cf-" + Guid.NewGuid().ToString("N")[..8]);
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

                await ApplyStealthAsync(page);

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
                string? solvedHtmlContent = null;

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
                        try
                        {
                            solvedHtmlContent = await page.GetContentAsync();
                        }
                        catch
                        {
                            solvedHtmlContent = content;
                        }
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

                if (IsLikelyDocumentUrl(targetUrl))
                {
                    var confirmedUrl = page.Url;
                    if (string.IsNullOrWhiteSpace(confirmedUrl))
                        confirmedUrl = targetUrl;

                    try
                    {
                        _log?.Debug($"[Cloudflare] Reloading solved document to capture final HTML: {confirmedUrl}");
                        await page.GoToAsync(confirmedUrl, new NavigationOptions
                        {
                            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2],
                            Timeout = 30_000,
                        });

                        await Task.Delay(500, ct);
                        solvedHtmlContent = await page.GetContentAsync();
                    }
                    catch (NavigationException)
                    {
                        _log?.Warn("[Cloudflare] Follow-up document navigation timed out after challenge. Keeping current page HTML.");
                    }
                }

                 

                var uri = new Uri(targetUrl);
                var host = uri.Host;

                var allCookies = await CollectCookiesAsync(page, browser, uri);
                var hasCfClearance = allCookies.ContainsKey("cf_clearance");
                _log?.Info($"[Cloudflare] Extracted {allCookies.Count} cookies. cf_clearance present: {hasCfClearance}");

                if (!hasCfClearance)
                {
                    _log?.Warn("[Cloudflare] cf_clearance still not found. Bypass might not work.");
                }

                 
                var userAgent = await GetBrowserUserAgentAsync(page, browser);
                _log?.Debug($"[Cloudflare] User-Agent: {userAgent}");

                var creds = new CloudflareCredentials
                {
                    Domain = host,
                    UserAgent = userAgent,
                    AllCookies = allCookies,
                    ObtainedAtUtc = DateTime.UtcNow,
                };

                var htmlContent = solvedHtmlContent ?? string.Empty;

                BrowserTransportSessions.AddOrUpdate(
                    host,
                    _ => new BrowserTransportSession
                    {
                        Host = host,
                        UserDataDir = tempProfile,
                        UserAgent = userAgent,
                        LastUsedUtc = DateTime.UtcNow,
                    },
                    (_, session) =>
                    {
                        session.LastUsedUtc = DateTime.UtcNow;
                        return session;
                    });
                keepTempProfile = true;

                 
                _log?.Info("[Cloudflare] Closing browser…");
                try { await browser.CloseAsync(); } catch {   }
                browser = null;

                return new BrowserSolveResult
                {
                    Credentials = creds,
                    HtmlContent = htmlContent,
                    FinalUrl = page.Url,
                };
            }
            finally
            {
                 
                try
                {
                    if (!keepTempProfile && Directory.Exists(tempProfile))
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

    private async Task<HttpResponseMessage?> TrySendWithBrowserTransportAsync(
        HttpRequestMessage request,
        string host,
        CloudflareCredentials creds,
        CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return null;

        await BrowserLock.WaitAsync(ct);
        try
        {
            return await SendDocumentRequestWithBrowserAsync(request, host, creds, ct);
        }
        finally
        {
            BrowserLock.Release();
        }
    }

    private async Task<HttpResponseMessage?> SendDocumentRequestWithBrowserAsync(
        HttpRequestMessage request,
        string host,
        CloudflareCredentials creds,
        CancellationToken ct)
    {
        var requestUri = request.RequestUri;
        if (requestUri is null)
            return null;

        IBrowser? browser = null;
        string? tempProfile = null;
        var deleteTempProfile = false;

        try
        {
            var chromePath = FindSystemChrome();
            if (chromePath is null)
            {
                _log?.Error("[Cloudflare] Browser-backed fallback unavailable: no Chrome/Chromium found.");
                return null;
            }

            if (BrowserTransportSessions.TryGetValue(host, out var session))
            {
                if (session.IsExpired || !Directory.Exists(session.UserDataDir))
                {
                    DisposeBrowserTransportSession(host);
                    session = null;
                }
                else
                {
                    tempProfile = session.UserDataDir;
                    session.LastUsedUtc = DateTime.UtcNow;
                }
            }

            if (string.IsNullOrWhiteSpace(tempProfile))
            {
                tempProfile = Path.Combine(Path.GetTempPath(), "nekosharp-cf-fetch-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempProfile);
                deleteTempProfile = true;
            }

            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = chromePath,
                UserDataDir = tempProfile,
                Args =
                [
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--start-minimized",
                    "--window-size=1280,850",
                ],
                IgnoredDefaultArgs = ["--enable-automation"],
                DefaultViewport = null,
            });

            var pages = await browser.PagesAsync();
            var page = pages.Length > 0 ? pages[0] : await browser.NewPageAsync();

            await ApplyStealthAsync(page);

            var metadata = TryBuildUserAgentMetadata(creds.UserAgent);
            if (metadata is null)
                await page.SetUserAgentAsync(creds.UserAgent);
            else
                await page.SetUserAgentAsync(creds.UserAgent, metadata);

            var extraHeaders = BuildBrowserExtraHeaders(request);
            if (extraHeaders.Count > 0)
                await page.SetExtraHttpHeadersAsync(extraHeaders);

            var cookieOrigin = requestUri.GetLeftPart(UriPartial.Authority);
            var cookies = creds.AllCookies
                .Where(static kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Select(kv => new CookieParam
                {
                    Name = kv.Key,
                    Value = kv.Value,
                    Url = cookieOrigin,
                    Path = "/",
                    Secure = requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                })
                .ToArray();

            if (cookies.Length > 0)
            {
                await page.SetCookieAsync(cookies);
            }

            _log?.Info($"[Cloudflare] Browser-backed request for {requestUri}");

            var referer = request.Headers.Referrer?.ToString();
            if (string.IsNullOrWhiteSpace(referer) &&
                request.Headers.TryGetValues("Referer", out var refererValues))
            {
                referer = refererValues.FirstOrDefault();
            }

            var browserResponse = await page.GoToAsync(requestUri.ToString(), new NavigationOptions
            {
                Timeout = 30_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Referer = referer,
                CancellationToken = ct,
            });

            if (browserResponse is null)
                return null;

            var contentType = browserResponse.Headers.TryGetValue("content-type", out var ctHeader)
                ? ctHeader
                : "text/html; charset=utf-8";

            byte[] body;
            if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            {
                body = Encoding.UTF8.GetBytes(await browserResponse.TextAsync());
            }
            else
            {
                body = await browserResponse.BufferAsync();
            }

            var response = new HttpResponseMessage((HttpStatusCode)browserResponse.Status)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = await CloneRequestAsync(request),
                ReasonPhrase = browserResponse.StatusText,
            };

            foreach (var header in browserResponse.Headers)
            {
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var refreshedCookies = await CollectCookiesAsync(page, browser, requestUri);
            if (refreshedCookies.Count > 0)
            {
                await _store.SaveAsync(MergeCredentials(
                    creds,
                    new CloudflareCredentials
                    {
                        Domain = creds.Domain,
                        UserAgent = creds.UserAgent,
                        AllCookies = refreshedCookies,
                        ObtainedAtUtc = DateTime.UtcNow,
                    }));
            }

            if (response.IsSuccessStatusCode)
                RememberResolvedChallengeUrl(requestUri, page.Url);

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn($"[Cloudflare] Browser-backed fallback failed for {request.RequestUri}: {ex.Message}");
            return null;
        }
        finally
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync(); } catch {   }
            }

            if (!string.IsNullOrWhiteSpace(tempProfile))
            {
                try
                {
                    if (deleteTempProfile && Directory.Exists(tempProfile))
                        Directory.Delete(tempProfile, true);
                }
                catch {   }
            }
        }
    }

    private static HttpResponseMessage CreateDocumentResponse(HttpRequestMessage request, string htmlContent)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(htmlContent, Encoding.UTF8, "text/html"),
            RequestMessage = request,
        };
    }

    private async Task<Dictionary<string, string>> CollectCookiesAsync(IPage page, IBrowser browser, Uri uri)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var documentCookie = await page.EvaluateExpressionAsync<string>("document.cookie");
            MergeDocumentCookies(documentCookie, cookies);
            if (cookies.Count > 0)
                _log?.Debug($"[Cloudflare] Cookies via document.cookie: {cookies.Count}");
        }
        catch (Exception ex)
        {
            _log?.Debug($"[Cloudflare] Failed to read document.cookie: {ex.Message}");
        }

        if (cookies.ContainsKey("cf_clearance"))
            return cookies;

        _log?.Warn("[Cloudflare] cf_clearance not visible via document.cookie. Querying browser cookie store via CDP…");

        try
        {
            var session = await browser.CreateCDPSessionAsync();
            try
            {
                var result = await session.SendAsync<JsonElement>("Storage.getCookies");
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("cookies", out var cookiesElement) &&
                    cookiesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cookiesElement.EnumerateArray())
                    {
                        if (!TryGetString(item, "name", out var name) ||
                            !TryGetString(item, "value", out var value))
                            continue;

                        var domain = TryGetString(item, "domain", out var parsedDomain)
                            ? parsedDomain
                            : string.Empty;

                        if (!IsCookieForHost(domain, uri.Host))
                            continue;

                        cookies[name] = value;
                        _log?.Debug($"  CDP: {name} = {(value.Length > 20 ? value[..20] + "…" : value)} (domain: {domain})");
                    }
                }
            }
            finally
            {
                try { await session.DetachAsync(); } catch {   }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"[Cloudflare] Failed to query browser cookies via CDP: {ex.Message}");
        }

        return cookies;
    }

    private async Task<string> GetBrowserUserAgentAsync(IPage page, IBrowser browser)
    {
        try
        {
            var session = await browser.CreateCDPSessionAsync();
            try
            {
                var result = await session.SendAsync<JsonElement>("Browser.getVersion");
                if (result.ValueKind == JsonValueKind.Object &&
                    TryGetString(result, "userAgent", out var userAgent) &&
                    !string.IsNullOrWhiteSpace(userAgent))
                {
                    return userAgent;
                }
            }
            finally
            {
                try { await session.DetachAsync(); } catch {   }
            }
        }
        catch (Exception ex)
        {
            _log?.Debug($"[Cloudflare] Failed to query browser user-agent via CDP: {ex.Message}");
        }

        return await page.EvaluateExpressionAsync<string>("navigator.userAgent");
    }

    private static async Task ApplyStealthAsync(IPage page)
    {
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
    }

    private static bool IsBrowserTransportCandidate(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return false;

        var accept = string.Join(", ", request.Headers.Accept.Select(static a => a.MediaType));
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
            accept.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = request.RequestUri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path.EndsWith('/'))
            return true;

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension);
    }

    private static bool IsNewerCredentialSet(CloudflareCredentials? current, CloudflareCredentials candidate)
    {
        if (current is null)
            return true;

        return candidate.ObtainedAtUtc > current.ObtainedAtUtc;
    }

    private static CloudflareCredentials MergeCredentials(
        CloudflareCredentials? baseline,
        CloudflareCredentials incoming)
    {
        var mergedCookies = baseline is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(baseline.AllCookies, StringComparer.Ordinal);

        foreach (var cookie in incoming.AllCookies)
            mergedCookies[cookie.Key] = cookie.Value;

        return new CloudflareCredentials
        {
            Domain = !string.IsNullOrWhiteSpace(incoming.Domain)
                ? incoming.Domain
                : baseline?.Domain ?? string.Empty,
            UserAgent = !string.IsNullOrWhiteSpace(incoming.UserAgent)
                ? incoming.UserAgent
                : baseline?.UserAgent ?? string.Empty,
            AllCookies = mergedCookies,
            ObtainedAtUtc = DateTime.UtcNow,
        };
    }

    private static void DisposeBrowserTransportSession(string host)
    {
        if (!BrowserTransportSessions.TryRemove(host, out var session))
            return;

        try
        {
            if (Directory.Exists(session.UserDataDir))
                Directory.Delete(session.UserDataDir, true);
        }
        catch {   }
    }

    private static void MergeDocumentCookies(string? cookieHeader, Dictionary<string, string> target)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return;

        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var name = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            target[name] = value;
        }
    }

    private static Dictionary<string, string> BuildBrowserExtraHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (request.Headers.TryGetValues("Accept", out var acceptValues))
            headers["Accept"] = string.Join(", ", acceptValues);

        if (request.Headers.TryGetValues("Accept-Language", out var languageValues))
            headers["Accept-Language"] = string.Join(", ", languageValues);

        if (request.Headers.TryGetValues("Sec-GPC", out var secGpcValues))
            headers["Sec-GPC"] = string.Join(", ", secGpcValues);

        return headers;
    }

    private static bool IsCookieForHost(string? cookieDomain, string host)
    {
        if (string.IsNullOrWhiteSpace(cookieDomain))
            return false;

        var normalizedDomain = cookieDomain.Trim().TrimStart('.');
        return host.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind is not JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static UserAgentMetadata? TryBuildUserAgentMetadata(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        var chromeMatch = Regex.Match(userAgent, @"Chrome/(?<version>\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
        if (!chromeMatch.Success)
            return null;

        var fullVersion = chromeMatch.Groups["version"].Value;
        var majorVersion = fullVersion.Split('.', 2)[0];

        var platform = userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            ? "Windows"
            : userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
                ? "Android"
                : userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                    ? "iOS"
                    : userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                        ? "macOS"
                        : userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                            ? "Linux"
                            : string.Empty;

        var architecture =
            userAgent.Contains("arm", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
                ? "arm"
                : "x86";

        var bitness =
            userAgent.Contains("WOW64", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Win64", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("x64", StringComparison.OrdinalIgnoreCase)
                ? "64"
                : "32";

        return new UserAgentMetadata
        {
            Brands =
            [
                new UserAgentBrandVersion { Brand = "Google Chrome", Version = majorVersion },
                new UserAgentBrandVersion { Brand = "Chromium", Version = majorVersion },
                new UserAgentBrandVersion { Brand = "Not?A_Brand", Version = "8" },
            ],
            FullVersion = fullVersion,
            Platform = platform,
            PlatformVersion = string.Empty,
            Architecture = architecture,
            Model = string.Empty,
            Bitness = bitness,
            Mobile = userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase),
            Wow64 = userAgent.Contains("WOW64", StringComparison.OrdinalIgnoreCase),
        };
    }

    private sealed class BrowserSolveResult
    {
        public required CloudflareCredentials Credentials { get; init; }
        public string? HtmlContent { get; init; }
        public string? FinalUrl { get; init; }
    }

    private sealed class BrowserTransportSession
    {
        public required string Host { get; init; }
        public required string UserDataDir { get; init; }
        public required string UserAgent { get; init; }
        public DateTime LastUsedUtc { get; set; }
        public bool IsExpired => DateTime.UtcNow - LastUsedUtc > TimeSpan.FromMinutes(25);
    }

    private sealed class ResolvedChallengeUrl
    {
        public required string ResolvedUrl { get; init; }
        public DateTime ObtainedAtUtc { get; init; }
        public bool IsExpired => DateTime.UtcNow - ObtainedAtUtc > ResolvedChallengeUrlLifetime;
    }

     
     
     

    private void RememberResolvedChallengeUrl(Uri originalUri, string? finalUrl)
    {
        if (string.IsNullOrWhiteSpace(finalUrl) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var resolvedUri))
            return;

        if (!originalUri.Host.Equals(resolvedUri.Host, StringComparison.OrdinalIgnoreCase))
            return;

        var key = originalUri.AbsoluteUri;
        if (UrisMatch(originalUri, resolvedUri))
        {
            ResolvedChallengeUrls.TryRemove(key, out _);
            return;
        }

        ResolvedChallengeUrls[key] = new ResolvedChallengeUrl
        {
            ResolvedUrl = resolvedUri.AbsoluteUri,
            ObtainedAtUtc = DateTime.UtcNow,
        };

        _log?.Debug($"[Cloudflare] Remembered resolved challenge URL for {originalUri} -> {resolvedUri}");
    }

    private void ForgetResolvedChallengeUrl(Uri? originalUri)
    {
        if (originalUri is null)
            return;

        if (ResolvedChallengeUrls.TryRemove(originalUri.AbsoluteUri, out _))
            _log?.Debug($"[Cloudflare] Cleared resolved challenge URL for {originalUri}");
    }

    private static Uri? TryGetResolvedChallengeUri(Uri originalUri)
    {
        if (!ResolvedChallengeUrls.TryGetValue(originalUri.AbsoluteUri, out var resolved))
            return null;

        if (resolved.IsExpired || !Uri.TryCreate(resolved.ResolvedUrl, UriKind.Absolute, out var resolvedUri))
        {
            ResolvedChallengeUrls.TryRemove(originalUri.AbsoluteUri, out _);
            return null;
        }

        return resolvedUri;
    }

    private static bool UrisMatch(Uri left, Uri right)
    {
        return string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
    }

    private static bool IsLikelyDocumentUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path.EndsWith('/'))
            return true;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
            return true;

        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".php", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".asp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase);
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

    private static bool ShouldInvalidateStoredCredentialsAfterSuccess(HttpResponseMessage response, Uri? originalUri)
    {
        if (!response.IsSuccessStatusCode || originalUri is null)
            return false;

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null)
            return false;

        if (IsTrivialCanonicalRedirect(originalUri, finalUri))
            return false;

        if (!IsLikelyDocumentContent(response))
            return false;

        return true;
    }

    private static bool IsTrivialCanonicalRedirect(Uri originalUri, Uri finalUri)
    {
        if (!originalUri.Host.Equals(finalUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var originalPath = NormalizePath(originalUri.AbsolutePath);
        var finalPath = NormalizePath(finalUri.AbsolutePath);

        return originalPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        normalized = normalized.TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
    }

    private static bool IsLikelyDocumentContent(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
            return true;

        return mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);
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
