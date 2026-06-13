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
    // Cached Next.js buildId — starts with known-good value, auto-refreshes when stale (404)
    private static string? _cachedBuildId = "qCElbg6YXMsFfmG1aQrwG";
    private static readonly SemaphoreSlim _buildIdLock = new(1, 1);

    // Tracks whether the last FetchNextData call was redirected from /live to /summary (match ended)
    [ThreadStatic] private static bool _lastFetchWasRedirectedToSummary;

    // Dedicated HttpClient for _next/data requests — does NOT follow HTTP redirects
    // because CricHeroes returns JSON __N_REDIRECT responses that we handle manually.
    // If auto-redirect is on, the HTTP client follows 307 to the web page which gets 403 from Cloudflare.
    private static readonly HttpClient _nextDataClient = CreateNextDataClient();

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
    /// Manually set the buildId (e.g. from admin UI).
    /// </summary>
    public static void SetBuildId(string buildId) => _cachedBuildId = buildId;

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

    public async Task<LiveScoreData> ExtractAsync(string matchUrl, HttpClient httpClient)
    {
        var result = new LiveScoreData { ProviderType = ProviderName, MatchUrl = matchUrl };

        try
        {
            matchUrl = NormalizeLiveUrl(matchUrl);

            // Strategy 1: Try CricHeroes _next/data JSON API (bypasses Cloudflare web protection)
            _lastFetchWasRedirectedToSummary = false;
            var matchData = await TryNextDataApi(matchUrl, httpClient);

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
                result.ErrorMessage = "Unable to fetch score data from CricHeroes. The cached buildId may be stale — try updating it via POST /api/admin/cricheroes-buildid.";
                return result;
            }

            ParseMatchData(matchData.Value, result);
            result.IsValid = !string.IsNullOrWhiteSpace(result.TeamA);
            result.ScrapedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Extraction failed: {ex.Message}";
        }

        return result;
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

            // All strategies failed — try full discovery as last resort
            buildId = await DiscoverBuildId(matchUrl, httpClient);
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

        var response = await _nextDataClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        // If we got redirected to an HTML page (Cloudflare or CricHeroes web page),
        // the content won't be JSON. Check for the _next/data pattern in the final URL too.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
        if (!finalUrl.Contains("/_next/data/") && !content.TrimStart().StartsWith("{"))
        {
            // We were auto-redirected away from the API — the buildId is likely still valid
            // but the HTTP redirect went to a Cloudflare-blocked page.
            // Try to extract buildId from any HTML we got back.
            var discovered = ExtractBuildIdFromResponse(content);
            if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                _cachedBuildId = discovered;
            return null;
        }

        // Not JSON at all — likely Cloudflare challenge or error page
        if (!content.TrimStart().StartsWith("{"))
        {
            // Try to extract buildId from error page HTML (404 page contains the real buildId)
            var discovered = ExtractBuildIdFromResponse(content);
            if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                _cachedBuildId = discovered;
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
                var discoveryResponse = await _nextDataClient.SendAsync(discoveryRequest);
                var discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
                var discovered = ExtractBuildIdFromResponse(discoveryContent);
                if (!string.IsNullOrWhiteSpace(discovered) && discovered != buildId)
                    _cachedBuildId = discovered;
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
    /// The 404 page embeds __NEXT_DATA__ with the current buildId.
    /// </summary>
    private static string? ExtractBuildIdFromResponse(string content)
    {
        var match = Regex.Match(content, @"""buildId""\s*:\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Discover the Next.js buildId when the cached one is stale.
    /// Strategy: attempt page scrape to extract buildId from HTML __NEXT_DATA__ or script URLs.
    /// </summary>
    private async Task<string?> DiscoverBuildId(string matchUrl, HttpClient httpClient)
    {
        await _buildIdLock.WaitAsync();
        try
        {
            // Double-check: another thread may have already refreshed it
            if (!string.IsNullOrWhiteSpace(_cachedBuildId))
                return _cachedBuildId;

            // Try scraping the actual match page to find buildId in HTML
            var discovered = await TryScrapeBuildId(matchUrl, httpClient);
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                _cachedBuildId = discovered;
                return discovered;
            }

            // Try a few well-known pages that might be less protected
            var probeUrls = new[]
            {
                "https://cricheroes.com/",
                "https://cricheroes.com/live-matches",
                matchUrl
            };

            foreach (var probeUrl in probeUrls)
            {
                discovered = await TryScrapeBuildId(probeUrl, httpClient);
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    _cachedBuildId = discovered;
                    return discovered;
                }
            }

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

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync();

            // Try buildId from __NEXT_DATA__ JSON
            var buildIdMatch = Regex.Match(html, @"""buildId""\s*:\s*""([^""]+)""");
            if (buildIdMatch.Success) return buildIdMatch.Groups[1].Value;

            // Try from _buildManifest.js script URL
            var manifestMatch = Regex.Match(html, @"/_next/static/([^/]+)/_buildManifest\.js");
            if (manifestMatch.Success) return manifestMatch.Groups[1].Value;
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

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync();
            return ExtractNextData(html);
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

    private static string CapFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
