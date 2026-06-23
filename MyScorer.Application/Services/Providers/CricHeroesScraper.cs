using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services.Providers;

/// <summary>
/// Extracts live score data from CricHeroes match pages by parsing the
/// embedded __NEXT_DATA__ JSON payload (server-rendered, no JS execution needed).
/// 
/// Data path: __NEXT_DATA__ → props.pageProps.miniScorecard.data
/// </summary>
public class CricHeroesScraper : IProviderScraper
{
    private enum FetchFailureReason
    {
        None,
        CloudflareBlocked,
        AppShellOrContractChanged
    }

    // Cached Next.js buildId is persisted to disk so it survives app restarts.
    private static readonly string BuildIdCachePath = Path.Combine(AppContext.BaseDirectory, "cricheroes-buildid.txt");
    private static readonly string SessionCookieCachePath = Path.Combine(AppContext.BaseDirectory, "cricheroes-session-cookie.txt");
    private static string? _cachedBuildId = LoadCachedBuildId();
    private static string? _sessionCookieHeader = LoadSessionCookieHeader();
    private static readonly SemaphoreSlim _buildIdLock = new(1, 1);

    // Tracks whether the last FetchNextData call was redirected from /live to /summary (match ended)
    [ThreadStatic] private static bool _lastFetchWasRedirectedToSummary;
    [ThreadStatic] private static FetchFailureReason _lastFetchFailureReason;

    // Dedicated HttpClient for _next/data requests — does NOT follow HTTP redirects
    // because CricHeroes returns JSON __N_REDIRECT responses that we handle manually.
    // If auto-redirect is on, the HTTP client follows 307 to the web page which gets 403 from Cloudflare.
    private static readonly HttpClient _nextDataClient = CreateNextDataClient();
    private const string OfficialApiBase = "https://api.cricheroes.in/api/v1";
    private const string OfficialApiKey = "cr!CkH3r0s";
    private const string OfficialDeviceType = "Chrome: 149.0.0.0";
    private const string OfficialUdid = "33f1796b2918055566db36fd2aa681a4";

    private static HttpClient CreateNextDataClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public string ProviderName => "Cricheroes";

    /// <summary>
    /// Get the currently cached buildId.
    /// </summary>
    public static string? GetCachedBuildId() => _cachedBuildId;

    /// <summary>
    /// Get the configured browser session cookie header used for CricHeroes requests.
    /// </summary>
    public static string? GetSessionCookieHeader() => _sessionCookieHeader;

    /// <summary>
    /// Manually set the buildId (e.g. from admin UI).
    /// </summary>
    public static void SetBuildId(string buildId)
    {
        _cachedBuildId = buildId;
        PersistCachedBuildId(buildId);
    }

    /// <summary>
    /// Set browser session cookie header (example: "cf_clearance=...; _cfuvid=...").
    /// </summary>
    public static void SetSessionCookieHeader(string cookieHeader)
    {
        _sessionCookieHeader = string.IsNullOrWhiteSpace(cookieHeader)
            ? null
            : cookieHeader.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);

