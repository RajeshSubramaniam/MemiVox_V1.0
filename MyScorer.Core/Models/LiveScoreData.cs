namespace MyScorer.Core.Models;

public class LiveScoreData
{
    public string SetupId { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string MatchUrl { get; set; } = string.Empty;

    // Teams
    public string TeamA { get; set; } = string.Empty;
    public string TeamB { get; set; } = string.Empty;
    public string TeamASummary { get; set; } = string.Empty;
    public string TeamBSummary { get; set; } = string.Empty;

    // Current innings score
    public string BattingTeam { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int Wickets { get; set; }
    public string Overs { get; set; } = "0.0";

    // Batsmen at the crease
    public string BatsmanOnStrike { get; set; } = string.Empty;
    public string BatsmanOnStrikeRuns { get; set; } = string.Empty;
    public string BatsmanNonStrike { get; set; } = string.Empty;
    public string BatsmanNonStrikeRuns { get; set; } = string.Empty;

    // Current bowler
    public string CurrentBowler { get; set; } = string.Empty;
    public string CurrentBowlerFigures { get; set; } = string.Empty;

    // Target / chase info
    public string Target { get; set; } = string.Empty;
    public string RunRate { get; set; } = string.Empty;
    public string RequiredRunRate { get; set; } = string.Empty;

    // Extra info
    public string CurrentPartnership { get; set; } = string.Empty;
    public int ProjectedScore { get; set; }

    // Match info
    public string TossResult { get; set; } = string.Empty;
    public string MatchStatus { get; set; } = "Live";
    public string MatchSummary { get; set; } = string.Empty;

    // On-demand full scorecard details (only populated when requested)
    public bool IsFullScorecardAvailable { get; set; }
    public string FullScorecardNote { get; set; } = string.Empty;
    public List<InningsScoreLine> AllInnings { get; set; } = new();
    public List<BattingCardEntry> BattersBatted { get; set; } = new();
    public List<BattingCardEntry> BattersYetToBat { get; set; } = new();
    public List<BowlingCardEntry> BowlersBowled { get; set; } = new();

    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    // Resilience metadata: indicates if response is a temporary stale fallback.
    public bool IsStaleFallback { get; set; }
    public int StaleAgeSeconds { get; set; }
    public string WarningMessage { get; set; } = string.Empty;
}

public class InningsScoreLine
{
    public string TeamName { get; set; } = string.Empty;
    public string InningsLabel { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int Wickets { get; set; }
    public string Overs { get; set; } = string.Empty;
    public string ClosureStatus { get; set; } = string.Empty;
}

public class BattingCardEntry
{
    public string Name { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int Balls { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class BowlingCardEntry
{
    public string Name { get; set; } = string.Empty;
    public string Overs { get; set; } = string.Empty;
    public int Maidens { get; set; }
    public int Runs { get; set; }
    public int Wickets { get; set; }
}

public class UrlValidationResult
{
    public bool IsValid { get; set; }
    public string ProviderType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
