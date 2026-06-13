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

    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class UrlValidationResult
{
    public bool IsValid { get; set; }
    public string ProviderType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
