using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services.Providers;

/// <summary>
/// Connects to the PlayHQ spectator WebSocket endpoint (graphql-ws protocol) to fetch
/// live scoring data. The spectator HTTP endpoint returns 404 — only WebSocket is supported.
/// Designed as a singleton with a 15-second cache.
/// </summary>
public sealed class PlayHqSpectatorService : IAsyncDisposable
{
    private const string DefaultSpectatorWsUrl = "wss://spectator.playhq.com/graphql";
    private readonly string _spectatorWsUrl;
    private readonly ConcurrentDictionary<string, (LiveScoreData Data, DateTime CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private readonly ILogger<PlayHqSpectatorService> _logger;

    public PlayHqSpectatorService(string? spectatorWsUrl = null, ILogger<PlayHqSpectatorService>? logger = null)
    {
        _spectatorWsUrl = spectatorWsUrl ?? DefaultSpectatorWsUrl;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayHqSpectatorService>.Instance;
    }

    private static readonly string GameQuery = @"
subscription($id: ID!) {
  gameUpdated(id: $id) {
    id
    status
    updatedAt
    result {
      home {
        statistics { type { value } count }
        periods {
          period { value }
          statistics { type { value } count }
          type
          role
          closureStatus
        }
      }
      away {
        statistics { type { value } count }
        periods {
          period { value }
          statistics { type { value } count }
          type
          role
          closureStatus
        }
      }
      currentPeriod { value primarySide }
    }
    statistics {
      home {
        players {
          id
          name
          periodStatistics {
            period { value }
            side
            type
            statistics { type { value } count }
            status
            displayOrder
          }
        }
      }
      away {
        players {
          id
          name
          periodStatistics {
            period { value }
            side
            type
            statistics { type { value } count }
            status
            displayOrder
          }
        }
      }
    }
  }
}";

    public async Task<LiveScoreData?> ExtractLiveScoreAsync(string matchUrl, string gameId, string teamA, string teamB, string? tenantLabel = null, bool includeFullScorecard = false)
    {
        // Check cache first
        if (_cache.TryGetValue(gameId, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheDuration)
        {
            if (!includeFullScorecard)
                return cached.Data;

            var cachedHasFullcard = cached.Data.IsFullScorecardAvailable ||
                                    cached.Data.AllInnings.Count > 0 ||
                                    cached.Data.BattersBatted.Count > 0 ||
                                    cached.Data.BattersYetToBat.Count > 0 ||
                                    cached.Data.BowlersBowled.Count > 0;

            if (cachedHasFullcard)
                return cached.Data;
        }

        try
        {
            // Tenant label (e.g. "ca") comes from the API's tenantConfiguration.label
            var tenant = tenantLabel ?? "ca";
            var responseJson = await QuerySpectatorViaWebSocket(gameId, tenant);

            if (string.IsNullOrEmpty(responseJson))
            {
                _logger.LogWarning("No response from WebSocket for game {GameId}", gameId);
                return null;
            }

            _logger.LogDebug("Got WebSocket response ({Length} chars)", responseJson.Length);
            var result = ParseSpectatorResponse(responseJson, teamA, teamB, includeFullScorecard);
            if (result != null)
            {
                result.MatchUrl = matchUrl;
                result.ProviderType = "PlayHQ";
                result.ScrapedAt = DateTime.UtcNow;
                _cache[gameId] = (result, DateTime.UtcNow);
                return result;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Spectator error: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Remove cache entries older than 5 minutes. Called by MaintenanceService.
    /// </summary>
    public int EvictStaleCache()
    {
        var evicted = 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var key in _cache.Keys.ToList())
        {
            if (_cache.TryGetValue(key, out var entry) && entry.CachedAt < cutoff)
            {
                if (_cache.TryRemove(key, out _))
                    evicted++;
            }
        }
        return evicted;
    }

    /// <summary>
    /// Implements graphql-ws protocol over WebSocket:
    /// 1. Connect to wss://spectator.playhq.com/graphql?tenant={tenant}
    /// 2. Send ConnectionInit
    /// 3. Wait for ConnectionAck
    /// 4. Send Subscribe with query
    /// 5. Receive Next message with data
    /// 6. Send Complete and close
    /// </summary>
    private async Task<string?> QuerySpectatorViaWebSocket(string gameId, string tenant)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        ws.Options.SetRequestHeader("Origin", "https://www.playhq.com");
        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36");

        var uri = new Uri($"{_spectatorWsUrl}?tenant={Uri.EscapeDataString(tenant)}");
        // Keep live overlay startup responsive. If spectator is slow/unavailable,
        // fail fast so callers can fall back instead of hanging for ~30 seconds.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        _logger.LogDebug("Connecting WebSocket to {Uri}", uri);
        await ws.ConnectAsync(uri, cts.Token);
        _logger.LogDebug("Connected. SubProtocol: {SubProtocol}", ws.SubProtocol);

        // Step 1: ConnectionInit
        await SendJsonAsync(ws, new { type = "connection_init" }, cts.Token);

        // Step 2: Wait for ConnectionAck
        var ackMsg = await ReceiveJsonAsync(ws, cts.Token);
        if (ackMsg == null || GetStr(ackMsg.Value, "type") != "connection_ack")
        {
            _logger.LogWarning("Expected connection_ack, got: {Msg}", ackMsg?.ToString());
            return null;
        }
        _logger.LogDebug("Connection acknowledged");

        // Step 3: Subscribe with game query
        var subscribePayload = new
        {
            id = "1",
            type = "subscribe",
            payload = new
            {
                query = GameQuery,
                variables = new { id = gameId }
            }
        };
        await SendJsonAsync(ws, subscribePayload, cts.Token);

        // Step 4: Receive data (Next message)
        string? resultJson = null;
        while (true)
        {
            var msg = await ReceiveJsonAsync(ws, cts.Token);
            if (msg == null) break;

            var msgType = GetStr(msg.Value, "type");
            _logger.LogDebug("Received message type: {MsgType}", msgType);

            if (msgType == "next" && msg.Value.TryGetProperty("payload", out var payload))
            {
                resultJson = payload.ToString();
                break;
            }
            else if (msgType == "error")
            {
                _logger.LogWarning("Spectator error response: {Msg}", msg.Value.ToString());
                break;
            }
            else if (msgType == "complete")
            {
                break;
            }
        }

        // Step 5: Complete and close
        try
        {
            await SendJsonAsync(ws, new { id = "1", type = "complete" }, cts.Token);
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }
        catch { /* best-effort cleanup */ }

        return resultJson;
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static LiveScoreData? ParseSpectatorResponse(string json, string teamA, string teamB, bool includeFullScorecard)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
                return null;

            if (!data.TryGetProperty("game", out var game) && !data.TryGetProperty("gameUpdated", out game))
                return null;

            if (game.ValueKind != JsonValueKind.Object)
                return null;

            var result = new LiveScoreData
            {
                TeamA = teamA,
                TeamB = teamB,
                MatchStatus = "Live",
                IsValid = true
            };

            // Status
            if (game.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var statusVal = status.GetString() ?? "";
                result.MatchStatus = statusVal switch
                {
                    "FINAL" => "Completed",
                    "LIVE" or "IN_PROGRESS" => "Live",
                    "PRE_GAME" or "SCHEDULED" => "Scheduled",
                    _ => "Live"
                };
            }

            // Result contains periods (innings) with scores
            if (game.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
                ParseSpectatorResult(resultEl, result, includeFullScorecard);

            // Player statistics
            if (game.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
                ParseSpectatorPlayerStats(stats, result, includeFullScorecard);

            if (includeFullScorecard)
            {
                var hasAnyFullcardData = result.AllInnings.Count > 0 ||
                                         result.BattersBatted.Count > 0 ||
                                         result.BattersYetToBat.Count > 0 ||
                                         result.BowlersBowled.Count > 0;
                result.IsFullScorecardAvailable = hasAnyFullcardData;
                if (!hasAnyFullcardData)
                    result.FullScorecardNote = "PlayHQ spectator feed has limited scorecard details for this live state.";
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseSpectatorResult(JsonElement resultEl, LiveScoreData result, bool includeFullScorecard)
    {
        var homeInnings = ExtractInnings(resultEl, "home");
        var awayInnings = ExtractInnings(resultEl, "away");

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

        // Format summaries
        result.TeamASummary = FormatInnings(homeInnings);
        result.TeamBSummary = FormatInnings(awayInnings);

        // Current period tells us who is batting
        string? currentSide = null;
        if (resultEl.TryGetProperty("currentPeriod", out var cp) && cp.ValueKind == JsonValueKind.Object)
        {
            if (cp.TryGetProperty("primarySide", out var ps) && ps.ValueKind == JsonValueKind.String)
                currentSide = ps.GetString();
        }

        // Determine current batting innings
        InningsInfo? currentInnings = null;
        bool currentIsHome = true;

        if (currentSide == "HOME" && homeInnings.Count > 0)
        {
            currentInnings = homeInnings[^1];
            currentIsHome = true;
        }
        else if (currentSide == "AWAY" && awayInnings.Count > 0)
        {
            currentInnings = awayInnings[^1];
            currentIsHome = false;
        }
        else
        {
            // Fallback: find the active innings (IN_PROGRESS)
            var activeHome = homeInnings.FindLast(i => i.ClosureStatus == "IN_PROGRESS");
            var activeAway = awayInnings.FindLast(i => i.ClosureStatus == "IN_PROGRESS");

            if (activeAway != null) { currentInnings = activeAway; currentIsHome = false; }
            else if (activeHome != null) { currentInnings = activeHome; currentIsHome = true; }
            else if (awayInnings.Count > 0) { currentInnings = awayInnings[^1]; currentIsHome = false; }
            else if (homeInnings.Count > 0) { currentInnings = homeInnings[^1]; currentIsHome = true; }
        }

        if (currentInnings == null) return;

        result.BattingTeam = currentIsHome ? result.TeamA : result.TeamB;
        result.Runs = currentInnings.Runs;
        result.Wickets = currentInnings.Wickets;
        result.Overs = currentInnings.Overs;

        if (double.TryParse(currentInnings.Overs, out var ov) && ov > 0)
            result.RunRate = (currentInnings.Runs / ov).ToString("0.00");

        // Target calculation
        var opponentInnings = currentIsHome ? awayInnings : homeInnings;
        var currentTeamInnings = currentIsHome ? homeInnings : awayInnings;
        int opponentTotal = opponentInnings.Sum(i => i.Runs);
        int currentTeamPrevious = currentTeamInnings.Where(i => i != currentInnings).Sum(i => i.Runs);
        if (opponentTotal > 0)
        {
            var target = opponentTotal - currentTeamPrevious + 1;
            if (target > 0) result.Target = target.ToString();
        }
    }

    private static void ParseSpectatorPlayerStats(JsonElement stats, LiveScoreData result, bool includeFullScorecard)
    {
        var battingIsHome = result.BattingTeam == result.TeamA;
        var battingSide = battingIsHome ? "HOME" : "AWAY";
        var battingKey = battingIsHome ? "home" : "away";
        var bowlingKey = battingIsHome ? "away" : "home";

        // Batsmen
        if (stats.TryGetProperty(battingKey, out var battingStats) &&
            battingStats.TryGetProperty("players", out var battingPlayers))
        {
            var notOut = new List<(string Name, int Runs, int Balls, int DisplayOrder)>();
            var battedByName = new Dictionary<string, BattingCardEntry>(StringComparer.OrdinalIgnoreCase);
            var knownBatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var player in battingPlayers.EnumerateArray())
            {
                var name = GetPlayerName(player);
                if (string.IsNullOrWhiteSpace(name)) continue;
                knownBatters.Add(name);
                if (!player.TryGetProperty("periodStatistics", out var ps)) continue;

                foreach (var period in ps.EnumerateArray())
                {
                    var side = GetStr(period, "side");
                    var status = GetStr(period, "status");
                    if (string.Equals(side, battingSide, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(status, "NOT_OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        int runs = 0, balls = 0, displayOrder = 0;
                        if (period.TryGetProperty("displayOrder", out var dov) && dov.ValueKind == JsonValueKind.Number)
                            displayOrder = (int)dov.GetDouble();
                        if (period.TryGetProperty("statistics", out var pStats))
                        {
                            foreach (var s in pStats.EnumerateArray())
                            {
                                var tv = GetNestedStr(s, "type", "value");
                                var cnt = s.TryGetProperty("count", out var cv) ? (int)cv.GetDouble() : 0;
                                if (tv == "TOTAL_RUNS" || tv == "CURRENT_RUNS") runs = cnt;
                                else if (tv == "BALLS_FACED" || tv == "CURRENT_BALLS_FACED") balls = cnt;
                            }
                        }
                        notOut.Add((name, runs, balls, displayOrder));
                    }

                    if (includeFullScorecard && string.Equals(side, battingSide, StringComparison.OrdinalIgnoreCase))
                    {
                        int runs = 0, balls = 0;
                        bool hasBattingStats = false;

                        if (period.TryGetProperty("statistics", out var pStats))
                        {
                            foreach (var s in pStats.EnumerateArray())
                            {
                                var tv = GetNestedStr(s, "type", "value");
                                var cnt = s.TryGetProperty("count", out var cv) ? (int)cv.GetDouble() : 0;
                                if (tv == "TOTAL_RUNS" || tv == "CURRENT_RUNS") { runs = cnt; hasBattingStats = true; }
                                else if (tv == "BALLS_FACED" || tv == "CURRENT_BALLS_FACED") { balls = cnt; hasBattingStats = true; }
                            }
                        }

                        if (hasBattingStats)
                        {
                            battedByName[name] = new BattingCardEntry
                            {
                                Name = name,
                                Runs = runs,
                                Balls = balls,
                                Status = status
                            };
                        }
                    }
                }
            }

            // Sort by displayOrder so higher = most recently at crease
            notOut.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));

            if (notOut.Count >= 1)
            {
                result.BatsmanOnStrike = notOut[^1].Name;
                result.BatsmanOnStrikeRuns = $"{notOut[^1].Runs}({notOut[^1].Balls})";
            }
            if (notOut.Count >= 2)
            {
                result.BatsmanNonStrike = notOut[^2].Name;
                result.BatsmanNonStrikeRuns = $"{notOut[^2].Runs}({notOut[^2].Balls})";
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

        // Bowler
        if (stats.TryGetProperty(bowlingKey, out var bowlingStats) &&
            bowlingStats.TryGetProperty("players", out var bowlingPlayers))
        {
            string? currentBowlerName = null;
            int cbRuns = 0, cbWickets = 0;
            double cbOvers = 0;
            bool foundCurrentBowler = false;
            var bowlersByName = new Dictionary<string, BowlingCardEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var player in bowlingPlayers.EnumerateArray())
            {
                var name = GetPlayerName(player);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!player.TryGetProperty("periodStatistics", out var ps)) continue;

                foreach (var period in ps.EnumerateArray())
                {
                    var side = GetStr(period, "side");
                    if (!string.Equals(side, battingSide, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!period.TryGetProperty("statistics", out var pStats)) continue;

                    double overs = 0;
                    int runs = 0, wickets = 0, currentBalls = 0;
                    bool hasBowling = false;

                    foreach (var s in pStats.EnumerateArray())
                    {
                        var tv = GetNestedStr(s, "type", "value");
                        var cnt = s.TryGetProperty("count", out var cv) ? cv.GetDouble() : 0;
                        switch (tv)
                        {
                            case "OVERS": overs = cnt; hasBowling = true; break;
                            case "OVERS_BOWLED": overs = cnt; hasBowling = true; break;
                            case "RUNS": runs = (int)cnt; break;
                            case "WICKETS": wickets = (int)cnt; break;
                            case "CURRENT_BALLS": currentBalls = (int)cnt; break;
                        }
                    }

                    if (hasBowling && overs > 0)
                    {
                        // Prefer the bowler currently bowling (CURRENT_BALLS > 0)
                        if (currentBalls > 0)
                        {
                            currentBowlerName = name;
                            cbRuns = runs;
                            cbWickets = wickets;
                            cbOvers = overs;
                            foundCurrentBowler = true;
                        }
                        else if (!foundCurrentBowler)
                        {
                            currentBowlerName = name;
                            cbRuns = runs;
                            cbWickets = wickets;
                            cbOvers = overs;
                        }

                        if (includeFullScorecard)
                        {
                            bowlersByName[name] = new BowlingCardEntry
                            {
                                Name = name,
                                Overs = overs.ToString("0.#"),
                                Maidens = 0,
                                Runs = runs,
                                Wickets = wickets
                            };
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(currentBowlerName))
            {
                result.CurrentBowler = currentBowlerName;
                result.CurrentBowlerFigures = $"{cbWickets}/{cbRuns} ({cbOvers:0.#} ov)";
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
    }

    private record InningsInfo(string PeriodValue, int Runs, int Wickets, string Overs, string? ClosureStatus);

    private static List<InningsInfo> ExtractInnings(JsonElement resultEl, string teamKey)
    {
        var list = new List<InningsInfo>();
        if (!resultEl.TryGetProperty(teamKey, out var team)) return list;
        if (!team.TryGetProperty("periods", out var periods)) return list;

        foreach (var period in periods.EnumerateArray())
        {
            // Spectator often emits both BATTING and BOWLING role entries for the same period.
            // Only BATTING entries contain innings totals we want for scorecard lines.
            var role = GetStr(period, "role");
            if (!string.IsNullOrWhiteSpace(role) && !role.Equals("BATTING", StringComparison.OrdinalIgnoreCase))
                continue;

            var periodValue = GetNestedStr(period, "period", "value");
            var closure = period.TryGetProperty("closureStatus", out var cs) && cs.ValueKind == JsonValueKind.String
                ? cs.GetString() : null;
            int runs = 0, wickets = 0;
            string overs = "0.0";

            if (period.TryGetProperty("statistics", out var stats))
            {
                foreach (var stat in stats.EnumerateArray())
                {
                    var tv = GetNestedStr(stat, "type", "value");
                    var cnt = stat.TryGetProperty("count", out var c) ? c.GetDouble() : 0;
                    switch (tv)
                    {
                        case "TOTAL_SCORE": runs = (int)cnt; break;
                        case "TOTAL_OUTS": wickets = (int)cnt; break;
                        case "TOTAL_OVERS": overs = cnt.ToString("0.#"); break;
                    }
                }
            }
            // Keep innings rows that have meaningful totals, or active rows (open closure status).
            if (runs > 0 || wickets > 0 || overs != "0.0" || string.IsNullOrEmpty(closure))
            {
                list.Add(new InningsInfo(periodValue, runs, wickets, overs, closure));
            }
        }
        return list;
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

    private static string FormatInnings(List<InningsInfo> innings)
    {
        if (innings.Count == 0) return "Yet to bat";
        var parts = innings.Select(i =>
        {
            var suffix = i.ClosureStatus == "DECLARED" ? "d" : "";
            return i.Wickets == 10 ? $"{i.Runs}{suffix}" : $"{i.Runs}/{i.Wickets}{suffix}";
        });
        return string.Join(" & ", parts);
    }

    private static string GetPlayerName(JsonElement player)
    {
        if (!player.TryGetProperty("player", out var p) &&
            !player.TryGetProperty("id", out _))
            return "";

        // spectator schema: player has name directly or profile with firstName/lastName
        if (player.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            return nameEl.GetString() ?? "";

        if (player.TryGetProperty("player", out var pl))
        {
            if (pl.TryGetProperty("profile", out var profile))
            {
                var first = GetStr(profile, "firstName");
                var last = GetStr(profile, "lastName");
                return $"{first} {last}".Trim();
            }
            if (pl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                return n.GetString() ?? "";
        }

        return "";
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return "";
    }

    private static string GetNestedStr(JsonElement el, string obj, string prop)
    {
        if (el.TryGetProperty(obj, out var o) && o.TryGetProperty(prop, out var p))
            return p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString();
        return "";
    }

    public ValueTask DisposeAsync()
    {
        _cache.Clear();
        return ValueTask.CompletedTask;
    }
}
