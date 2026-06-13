namespace MyScorer.Core.Models;

public class MatchSnapshot
{
    public string SetupId { get; set; } = "23082201";
    public string TeamA { get; set; } = "Sydney CC";
    public string TeamB { get; set; } = "Parramatta CC";
    public int Runs { get; set; } = 120;
    public int Wickets { get; set; } = 3;
    public string Overs { get; set; } = "18.2";
    public string Status { get; set; } = "Live";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
