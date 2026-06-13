using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public interface IScoreExtractionService
{
    /// <summary>
    /// Validate a match URL belongs to a supported cricket score provider.
    /// </summary>
    Task<UrlValidationResult> ValidateMatchUrlAsync(string matchUrl, string providerType);

    /// <summary>
    /// Fetch and extract live score data from the provider match URL.
    /// </summary>
    Task<LiveScoreData> ExtractScoreAsync(string setupId, string matchUrl, string providerType);
}
