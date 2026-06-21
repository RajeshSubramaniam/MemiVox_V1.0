using System.Text.Json;
using System.Text.RegularExpressions;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services.Providers;

/// <summary>
/// Extracts live/completed score data from PlayHQ via their public GraphQL API.
/// 
/// API: POST https://api.playhq.com/graphql  (requires "tenant" header, e.g. "cricket-australia")
/// Game ID: last path segment of the match URL
/// Tenant: first path segment (e.g. "cricket-australia")
/// 
/// URL formats supported:
///   - /cricket-australia/org/.../game-centre/{gameId}
///   - /account/my-teams/cricket-australia/.../game/{gameId}
/// </summary>
public class PlayHqScraper : IProviderScraper
{
    private const string GraphqlEndpoint = "https://api.playhq.com/graphql";
    private readonly PlayHqSpectatorService? _spectatorService;

    private static readonly string GameQuery = @"
query($gameId: ID!) {
  tenantConfiguration { label }
  discoverGame(gameID: $gameId) {
    id
    status { name value }
    home { ... on DiscoverTeam { id name } }
    away { ... on DiscoverTeam { id name } }
    result {
      winner { name value }
      outcome { name value }
      home {
        score
        periods {
          period { label value }
          statistics { count type { label value } }
          closureStatus
        }
        gameOutcomeDescription
      }
      away {
        score
        periods {
          period { label value }
          statistics { count type { label value } }
          closureStatus
        }
      }
    }
    statistics {
      home {
        players {
          player {
            ... on DiscoverParticipant { id profile { firstName lastName } }
            ... on DiscoverParticipantFillInPlayer { id profile { firstName lastName } }
            ... on DiscoverAnonymousParticipant { id name }
          }
          periodStatistics {
            period { value }
            type
            statistics { type { value } count }
            status side displayOrder
          }
        }
      }
      away {
        players {
          player {
            ... on DiscoverParticipant { id profile { firstName lastName } }
            ... on DiscoverParticipantFillInPlayer { id profile { firstName lastName } }
            ... on DiscoverAnonymousParticipant { id name }
          }
          periodStatistics {
            period { value }
            type
            statistics { type { value } count }
            status side displayOrder
          }
        }
      }
    }
  }
}";

    public PlayHqScraper(PlayHqSpectatorService? spectatorService = null)
    {
        _spectatorService = spectatorService;
    }

    public string ProviderName => "PlayHQ";

    public bool CanHandle(string matchUrl)
    {
        return !string.IsNullOrWhiteSpace(matchUrl) &&
               matchUrl.Contains("playhq.com", StringComparison.OrdinalIgnoreCase);
    }

