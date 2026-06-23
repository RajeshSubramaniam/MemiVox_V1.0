using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyScorer.Application.Services.Providers;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public class ScoreExtractionService : IScoreExtractionService
{
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IProviderScraper> _scrapers;
    private readonly ILogger<ScoreExtractionService> _logger;

    // Cache scraped scores for 7 seconds — overlay polls every 8s so each poll gets fresh data
    private static readonly ConcurrentDictionary<string, (LiveScoreData Data, DateTime CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(7);

    public static int CacheCount => _cache.Count;

    public ScoreExtractionService(HttpClient httpClient, ILogger<ScoreExtractionService> logger, PlayHqSpectatorService? spectatorService = null)
    {
        _httpClient = httpClient;
        _logger = logger;

        _scrapers = new List<IProviderScraper>
        {
            new PlayHqScraper(spectatorService),
            new CricHeroesScraper()
        };
    }

    public async Task<UrlValidationResult> ValidateMatchUrlAsync(string matchUrl, string providerType)
    {
        if (string.IsNullOrWhiteSpace(matchUrl))
        {
            return new UrlValidationResult { IsValid = false, Message = "Match URL is required." };
        }

        if (!Uri.TryCreate(matchUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new UrlValidationResult { IsValid = false, Message = "Invalid URL format. Must be http or https." };
        }

        var scraper = ResolveScraper(matchUrl, providerType);
        if (scraper == null)
        {
            return new UrlValidationResult
            {
                IsValid = false,
                Message = $"Unsupported provider. URL must be from a supported platform (PlayHQ, CricHeroes)."
            };
        }

        _logger.LogInformation("Validating {Provider} match URL: {Url}", scraper.ProviderName, matchUrl);
        return await scraper.ValidateAsync(matchUrl, _httpClient);
    }

    public async Task<LiveScoreData> ExtractScoreAsync(string setupId, string matchUrl, string providerType)
    {
        var cacheKey = $"{setupId}:{matchUrl}";

        // Return cached data if fresh enough
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < CacheDuration)
        {
            _logger.LogDebug("Returning cached score for SetupId {SetupId}.", setupId);
            return cached.Data;
        }

        var scraper = ResolveScraper(matchUrl, providerType);
        if (scraper == null)
        {
            return new LiveScoreData
            {
                SetupId = setupId,
                MatchUrl = matchUrl,
                ProviderType = providerType,
                ErrorMessage = "No scraper available for this provider.",
                ScrapedAt = DateTime.UtcNow
            };
        }

        _logger.LogInformation("Extracting score for SetupId {SetupId} from {Provider}.", setupId, scraper.ProviderName);

        LiveScoreData? result = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            result = await scraper.ExtractAsync(matchUrl, _httpClient);
            result.SetupId = setupId;

            if (result.IsValid || string.IsNullOrEmpty(result.ErrorMessage))
                break;

            if (attempt < 2)
            {
                _logger.LogWarning("Attempt {Attempt} failed for SetupId {SetupId}: {Error}. Retrying...", attempt, setupId, result.ErrorMessage);
                await Task.Delay(1000);
            }
        }

        // On failure, serve stale cache if available
        if (result != null && !result.IsValid && !string.IsNullOrEmpty(result.ErrorMessage))
        {
            if (_cache.TryGetValue(cacheKey, out var stale) && stale.Data.IsValid)
            {
                _logger.LogWarning("Serving stale cached score for SetupId {SetupId} due to extraction failure.", setupId);
                return stale.Data;
            }
        }

        // Cache successful result
        if (result != null && (result.IsValid || string.IsNullOrEmpty(result.ErrorMessage)))
        {
            _cache[cacheKey] = (result, DateTime.UtcNow);
        }

        return result!;
    }

    private IProviderScraper? ResolveScraper(string matchUrl, string providerType)
    {
        // Try to match by URL first (most reliable)
        var byUrl = _scrapers.FirstOrDefault(s => s.CanHandle(matchUrl));
        if (byUrl != null) return byUrl;

        // Fall back to provider type hint
        if (!string.IsNullOrWhiteSpace(providerType))
        {
            return _scrapers.FirstOrDefault(s =>
                s.ProviderName.Equals(providerType, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Remove cache entries older than 5 minutes. Called by MaintenanceService.
    /// </summary>
    public static int EvictStaleEntries()
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
}
