using MyScorer.Core.Models;

namespace MyScorer.Application.Services.Providers;

public interface IProviderScraper
{
    string ProviderName { get; }

    /// <summary>
    /// Check if this URL matches the expected pattern for this provider.
    /// </summary>
    bool CanHandle(string matchUrl);

    /// <summary>
    /// Validate the page content contains expected cricket score markers.
    /// </summary>
    Task<UrlValidationResult> ValidateAsync(string matchUrl, HttpClient httpClient);

    /// <summary>
    /// Scrape and extract live score data from the page content.
    /// </summary>
    Task<LiveScoreData> ExtractAsync(string matchUrl, HttpClient httpClient);
}