        PersistSessionCookieHeader(_sessionCookieHeader);
    }

    /// <summary>
    /// Clear persisted session cookie header.
    /// </summary>
    public static void ClearSessionCookieHeader()
    {
        _sessionCookieHeader = null;
        try
        {
            if (File.Exists(SessionCookieCachePath))
                File.Delete(SessionCookieCachePath);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string? LoadCachedBuildId()
    {
        try
        {
            if (!File.Exists(BuildIdCachePath))
                return null;

            var buildId = File.ReadAllText(BuildIdCachePath).Trim();
            return string.IsNullOrWhiteSpace(buildId) ? null : buildId;
        }
        catch
        {
            return null;
        }
    }

    private static string? LoadSessionCookieHeader()
    {
        try
        {
            if (!File.Exists(SessionCookieCachePath))
                return null;

            var cookieHeader = File.ReadAllText(SessionCookieCachePath).Trim();
            return string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader;
        }
        catch
        {
            return null;
        }
    }

    private static void PersistCachedBuildId(string buildId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BuildIdCachePath) ?? AppContext.BaseDirectory);
            File.WriteAllText(BuildIdCachePath, buildId);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void PersistSessionCookieHeader(string? cookieHeader)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                if (File.Exists(SessionCookieCachePath))
                    File.Delete(SessionCookieCachePath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SessionCookieCachePath) ?? AppContext.BaseDirectory);
            File.WriteAllText(SessionCookieCachePath, cookieHeader);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void ApplySessionCookie(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_sessionCookieHeader))
            return;

        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", _sessionCookieHeader);
    }

    public bool CanHandle(string matchUrl)
    {
        return !string.IsNullOrWhiteSpace(matchUrl) &&
               matchUrl.Contains("cricheroes", StringComparison.OrdinalIgnoreCase);
    }

    public Task<UrlValidationResult> ValidateAsync(string matchUrl, HttpClient httpClient)
    {
        // Validate by URL structure — CricHeroes scorecard URLs follow:
        // https://cricheroes.com/scorecard/{matchId}/{tournamentName}/{teamNames}/{tab}
        // Avoid fetching the page since CricHeroes blocks server-side requests (403).

        if (!Uri.TryCreate(matchUrl, UriKind.Absolute, out var uri))
        {
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "Invalid URL format." });
        }

        if (!uri.Host.Contains("cricheroes.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "URL is not from cricheroes.com." });
        }

        // Must contain /scorecard/ path with a numeric match ID
        var path = uri.AbsolutePath;
        var scorecardMatch = System.Text.RegularExpressions.Regex.IsMatch(path, @"/scorecard/\d+/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!scorecardMatch)
        {
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "URL does not look like a CricHeroes match scorecard. Expected format: cricheroes.com/scorecard/{matchId}/..." });
        }

        return Task.FromResult(new UrlValidationResult { IsValid = true, ProviderType = ProviderName, Message = "Valid CricHeroes match URL." });
    }

    public async Task<LiveScoreData> ExtractAsync(string matchUrl, HttpClient httpClient, bool includeFullScorecard = false)
    {
        var result = new LiveScoreData { ProviderType = ProviderName, MatchUrl = matchUrl };

        try
        {
            _lastFetchFailureReason = FetchFailureReason.None;
            matchUrl = NormalizeLiveUrl(matchUrl);

            var matchId = ExtractMatchId(matchUrl);
            var usedOfficialApi = false;

            // Strategy 0 (primary): CricHeroes official API host used by web/app clients.
            JsonElement? matchData = null;
            if (matchId.HasValue)
            {
                matchData = await TryOfficialMiniScorecardApi(matchId.Value, httpClient);
                if (matchData != null)
                    usedOfficialApi = true;
            }

            // Strategy 1: Try CricHeroes _next/data JSON API (bypasses Cloudflare web protection)
            if (matchData == null)
            {
                _lastFetchWasRedirectedToSummary = false;
                matchData = await TryNextDataApi(matchUrl, httpClient);
            }

            // If /live was redirected to /summary, the match has completed
            if (matchData != null && _lastFetchWasRedirectedToSummary)
            {
                result.MatchStatus = "Completed";
            }

            // Strategy 2: Fall back to page scraping if API didn't work
            if (matchData == null)
            {
                matchData = await TryPageScrape(matchUrl, httpClient);
            }

            // Strategy 3: If /live failed, the match may have ended — try /summary
            if (matchData == null && matchUrl.EndsWith("/live", StringComparison.OrdinalIgnoreCase))
            {
                var summaryUrl = matchUrl[..matchUrl.LastIndexOf('/')] + "/summary";
                matchData = await TryNextDataApi(summaryUrl, httpClient);
                if (matchData == null)
                    matchData = await TryPageScrape(summaryUrl, httpClient);

                // If summary returned data, the match is completed
                if (matchData != null)
                    result.MatchStatus = "Completed";
            }

            if (matchData == null)
            {
                result.ErrorMessage = _lastFetchFailureReason switch
                {
                    FetchFailureReason.CloudflareBlocked =>
                        "Unable to fetch score data from CricHeroes. CricHeroes is returning a Cloudflare challenge page to server-side requests right now, so buildId updates will not help until access is restored.",
                    FetchFailureReason.AppShellOrContractChanged =>
                        "Unable to fetch score data from CricHeroes. CricHeroes is returning an HTML app-shell/404 response for _next/data instead of JSON, so the old buildId-based endpoint appears to have changed.",
                    _ =>
                        "Unable to fetch score data from CricHeroes. The cached buildId may be stale — try updating it via POST /api/admin/cricheroes-buildid."
                };
                return result;
            }

            ParseMatchData(matchData.Value, result);

            if (includeFullScorecard)
            {
                if (usedOfficialApi && matchId.HasValue)
                {
                    var officialScorecardData = await TryOfficialScorecardApi(matchId.Value, httpClient);
                    if (officialScorecardData != null)
                    {
                        ParseOfficialFullScorecard(officialScorecardData.Value, result);
                    }
                }
                else
                {
                    var scorecardUrl = ToScorecardUrl(matchUrl);
                    var pageProps = await TryNextPagePropsApi(scorecardUrl, httpClient);
                    if (pageProps == null)
                    {
                        pageProps = await TryPageScrapePageProps(scorecardUrl, httpClient);
                    }

                    if (pageProps != null)
                    {
                        ParseFullScorecard(pageProps.Value, result);
                    }
                }

                if (!result.IsFullScorecardAvailable)
                {
                    result.FullScorecardNote = "Detailed scorecard unavailable for this CricHeroes match state.";
                }
            }

            result.IsValid = !string.IsNullOrWhiteSpace(result.TeamA);
            result.ScrapedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Extraction failed: {ex.Message}";
        }

        return result;
    }

    private static int? ExtractMatchId(string matchUrl)
    {
        if (string.IsNullOrWhiteSpace(matchUrl))
            return null;

        var m = Regex.Match(matchUrl, @"/scorecard/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;

        return int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private static void ApplyOfficialApiHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("api-key", OfficialApiKey);
        request.Headers.Add("device-type", OfficialDeviceType);
        request.Headers.Add("origin", "https://cricheroes.com");
        request.Headers.Add("referer", "https://cricheroes.com/");
        request.Headers.Add("udid", OfficialUdid);
    }

    private static async Task<JsonElement?> TryOfficialMiniScorecardApi(int matchId, HttpClient httpClient)
    {
        try
        {
            var url = $"{OfficialApiBase}/scorecard/get-mini-scorecard/{matchId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyOfficialApiHeaders(request);
            ApplySessionCookie(request);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
                return null;

            if (!root.TryGetProperty("data", out var data))
                return null;

            return JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonElement?> TryOfficialScorecardApi(int matchId, HttpClient httpClient)
    {
        try
        {
            var url = $"{OfficialApiBase}/scorecard/get-scorecard/{matchId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyOfficialApiHeaders(request);
            ApplySessionCookie(request);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
                return null;

            if (!root.TryGetProperty("data", out var data))
                return null;

            return JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Try the Next.js _next/data API endpoint which returns pure JSON.
    /// This endpoint bypasses Cloudflare web protection.
    /// URL pattern: /_next/data/{buildId}/scorecard/{matchId}/{tournament}/{teams}/live.json
    /// </summary>
    private async Task<JsonElement?> TryNextDataApi(string matchUrl, HttpClient httpClient)
    {
        try
        {
            var uri = new Uri(matchUrl);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            if (pathParts.Length < 3 || !pathParts[0].Equals("scorecard", StringComparison.OrdinalIgnoreCase))
                return null;

            var pathSegment = string.Join("/", pathParts);

            // Try with cached buildId first
            var buildId = _cachedBuildId;
            if (!string.IsNullOrWhiteSpace(buildId))
            {
                var result = await FetchNextData(buildId, pathSegment, matchUrl, httpClient);
                if (result != null)
                    return result;

                // FetchNextData may have auto-discovered a new buildId from the error page
                var newBuildId = _cachedBuildId;
                if (!string.IsNullOrWhiteSpace(newBuildId) && newBuildId != buildId)
                {
                    // Retry with the auto-discovered buildId
                    result = await FetchNextData(newBuildId, pathSegment, matchUrl, httpClient);
                    if (result != null)
                        return result;
                }
            }

            // Strategy: probe with a deliberate fake buildId.
            // Next.js always returns a 404 HTML page that embeds __NEXT_DATA__ containing the
            // real buildId — this is framework behaviour, not CricHeroes app logic, so it tends
            // to bypass Cloudflare bot protection which targets normal browser page visits.
            var probeDiscovered = await ProbeNextJsBuildId(httpClient);
            if (!string.IsNullOrWhiteSpace(probeDiscovered) && probeDiscovered != buildId)
            {
                Console.WriteLine($"[CricHeroes] Probe discovered buildId: {probeDiscovered}");
                SetBuildId(probeDiscovered);
                var result = await FetchNextData(probeDiscovered, pathSegment, matchUrl, httpClient);
                if (result != null)
                    return result;
            }

            // Last resort: full HTML page scraping (blocked by Cloudflare on server IPs)
            var staleBuildId = buildId;
            buildId = await DiscoverBuildId(matchUrl, httpClient, staleBuildId);
            if (string.IsNullOrWhiteSpace(buildId))
                return null;

            return await FetchNextData(buildId, pathSegment, matchUrl, httpClient);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch score data from the _next/data API using a specific buildId.
    /// Returns null if the buildId is stale or the request fails.
    /// Handles Next.js JSON redirects (e.g. /live → /summary when match ends).
    /// If stale, attempts to auto-discover the new buildId from the error page.
    /// </summary>
    private static async Task<JsonElement?> FetchNextData(string buildId, string pathSegment, string matchUrl, HttpClient httpClient, int redirectDepth = 0)
    {
        if (redirectDepth > 2) return null; // Prevent infinite redirect loops

        var apiUrl = $"https://cricheroes.com/_next/data/{buildId}/{pathSegment}.json";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("x-nextjs-data", "1");
        request.Headers.Add("Referer", matchUrl);
        ApplySessionCookie(request);

        var response = await _nextDataClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        // If we got redirected to an HTML page (Cloudflare or CricHeroes web page),
        // the content won't be JSON. Check for the _next/data pattern in the final URL too.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
        if (!finalUrl.Contains("/_next/data/") && !content.TrimStart().StartsWith("{"))
        {
            MarkFailureReasonFromHtml(content);
            // We were auto-redirected away from the API — the buildId is likely still valid
            // but the HTTP redirect went to a Cloudflare-blocked page.
            // Try to extract buildId from any HTML we got back.
            var discovered = ExtractBuildIdFromResponse(content);
            if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                SetBuildId(discovered);
            return null;
        }

        // Not JSON at all — likely Cloudflare challenge or error page
        if (!content.TrimStart().StartsWith("{"))
        {
            MarkFailureReasonFromHtml(content);
            // Try to extract buildId from error page HTML (404 page contains the real buildId)
            var discovered = ExtractBuildIdFromResponse(content);
            if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                SetBuildId(discovered);
            // Don't null _cachedBuildId — keep old value so future requests can auto-heal
            return null;
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        if (!root.TryGetProperty("pageProps", out var pageProps))
        {
            // Empty JSON like {} means the buildId is stale.
            // Fetch the same URL WITHOUT x-nextjs-data header to get a 404 HTML page
            // that contains the real buildId in its __NEXT_DATA__ script.
            try
            {
                var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                discoveryRequest.Headers.Add("Accept", "text/html");
                discoveryRequest.Headers.Add("Referer", matchUrl);
                ApplySessionCookie(discoveryRequest);
                var discoveryResponse = await _nextDataClient.SendAsync(discoveryRequest);
                var discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
                MarkFailureReasonFromHtml(discoveryContent);
                var discovered = ExtractBuildIdFromResponse(discoveryContent);
                if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                    SetBuildId(discovered);
            }
            catch { /* best-effort discovery */ }
            return null;
        }

        // Check for Next.js JSON redirect (match ended: /live → /summary)
        if (pageProps.TryGetProperty("__N_REDIRECT", out var redirectProp))
        {
            var redirectPath = redirectProp.GetString();
            if (!string.IsNullOrWhiteSpace(redirectPath))
            {
                var newPathSegment = redirectPath.TrimStart('/');
                _lastFetchWasRedirectedToSummary = redirectPath.Contains("/summary", StringComparison.OrdinalIgnoreCase);
                return await FetchNextData(buildId, newPathSegment, matchUrl, httpClient, redirectDepth + 1);
            }
        }

        // Direct match data — extract miniScorecard (live) or summaryData (completed)
        if (pageProps.TryGetProperty("miniScorecard", out var miniScorecard) &&
            miniScorecard.TryGetProperty("data", out var data))
        {
            return JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        }

        // Completed match: data is in summaryData.data instead of miniScorecard.data
        if (pageProps.TryGetProperty("summaryData", out var summaryData) &&
            summaryData.TryGetProperty("data", out var summaryMatchData))
        {
            _lastFetchWasRedirectedToSummary = true; // Mark as completed
            return JsonSerializer.Deserialize<JsonElement>(summaryMatchData.GetRawText());
        }

        // Valid JSON but no miniScorecard and no redirect — buildId is fine, just no data
        return null;
    }

    /// <summary>
    /// Extract buildId from any CricHeroes response HTML (e.g. 404 error page).
    /// CricHeroes may expose it through __NEXT_DATA__, script URLs, or inline JSON.
    /// </summary>
    private static string? ExtractBuildIdFromResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var buildIdMatch = Regex.Match(content, "\"buildId\"\\s*:\\s*\"([^\"]+)\"");
        if (buildIdMatch.Success)
            return buildIdMatch.Groups[1].Value;

        var manifestMatch = Regex.Match(content, @"/_next/static/([^/]+)/(?:_buildManifest|_ssgManifest)\.js");
        if (manifestMatch.Success)
            return manifestMatch.Groups[1].Value;

        return null;
    }

    private static void MarkFailureReasonFromHtml(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        var lower = content.ToLowerInvariant();
        if (lower.Contains("__cf_chl") || lower.Contains("just a moment") || lower.Contains("cf-challenge"))
        {
            _lastFetchFailureReason = FetchFailureReason.CloudflareBlocked;
            return;
        }

        if (lower.Contains("next.cricheroes.com/_next/static/chunks/") || lower.Contains("bailout_to_client_side_rendering") || lower.Contains("self.__next_f"))
        {
            _lastFetchFailureReason = FetchFailureReason.AppShellOrContractChanged;
        }
    }

    /// <summary>
    /// Discover the live buildId by deliberately requesting a fake one.
    /// Next.js responds with a 404 HTML page that always embeds __NEXT_DATA__ with the real buildId.
    /// This tends to bypass Cloudflare because it looks like a static asset request, not a browser visit.
    /// </summary>
    private static async Task<string?> ProbeNextJsBuildId(HttpClient httpClient)
    {
        try
        {
            // Use a known-minimal path so Next.js generates a 404 quickly without app-level blocking
            var probeUrl = "https://cricheroes.com/_next/data/__PROBE__/index.json";
            var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            request.Headers.Add("Accept", "text/html,application/json");
            // No x-nextjs-data header — we want the 404 HTML page, not JSON
            ApplySessionCookie(request);

            var response = await _nextDataClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            return ExtractBuildIdFromResponse(content);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Discover the Next.js buildId when the cached one is stale.
    /// Strategy: attempt page scrape to extract buildId from HTML __NEXT_DATA__ or script URLs.
    /// </summary>
    private async Task<string?> DiscoverBuildId(string matchUrl, HttpClient httpClient, string? knownStaleBuildId = null)
    {
        await _buildIdLock.WaitAsync();
        try
        {
            // Double-check: another thread may have already refreshed it.
            // Only skip discovery if the cached value is different from the known stale one.
            if (!string.IsNullOrWhiteSpace(_cachedBuildId) && _cachedBuildId != knownStaleBuildId)
                return _cachedBuildId;

            Console.WriteLine($"[CricHeroes] Auto-discovering buildId (stale: {knownStaleBuildId})");

            // Probe order: homepage first (least Cloudflare-protected), then live-matches, then match page
            var probeUrls = new[]
            {
                "https://cricheroes.com/",
                "https://cricheroes.com/live-cricket-score",
                matchUrl
            };

            foreach (var probeUrl in probeUrls)
            {
                var discovered = await TryScrapeBuildId(probeUrl, httpClient);
                if (!string.IsNullOrWhiteSpace(discovered) && discovered != knownStaleBuildId)
                {
                    Console.WriteLine($"[CricHeroes] Discovered new buildId: {discovered} (from {probeUrl})");
                    SetBuildId(discovered);
                    return discovered;
                }
            }

            Console.WriteLine("[CricHeroes] Auto-discovery failed — all probe URLs blocked or returned no buildId");
            return null;
        }
        finally
        {
            _buildIdLock.Release();
        }
    }

    /// <summary>
    /// Attempt to fetch a CricHeroes page and extract the buildId from the HTML.
    /// </summary>
    private static async Task<string?> TryScrapeBuildId(string url, HttpClient httpClient)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-User", "?1");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            ApplySessionCookie(request);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync();

            var discovered = ExtractBuildIdFromResponse(html);
            if (!string.IsNullOrWhiteSpace(discovered))
                return discovered;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Fall back to direct page scrape with full browser headers.
    /// </summary>
    private static async Task<JsonElement?> TryPageScrape(string matchUrl, HttpClient httpClient)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, matchUrl);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-User", "?1");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            ApplySessionCookie(request);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync();
            MarkFailureReasonFromHtml(html);
            return ExtractNextData(html);
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonElement?> TryNextPagePropsApi(string scorecardUrl, HttpClient httpClient)
    {
        try
        {
            var uri = new Uri(scorecardUrl);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            if (pathParts.Length < 3 || !pathParts[0].Equals("scorecard", StringComparison.OrdinalIgnoreCase))
                return null;

            var pathSegment = string.Join("/", pathParts);

            var buildId = _cachedBuildId;
            if (!string.IsNullOrWhiteSpace(buildId))
            {
                var result = await FetchNextPageProps(buildId, pathSegment, scorecardUrl, httpClient);
                if (result != null)
                    return result;

                var newBuildId = _cachedBuildId;
                if (!string.IsNullOrWhiteSpace(newBuildId) && newBuildId != buildId)
                {
                    result = await FetchNextPageProps(newBuildId, pathSegment, scorecardUrl, httpClient);
                    if (result != null)
                        return result;
                }
            }

            var probeDiscovered = await ProbeNextJsBuildId(httpClient);
            if (!string.IsNullOrWhiteSpace(probeDiscovered) && probeDiscovered != buildId)
            {
                SetBuildId(probeDiscovered);
                var result = await FetchNextPageProps(probeDiscovered, pathSegment, scorecardUrl, httpClient);
                if (result != null)
                    return result;
            }

            var staleBuildId = buildId;
            buildId = await DiscoverBuildId(scorecardUrl, httpClient, staleBuildId);
            if (string.IsNullOrWhiteSpace(buildId))
                return null;

            return await FetchNextPageProps(buildId, pathSegment, scorecardUrl, httpClient);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonElement?> FetchNextPageProps(string buildId, string pathSegment, string refererUrl, HttpClient httpClient)
    {
        var apiUrl = $"https://cricheroes.com/_next/data/{buildId}/{pathSegment}.json";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("x-nextjs-data", "1");
        request.Headers.Add("Referer", refererUrl);
        ApplySessionCookie(request);

        var response = await _nextDataClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!content.TrimStart().StartsWith("{"))
        {
            MarkFailureReasonFromHtml(content);
            var discovered = ExtractBuildIdFromResponse(content);
            if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                SetBuildId(discovered);
            return null;
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("pageProps", out var pageProps))
        {
            return null;
        }

        return JsonSerializer.Deserialize<JsonElement>(pageProps.GetRawText());
    }

    private static async Task<JsonElement?> TryPageScrapePageProps(string scorecardUrl, HttpClient httpClient)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, scorecardUrl);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-User", "?1");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            ApplySessionCookie(request);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync();
            MarkFailureReasonFromHtml(html);
            return ExtractNextPageProps(html);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract the miniScorecard.data JSON element from the __NEXT_DATA__ script tag.
    /// </summary>
    private static JsonElement? ExtractNextData(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
        if (scriptNode == null) return null;

        var json = scriptNode.InnerText;
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Navigate: props → pageProps → miniScorecard → data
        if (root.TryGetProperty("props", out var props) &&
            props.TryGetProperty("pageProps", out var pageProps) &&
            pageProps.TryGetProperty("miniScorecard", out var miniScorecard) &&
            miniScorecard.TryGetProperty("data", out var data))
        {
            // Clone so we can use it after the JsonDocument is disposed
            return JsonSerializer.Deserialize<JsonElement>(data.GetRawText());
        }

        return null;
    }

    private static JsonElement? ExtractNextPageProps(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
        if (scriptNode == null) return null;

        var json = scriptNode.InnerText;
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("props", out var props) &&
            props.TryGetProperty("pageProps", out var pageProps))
        {
            return JsonSerializer.Deserialize<JsonElement>(pageProps.GetRawText());
        }

        return null;
    }

    private static void ParseFullScorecard(JsonElement pageProps, LiveScoreData result)
    {
        if (!pageProps.TryGetProperty("scorecard", out var scorecard) || scorecard.ValueKind != JsonValueKind.Array)
            return;

        JsonElement? currentCard = null;

        foreach (var card in scorecard.EnumerateArray())
        {
            var teamName = GetStringDirect(card, "teamName");

            if (card.TryGetProperty("inning", out var inning))
            {
                result.AllInnings.Add(new InningsScoreLine
                {
                    TeamName = teamName,
                    InningsLabel = GetInningsLabel(GetIntDirect(inning, "inning")),
                    Runs = GetIntDirect(inning, "total_run"),
                    Wickets = GetIntDirect(inning, "total_wicket"),
                    Overs = GetStringDirect(inning, "overs_played"),
                    ClosureStatus = GetIntDirect(inning, "is_allout") == 1 ? "ALL_OUT" : string.Empty
                });
            }

            if (!string.IsNullOrWhiteSpace(result.BattingTeam) &&
                string.Equals(teamName?.Trim(), result.BattingTeam.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                currentCard = card;
            }
        }

        if (currentCard == null)
        {
            // Fallback: use the latest innings card
            var latestInning = -1;
            foreach (var card in scorecard.EnumerateArray())
            {
                if (card.TryGetProperty("inning", out var inning))
                {
                    var inningNo = GetIntDirect(inning, "inning");
                    if (inningNo > latestInning)
                    {
                        latestInning = inningNo;
                        currentCard = card;
                    }
                }
            }
        }

        if (currentCard == null)
            return;

        if (currentCard.Value.TryGetProperty("batting", out var batting) && batting.ValueKind == JsonValueKind.Array)
        {
            foreach (var batter in batting.EnumerateArray())
            {
                result.BattersBatted.Add(new BattingCardEntry
                {
                    Name = GetStringDirect(batter, "name"),
                    Runs = GetIntDirect(batter, "runs"),
                    Balls = GetIntDirect(batter, "balls"),
                    Status = GetStringDirect(batter, "how_to_out")
                });
            }
        }

        if (currentCard.Value.TryGetProperty("to_be_bat", out var toBeBat) && toBeBat.ValueKind == JsonValueKind.Array)
        {
            foreach (var batter in toBeBat.EnumerateArray())
            {
                result.BattersYetToBat.Add(new BattingCardEntry
                {
                    Name = GetStringDirect(batter, "name"),
                    Status = "YET_TO_BAT"
                });
            }
        }

        if (currentCard.Value.TryGetProperty("bowling", out var bowling) && bowling.ValueKind == JsonValueKind.Array)
        {
            foreach (var bowler in bowling.EnumerateArray())
            {
                result.BowlersBowled.Add(new BowlingCardEntry
                {
                    Name = GetStringDirect(bowler, "name"),
                    Overs = GetStringDirect(bowler, "overs"),
                    Maidens = GetIntDirect(bowler, "maidens"),
                    Runs = GetIntDirect(bowler, "runs"),
                    Wickets = GetIntDirect(bowler, "wickets")
                });
            }
        }

        result.IsFullScorecardAvailable = result.BattersBatted.Count > 0 ||
                                          result.BattersYetToBat.Count > 0 ||
                                          result.BowlersBowled.Count > 0;
    }

    private static void ParseOfficialFullScorecard(JsonElement data, LiveScoreData result)
    {
        result.AllInnings.Clear();
        result.BattersBatted.Clear();
        result.BattersYetToBat.Clear();
        result.BowlersBowled.Clear();

        if (data.TryGetProperty("team_a", out var teamA))
        {
            AddOfficialInnings(teamA, GetStringDirect(teamA, "name"), result);
        }

        if (data.TryGetProperty("team_b", out var teamB))
        {
            AddOfficialInnings(teamB, GetStringDirect(teamB, "name"), result);
        }

        JsonElement? battingTeam = null;
        if (!string.IsNullOrWhiteSpace(result.BattingTeam))
        {
            if (data.TryGetProperty("team_a", out var ta) &&
                string.Equals(GetStringDirect(ta, "name"), result.BattingTeam, StringComparison.OrdinalIgnoreCase))
            {
                battingTeam = ta;
            }
            else if (data.TryGetProperty("team_b", out var tb) &&
                     string.Equals(GetStringDirect(tb, "name"), result.BattingTeam, StringComparison.OrdinalIgnoreCase))
            {
                battingTeam = tb;
            }
        }

        if (battingTeam == null)
        {
            battingTeam = data.TryGetProperty("team_a", out var fallbackA) ? fallbackA : (JsonElement?)null;
        }

        if (battingTeam == null)
            return;

        if (!battingTeam.Value.TryGetProperty("scorecard", out var cards) || cards.ValueKind != JsonValueKind.Array || cards.GetArrayLength() == 0)
            return;

        JsonElement currentCard = cards[0];
        var highestInning = -1;
        foreach (var card in cards.EnumerateArray())
        {
            var inningNo = GetIntDirect(card, "inning");
            if (inningNo > highestInning)
            {
                highestInning = inningNo;
                currentCard = card;
            }
        }

        if (currentCard.TryGetProperty("batting", out var batting) && batting.ValueKind == JsonValueKind.Array)
        {
            foreach (var batter in batting.EnumerateArray())
            {
                result.BattersBatted.Add(new BattingCardEntry
                {
                    Name = GetStringDirect(batter, "name"),
                    Runs = GetIntDirect(batter, "runs"),
                    Balls = GetIntDirect(batter, "balls"),
                    Status = GetStringDirect(batter, "how_to_out")
                });
            }
        }

        if (currentCard.TryGetProperty("to_be_bat", out var yetToBat) && yetToBat.ValueKind == JsonValueKind.Array)
        {
            foreach (var batter in yetToBat.EnumerateArray())
            {
                result.BattersYetToBat.Add(new BattingCardEntry
                {
                    Name = GetStringDirect(batter, "name"),
                    Status = "YET_TO_BAT"
                });
            }
        }

        if (currentCard.TryGetProperty("bowling", out var bowling) && bowling.ValueKind == JsonValueKind.Array)
        {
            foreach (var bowler in bowling.EnumerateArray())
            {
                result.BowlersBowled.Add(new BowlingCardEntry
                {
                    Name = GetStringDirect(bowler, "name"),
                    Overs = GetStringDirect(bowler, "overs"),
                    Maidens = GetIntDirect(bowler, "maidens"),
                    Runs = GetIntDirect(bowler, "runs"),
                    Wickets = GetIntDirect(bowler, "wickets")
                });
            }
        }

        result.IsFullScorecardAvailable = result.BattersBatted.Count > 0 ||
                                          result.BattersYetToBat.Count > 0 ||
                                          result.BowlersBowled.Count > 0 ||
                                          result.AllInnings.Count > 0;
    }

    private static void AddOfficialInnings(JsonElement team, string teamName, LiveScoreData result)
    {
        if (!team.TryGetProperty("innings", out var innings) || innings.ValueKind != JsonValueKind.Array)
            return;

        foreach (var inning in innings.EnumerateArray())
        {
            result.AllInnings.Add(new InningsScoreLine
            {
                TeamName = teamName,
                InningsLabel = GetInningsLabel(GetIntDirect(inning, "inning")),
                Runs = GetIntDirect(inning, "total_run"),
                Wickets = GetIntDirect(inning, "total_wicket"),
                Overs = GetStringDirect(inning, "overs_played"),
                ClosureStatus = GetIntDirect(inning, "is_allout") == 1 ? "ALL_OUT" : string.Empty
            });
        }
    }

    /// <summary>
    /// Parse all score fields from the miniScorecard.data JSON element.
    /// </summary>
    private static void ParseMatchData(JsonElement data, LiveScoreData result)
    {
        // Teams
        result.TeamA = GetString(data, "team_a", "name");
        result.TeamB = GetString(data, "team_b", "name");
        result.TeamASummary = GetString(data, "team_a", "summary");
        result.TeamBSummary = GetString(data, "team_b", "summary");

        // Match status — preserve "Completed" if already set (e.g. from /live→/summary redirect)
        var status = GetStringDirect(data, "status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Normalize CricHeroes statuses to our standard values
            var normalized = status.Trim().ToLowerInvariant() switch
            {
                "past" or "completed" or "resulted" => "Completed",
                "live" or "in_progress" => "Live",
                _ => CapFirst(status)
            };
            result.MatchStatus = normalized;
        }
        else if (result.MatchStatus != "Completed")
            result.MatchStatus = "Unknown";

        // Current innings — find the batting team's innings
        var currentInning = GetIntDirect(data, "current_inning");
        ParseCurrentInnings(data, result, currentInning);

        // Batsmen
        if (data.TryGetProperty("batsmen", out var batsmen))
        {
            if (batsmen.TryGetProperty("sb", out var sb))
            {
                result.BatsmanOnStrike = GetStringDirect(sb, "name");
                var sbRuns = GetIntDirect(sb, "runs");
                var sbBalls = GetIntDirect(sb, "balls");
                result.BatsmanOnStrikeRuns = $"{sbRuns}({sbBalls})";
            }
            if (batsmen.TryGetProperty("nsb", out var nsb))
            {
                result.BatsmanNonStrike = GetStringDirect(nsb, "name");
                var nsbRuns = GetIntDirect(nsb, "runs");
                var nsbBalls = GetIntDirect(nsb, "balls");
                result.BatsmanNonStrikeRuns = $"{nsbRuns}({nsbBalls})";
            }
        }

        // Bowlers (current bowler = "sb")
        if (data.TryGetProperty("bowlers", out var bowlers) &&
            bowlers.TryGetProperty("sb", out var currentBowler))
        {
            result.CurrentBowler = GetStringDirect(currentBowler, "name");
            var bOvers = GetStringDirect(currentBowler, "overs");
            if (string.IsNullOrWhiteSpace(bOvers))
            {
                bOvers = currentBowler.TryGetProperty("overs", out var ov) && ov.ValueKind == JsonValueKind.Number
                    ? ov.ToString()
                    : "0";
            }
            var bRuns = GetIntDirect(currentBowler, "runs");
            var bWickets = GetIntDirect(currentBowler, "wickets");
            result.CurrentBowlerFigures = $"{bWickets}/{bRuns} ({bOvers} ov)";
        }

        // Toss
        result.TossResult = GetStringDirect(data, "toss_details");

        // Match summary, target, RRR
        if (data.TryGetProperty("match_summary", out var summary))
        {
            var target = GetStringDirect(summary, "target");
            if (!string.IsNullOrWhiteSpace(target) && target != "-")
            {
                result.Target = target;
            }

            var rrr = GetStringDirect(summary, "rrr");
            if (!string.IsNullOrWhiteSpace(rrr) && rrr != "0.00")
            {
                result.RequiredRunRate = rrr;
            }

            var matchSummaryText = GetStringDirect(summary, "summary");
            if (!string.IsNullOrWhiteSpace(matchSummaryText))
            {
                result.MatchSummary = matchSummaryText;
            }
        }

        // Current partnership
        result.CurrentPartnership = GetStringDirect(data, "current_partnership");

        // Projected score
        result.ProjectedScore = GetIntDirect(data, "projected_score");
    }

    /// <summary>
    /// Parse current innings score from the batting team's innings array.
    /// </summary>
    private static void ParseCurrentInnings(JsonElement data, LiveScoreData result, int currentInning)
    {
        // Determine which team is batting based on current_inning
        // Inning 1 = team_a bats, inning 2 = team_b bats (typically)
        string battingTeamKey = "team_a";
        if (currentInning >= 2 && data.TryGetProperty("team_b", out var tb) &&
            tb.TryGetProperty("innings", out var tbInnings) && tbInnings.GetArrayLength() > 0)
        {
            // Check if team_b's innings has started
            var tbFirstInning = tbInnings[0];
            var tbOvers = GetStringDirect(tbFirstInning, "overs_played");
            if (tbOvers != "0" && !string.IsNullOrWhiteSpace(tbOvers))
            {
                battingTeamKey = "team_b";
            }
        }

        if (data.TryGetProperty(battingTeamKey, out var battingTeam))
        {
            result.BattingTeam = GetStringDirect(battingTeam, "name");

            if (battingTeam.TryGetProperty("innings", out var innings) && innings.GetArrayLength() > 0)
            {
                // Use the last innings entry (most recent)
                var currentInningsData = innings[innings.GetArrayLength() - 1];

                result.Runs = GetIntDirect(currentInningsData, "total_run");
                result.Wickets = GetIntDirect(currentInningsData, "total_wicket");
                result.Overs = GetStringDirect(currentInningsData, "overs_played");

                // Run rate from summary
                if (currentInningsData.TryGetProperty("summary", out var sum))
                {
                    result.RunRate = GetStringDirect(sum, "rr");
                }
            }
        }
    }

    // --- JSON helper methods ---

    private static string GetString(JsonElement root, string objectName, string propertyName)
    {
        if (root.TryGetProperty(objectName, out var obj) && obj.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : prop.ToString();
        }
        return "";
    }

    private static string GetStringDirect(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : prop.ToString();
        }
        return "";
    }

    private static int GetIntDirect(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return 0;
    }

    /// <summary>
    /// Ensure the URL points to the /live tab for real-time data.
    /// </summary>
    private static string NormalizeLiveUrl(string url)
    {
        // CricHeroes scorecard URLs: /scorecard/{matchId}/{tournament}/{teams}/{tab}
        // We need the /live tab for real-time __NEXT_DATA__
        if (url.EndsWith("/scorecard", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/summary", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/commentary", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/analysis", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/teams", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/gallery", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = url.LastIndexOf('/');
            if (lastSlash > 0)
            {
                url = url[..lastSlash] + "/live";
            }
        }
        else if (!url.EndsWith("/live", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/live";
        }

        return url;
    }

    private static string ToScorecardUrl(string url)
    {
        if (url.EndsWith("/live", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/summary", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/commentary", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/analysis", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/teams", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/gallery", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = url.LastIndexOf('/');
            if (lastSlash > 0)
            {
                return url[..lastSlash] + "/scorecard";
            }
        }

        if (!url.EndsWith("/scorecard", StringComparison.OrdinalIgnoreCase))
            return url.TrimEnd('/') + "/scorecard";

        return url;
    }

    private static string GetInningsLabel(int inningNo)
    {
        return inningNo switch
        {
            1 => "1st Innings",
            2 => "2nd Innings",
            3 => "3rd Innings",
            4 => "4th Innings",
            _ => $"Innings {inningNo}"
        };
    }

    private static string CapFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