    public Task<UrlValidationResult> ValidateAsync(string matchUrl, HttpClient httpClient)
    {
        if (!Uri.TryCreate(matchUrl, UriKind.Absolute, out var uri))
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "Invalid URL format." });

        if (!uri.Host.Contains("playhq.com", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "URL is not from playhq.com." });

        var (tenant, gameId) = ParseUrl(matchUrl);
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(gameId))
            return Task.FromResult(new UrlValidationResult { IsValid = false, ProviderType = ProviderName, Message = "Could not extract tenant or game ID from URL. Expected format: playhq.com/{tenant}/.../game-centre/{gameId}" });

        return Task.FromResult(new UrlValidationResult { IsValid = true, ProviderType = ProviderName, Message = $"Valid PlayHQ match URL (tenant: {tenant}, gameId: {gameId})." });
    }

    public async Task<LiveScoreData> ExtractAsync(string matchUrl, HttpClient httpClient, bool includeFullScorecard = false)
    {
        var result = new LiveScoreData { ProviderType = ProviderName, MatchUrl = matchUrl };

        try
        {
            var (tenant, gameId) = ParseUrl(matchUrl);
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(gameId))
            {
                result.ErrorMessage = "Could not extract tenant or game ID from the PlayHQ URL.";
                return result;
            }

            var requestBody = JsonSerializer.Serialize(new
            {
                query = GameQuery,
                variables = new { gameId }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, GraphqlEndpoint);
            request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add("tenant", tenant);
            request.Headers.Add("Origin", "https://www.playhq.com");
            request.Headers.Add("Referer", "https://www.playhq.com/");

            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            // The response may have config.js appended after the JSON — split it off
            var jsonEnd = content.IndexOf("\n// Runtime config", StringComparison.Ordinal);
            if (jsonEnd > 0) content = content[..jsonEnd];

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                var firstError = errors[0].GetProperty("message").GetString();
                result.ErrorMessage = $"PlayHQ API error: {firstError}";
                return result;
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("discoverGame", out var game) ||
                game.ValueKind == JsonValueKind.Null)
            {
                result.ErrorMessage = "Game not found in PlayHQ.";
                return result;
            }

            ParseGameData(game, result, includeFullScorecard);
            result.IsValid = !string.IsNullOrWhiteSpace(result.TeamA);
            result.ScrapedAt = DateTime.UtcNow;

            // Extract tenant short label (e.g. "ca") for spectator WebSocket
            string? tenantLabel = null;
            if (data.TryGetProperty("tenantConfiguration", out var tenantConfig) &&
                tenantConfig.TryGetProperty("label", out var labelEl) &&
                labelEl.ValueKind == JsonValueKind.String)
            {
                tenantLabel = labelEl.GetString();
            }

            // For live matches, spectator WebSocket is usually richer than discoverGame.
            // In full-scorecard mode, always try spectator to pull player-card details.
            var shouldUseSpectator = _spectatorService != null && result.MatchStatus == "Live" &&
                (includeFullScorecard ||
                 (result.Runs == 0 && result.Wickets == 0 && string.IsNullOrWhiteSpace(result.TeamASummary)));

            if (shouldUseSpectator)
            {
                Console.WriteLine($"[PlayHQ] Live match — fetching scores via spectator for game {gameId} (tenant: {tenantLabel})");
                try
                {
                    var spectatorResult = await _spectatorService.ExtractLiveScoreAsync(
                        matchUrl, gameId!, result.TeamA, result.TeamB, tenantLabel, includeFullScorecard);
                    if (spectatorResult != null && spectatorResult.IsValid)
                    {
                        Console.WriteLine($"[PlayHQ] Spectator succeeded: {spectatorResult.Runs}/{spectatorResult.Wickets} ({spectatorResult.Overs} ov)");
                        spectatorResult.SetupId = result.SetupId;

                        // If this is a fullcard request and spectator has no extended details,
                        // fall back to discoverGame parse if that has more card data.
                        if (includeFullScorecard)
                        {
                            var spectatorHasFull = spectatorResult.IsFullScorecardAvailable ||
                                spectatorResult.AllInnings.Count > 0 ||
                                spectatorResult.BattersBatted.Count > 0 ||
                                spectatorResult.BattersYetToBat.Count > 0 ||
                                spectatorResult.BowlersBowled.Count > 0;

                            var apiHasFull = result.IsFullScorecardAvailable ||
                                result.AllInnings.Count > 0 ||
                                result.BattersBatted.Count > 0 ||
                                result.BattersYetToBat.Count > 0 ||
                                result.BowlersBowled.Count > 0;

                            if (!spectatorHasFull && apiHasFull)
                                return result;
                        }

                        return spectatorResult;
                    }
                }
                catch (Exception specEx)
                {
                    Console.WriteLine($"[PlayHQ] Spectator error: {specEx.Message}");
                }

                // For normal live polling, fall back to discoverGame parse rather than
                // forcing a loading state. This avoids blanking the overlay if spectator
                // data is temporarily unavailable or changes shape.
                if (!includeFullScorecard)
                {
                    Console.WriteLine($"[PlayHQ] Spectator unavailable — falling back to discoverGame result");
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"PlayHQ extraction failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Extract tenant (e.g. "cricket-australia") and gameId from various PlayHQ URL formats.
    /// </summary>
    internal static (string? Tenant, string? GameId) ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (null, null);

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return (null, null);

        // The game ID is always the last path segment
        var gameId = segments[^1];

        // Tenant is the first segment — but skip "account" for /account/my-teams/{tenant}/... URLs
        string? tenant = null;
        if (segments[0].Equals("account", StringComparison.OrdinalIgnoreCase) && segments.Length >= 3)
        {
            // /account/my-teams/{tenant}/...
            tenant = segments[2];
        }
        else
        {
            // /{tenant}/org/...
            tenant = segments[0];
        }

        // Validate: gameId should look like a short hex ID (e.g. "bf90b123")
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length < 6)
            return (tenant, null);

        return (tenant, gameId);
    }

    private static void ParseGameData(JsonElement game, LiveScoreData result, bool includeFullScorecard)
    {
        // Teams
        if (game.TryGetProperty("home", out var home))
            result.TeamA = GetStr(home, "name");
        if (game.TryGetProperty("away", out var away))
            result.TeamB = GetStr(away, "name");

        // Match status
        if (game.TryGetProperty("status", out var status))
        {
            var statusValue = GetStr(status, "value");
            result.MatchStatus = statusValue switch
            {
                "FINAL" or "COMPLETED" or "FORFEIT" => "Completed",
                "IN_PROGRESS" or "LIVE" => "Live",
                "SCHEDULED" or "NOT_STARTED" or "UPCOMING" => "Scheduled",
                _ => GetStr(status, "name")
            };
        }

        // Result and scores (null for scheduled/upcoming matches)
        if (game.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
            ParseResult(resultEl, result, includeFullScorecard);

        // Player statistics (batting/bowling) — null for scheduled matches
        if (game.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
            ParsePlayerStats(stats, result, includeFullScorecard);
    }

    private static void ParseResult(JsonElement resultEl, LiveScoreData result, bool includeFullScorecard)
    {
        // Outcome description (e.g. "Win 1st Innings", "Team A won by points")
        if (resultEl.TryGetProperty("outcome", out var outcome))
        {
            var outcomeName = GetStr(outcome, "name");
            if (!string.IsNullOrWhiteSpace(outcomeName))
                result.MatchSummary = outcomeName;
        }

        // Winner — substitute actual team name
        if (resultEl.TryGetProperty("winner", out var winner))
        {
            var winnerSide = GetStr(winner, "value"); // HOME or AWAY
            var winnerTeam = winnerSide == "HOME" ? result.TeamA : result.TeamB;
            if (!string.IsNullOrWhiteSpace(winnerTeam))
            {
                // Enhance summary with actual team name
                if (!string.IsNullOrWhiteSpace(result.MatchSummary))
                    result.MatchSummary = $"{winnerTeam} - {result.MatchSummary}";
                else
                    result.MatchSummary = $"{winnerTeam} won";
            }
        }

        // Extract all innings for each team
        var homeInnings = ExtractAllInnings(resultEl, "home");
        var awayInnings = ExtractAllInnings(resultEl, "away");

        // Format team summaries (handles multi-innings: "157 & 61/7d")
        result.TeamASummary = FormatMultiInningsScore(homeInnings);
        result.TeamBSummary = FormatMultiInningsScore(awayInnings);

        // gameOutcomeDescription override
        if (resultEl.TryGetProperty("home", out var homeResult) &&
            homeResult.TryGetProperty("gameOutcomeDescription", out var god))
        {
            var desc = god.GetString();
            if (!string.IsNullOrWhiteSpace(desc))
                result.MatchSummary = desc;
        }

        // Determine current batting team and set main score from latest innings
        DetermineCurrentInnings(homeInnings, awayInnings, result);

        if (includeFullScorecard)
        {
            foreach (var inn in homeInnings)
            {
                result.AllInnings.Add(new InningsScoreLine
                {
                    TeamName = result.TeamA,
                    InningsLabel = ToInningsLabel(inn.PeriodValue),
                    Runs = inn.Runs,
                    Wickets = inn.Wickets,
                    Overs = inn.Overs,
                    ClosureStatus = inn.ClosureStatus ?? string.Empty
                });
            }

            foreach (var inn in awayInnings)
            {
                result.AllInnings.Add(new InningsScoreLine
                {
                    TeamName = result.TeamB,
                    InningsLabel = ToInningsLabel(inn.PeriodValue),
                    Runs = inn.Runs,
                    Wickets = inn.Wickets,
                    Overs = inn.Overs,
                    ClosureStatus = inn.ClosureStatus ?? string.Empty
                });
            }
        }
    }

    private record InningsData(string PeriodValue, int Runs, int Wickets, string Overs, string? ClosureStatus);

    private static List<InningsData> ExtractAllInnings(JsonElement resultEl, string teamKey)
    {
        var innings = new List<InningsData>();

        if (!resultEl.TryGetProperty(teamKey, out var teamResult))
            return innings;

        if (!teamResult.TryGetProperty("periods", out var periods) || periods.GetArrayLength() == 0)
            return innings;

        foreach (var period in periods.EnumerateArray())
        {
            var periodValue = GetNestedStr(period, "period", "value");
            var closureStatus = period.TryGetProperty("closureStatus", out var cs) && cs.ValueKind == JsonValueKind.String
                ? cs.GetString() : null;

            int runs = 0, wickets = 0;
            string overs = "0.0";

            if (period.TryGetProperty("statistics", out var stats))
            {
                foreach (var stat in stats.EnumerateArray())
                {
                    var typeValue = GetNestedStr(stat, "type", "value");
                    var count = stat.TryGetProperty("count", out var c) ? c.GetDouble() : 0;

                    switch (typeValue)
                    {
                        case "TOTAL_SCORE": runs = (int)count; break;
                        case "TOTAL_OUTS": wickets = (int)count; break;
                        case "TOTAL_OVERS": overs = count.ToString("0.#"); break;
                    }
                }
            }

            innings.Add(new InningsData(periodValue, runs, wickets, overs, closureStatus));
        }

        // Sort chronologically: FIRST_INNINGS before SECOND_INNINGS (alphabetical F < S works)
        innings.Sort((a, b) => string.Compare(a.PeriodValue, b.PeriodValue, StringComparison.Ordinal));
        return innings;
    }

    private static string FormatMultiInningsScore(List<InningsData> innings)
    {
        if (innings.Count == 0) return "Yet to bat";

        var parts = new List<string>();
        foreach (var inn in innings)
        {
            var suffix = inn.ClosureStatus == "DECLARED" ? "d" : "";
            // All out (10 wickets) — show just runs; otherwise show runs/wickets
            if (inn.Wickets == 10)
                parts.Add($"{inn.Runs}{suffix}");
            else
                parts.Add($"{inn.Runs}/{inn.Wickets}{suffix}");
        }
        return string.Join(" & ", parts);
    }

    private static void DetermineCurrentInnings(List<InningsData> homeInnings, List<InningsData> awayInnings, LiveScoreData result)
    {
        // Find the latest active innings (no closureStatus) or the most recent completed innings
        var latestHome = homeInnings.Count > 0 ? homeInnings[^1] : null;
        var latestAway = awayInnings.Count > 0 ? awayInnings[^1] : null;

        // Determine which team is currently batting based on innings progression
        // Standard order: Home 1st → Away 1st → Home 2nd → Away 2nd
        // The "current" innings is the one still active (no closureStatus), or the last one if all are closed
        InningsData? currentInnings = null;
        bool currentIsHome = true;

        // Check if away's latest innings is still active
        if (latestAway != null && string.IsNullOrEmpty(latestAway.ClosureStatus))
        {
            currentInnings = latestAway;
            currentIsHome = false;
        }
        // Check if home's latest innings is still active
        else if (latestHome != null && string.IsNullOrEmpty(latestHome.ClosureStatus))
        {
            currentInnings = latestHome;
            currentIsHome = true;
        }
        // All closed — use the most recent innings (highest innings number, away over home for same)
        else if (latestAway != null && latestHome != null)
        {
            // Compare innings numbers
            int homeInningsNum = homeInnings.Count;
            int awayInningsNum = awayInnings.Count;

            if (awayInningsNum > homeInningsNum)
            {
                currentInnings = latestAway;
                currentIsHome = false;
            }
            else if (awayInningsNum == homeInningsNum)
            {
                // Same number of innings — away batted last
                currentInnings = latestAway;
                currentIsHome = false;
            }
            else
            {
                currentInnings = latestHome;
                currentIsHome = true;
            }
        }
        else if (latestAway != null)
        {
            currentInnings = latestAway;
            currentIsHome = false;
        }
        else if (latestHome != null)
        {
            currentInnings = latestHome;
            currentIsHome = true;
        }

        if (currentInnings == null) return;

        result.BattingTeam = currentIsHome ? result.TeamA : result.TeamB;
        result.Runs = currentInnings.Runs;
        result.Wickets = currentInnings.Wickets;
        result.Overs = currentInnings.Overs;

        if (double.TryParse(currentInnings.Overs, out var ov) && ov > 0)
        {
            result.RunRate = (currentInnings.Runs / ov).ToString("0.00");
        }

        // Calculate target: sum of opponent's all innings - sum of current team's previous innings + 1
        var opponentInnings = currentIsHome ? awayInnings : homeInnings;
        var currentTeamInnings = currentIsHome ? homeInnings : awayInnings;

        int opponentTotal = opponentInnings.Sum(i => i.Runs);
        int currentTeamPreviousTotal = currentTeamInnings.Where(i => i != currentInnings).Sum(i => i.Runs);

        if (opponentTotal > 0)
        {
            var target = opponentTotal - currentTeamPreviousTotal + 1;
            if (target > 0)
                result.Target = target.ToString();
        }
    }

    private static void ParsePlayerStats(JsonElement stats, LiveScoreData result, bool includeFullScorecard)
    {
        var battingTeamIsHome = result.BattingTeam == result.TeamA;
        var battingSide = battingTeamIsHome ? "HOME" : "AWAY";

        var battingTeamKey = battingTeamIsHome ? "home" : "away";
        var bowlingTeamKey = battingTeamIsHome ? "away" : "home";

        // Extract batsmen (NOT_OUT players on the batting side)
        if (stats.TryGetProperty(battingTeamKey, out var battingTeamStats) &&
            battingTeamStats.TryGetProperty("players", out var battingPlayers))
        {
            var notOutBatsmen = new List<(string Name, int Runs, int Balls)>();
            var battedByName = new Dictionary<string, BattingCardEntry>(StringComparer.OrdinalIgnoreCase);
            var knownBatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var player in battingPlayers.EnumerateArray())
            {
                var name = GetPlayerName(player);
                if (string.IsNullOrWhiteSpace(name)) continue;
                knownBatters.Add(name);

                if (!player.TryGetProperty("periodStatistics", out var periodStats)) continue;

                foreach (var ps in periodStats.EnumerateArray())
                {
                    var side = GetStr(ps, "side");
                    var psStatus = GetStr(ps, "status");

                    if (side == battingSide && psStatus == "NOT_OUT")
                    {
                        int runs = 0, balls = 0;
                        if (ps.TryGetProperty("statistics", out var pStats))
                        {
                            foreach (var s in pStats.EnumerateArray())
                            {
                                var tv = GetNestedStr(s, "type", "value");
                                var cnt = s.TryGetProperty("count", out var cv) ? (int)cv.GetDouble() : 0;
                                if (tv == "TOTAL_RUNS") runs = cnt;
                                else if (tv == "BALLS_FACED") balls = cnt;
                            }
                        }
                        notOutBatsmen.Add((name, runs, balls));
                    }

                    if (side == battingSide)
                    {
                        int runs = 0, balls = 0;
                        bool hasBattingStats = false;

                        if (ps.TryGetProperty("statistics", out var pStats))
                        {
                            foreach (var s in pStats.EnumerateArray())
                            {
                                var tv = GetNestedStr(s, "type", "value");
                                var cnt = s.TryGetProperty("count", out var cv) ? (int)cv.GetDouble() : 0;
                                if (tv == "TOTAL_RUNS") { runs = cnt; hasBattingStats = true; }
                                else if (tv == "BALLS_FACED") { balls = cnt; hasBattingStats = true; }
                            }
                        }

                        if (hasBattingStats)
                        {
                            battedByName[name] = new BattingCardEntry
                            {
                                Name = name,
                                Runs = runs,
                                Balls = balls,
                                Status = psStatus
                            };
                        }
                    }
                }
            }

            if (notOutBatsmen.Count >= 1)
            {
                result.BatsmanOnStrike = notOutBatsmen[^1].Name;
                result.BatsmanOnStrikeRuns = $"{notOutBatsmen[^1].Runs}({notOutBatsmen[^1].Balls})";
            }
            if (notOutBatsmen.Count >= 2)
            {
                result.BatsmanNonStrike = notOutBatsmen[^2].Name;
                result.BatsmanNonStrikeRuns = $"{notOutBatsmen[^2].Runs}({notOutBatsmen[^2].Balls})";
            }

            if (includeFullScorecard)
            {
                result.BattersBatted = battedByName.Values
                    .OrderByDescending(x => x.Runs)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                result.BattersYetToBat = knownBatters
                    .Where(name => !battedByName.ContainsKey(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => new BattingCardEntry { Name = name, Status = "YET_TO_BAT" })
                    .ToList();
            }
        }

        // Extract bowler (last bowler with overs on the batting side)
        if (stats.TryGetProperty(bowlingTeamKey, out var bowlingTeamStats) &&
            bowlingTeamStats.TryGetProperty("players", out var bowlingPlayers))
        {
            string? lastBowlerName = null;
            int bowlerRuns = 0, bowlerWickets = 0;
            double bowlerOvers = 0;
            var bowlersByName = new Dictionary<string, BowlingCardEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var player in bowlingPlayers.EnumerateArray())
            {
                var name = GetPlayerName(player);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!player.TryGetProperty("periodStatistics", out var periodStats)) continue;

                foreach (var ps in periodStats.EnumerateArray())
                {
                    var side = GetStr(ps, "side");

                    if (side == battingSide && ps.TryGetProperty("statistics", out var pStats))
                    {
                        int bOvers = 0, bRuns = 0, bWickets = 0;
                        bool hasBowlingStats = false;

                        foreach (var s in pStats.EnumerateArray())
                        {
                            var tv = GetNestedStr(s, "type", "value");
                            var cnt = s.TryGetProperty("count", out var cv) ? cv.GetDouble() : 0;

                            switch (tv)
                            {
                                case "OVERS": bOvers = (int)cnt; hasBowlingStats = true; break;
                                case "RUNS": bRuns = (int)cnt; break;
                                case "WICKETS": bWickets = (int)cnt; break;
                            }
                        }

                        if (hasBowlingStats && bOvers > 0)
                        {
                            lastBowlerName = name;
                            bowlerRuns = bRuns;
                            bowlerWickets = bWickets;
                            bowlerOvers = bOvers;

                            bowlersByName[name] = new BowlingCardEntry
                            {
                                Name = name,
                                Overs = bOvers.ToString(),
                                Maidens = 0,
                                Runs = bRuns,
                                Wickets = bWickets
                            };
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(lastBowlerName))
            {
                result.CurrentBowler = lastBowlerName;
                result.CurrentBowlerFigures = $"{bowlerWickets}/{bowlerRuns} ({bowlerOvers} ov)";
            }

            if (includeFullScorecard)
            {
                result.BowlersBowled = bowlersByName.Values
                    .OrderByDescending(x => x.Wickets)
                    .ThenBy(x => x.Runs)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (includeFullScorecard)
        {
            var hasAnyFullcardData = result.AllInnings.Count > 0 ||
                                     result.BattersBatted.Count > 0 ||
                                     result.BattersYetToBat.Count > 0 ||
                                     result.BowlersBowled.Count > 0;

            result.IsFullScorecardAvailable = hasAnyFullcardData;
            if (!hasAnyFullcardData && string.IsNullOrWhiteSpace(result.FullScorecardNote))
            {
                result.FullScorecardNote = "PlayHQ has not published scorecard details for this match state yet.";
            }
        }
    }

    private static string ToInningsLabel(string periodValue)
    {
        return periodValue switch
        {
            "FIRST_INNINGS" => "1st Innings",
            "SECOND_INNINGS" => "2nd Innings",
            "THIRD_INNINGS" => "3rd Innings",
            "FOURTH_INNINGS" => "4th Innings",
            _ => periodValue
        };
    }

    private static string GetPlayerName(JsonElement player)
    {
        if (!player.TryGetProperty("player", out var p))
            return "";

        if (p.TryGetProperty("profile", out var profile))
        {
            var first = GetStr(profile, "firstName");
            var last = GetStr(profile, "lastName");
            return $"{first} {last}".Trim();
        }

        return GetStr(p, "name");
    }

    // --- JSON helpers ---

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString();
        return "";
    }

    private static string GetNestedStr(JsonElement el, string obj, string prop)
    {
        if (el.TryGetProperty(obj, out var o) && o.TryGetProperty(prop, out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString();
        return "";
    }
}
