namespace MyScorer.Core.Models;

public class MatchRegistrationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SetupId { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "Manual";
    public string MatchUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

public class CreateMatchRegistrationRequest
{
    public string ProviderType { get; set; } = "Manual";
    public string MatchUrl { get; set; } = string.Empty;
}
