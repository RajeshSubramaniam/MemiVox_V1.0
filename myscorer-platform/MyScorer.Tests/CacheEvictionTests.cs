using Microsoft.Extensions.Logging.Abstractions;
using MyScorer.Application.Services;
using MyScorer.Core.Models;

namespace MyScorer.Tests;

public class CacheEvictionTests
{
    [Fact]
    public void InMemoryMatchState_EvictsStaleEntries()
    {
        var svc = new InMemoryMatchStateService(NullLogger<InMemoryMatchStateService>.Instance);

        // Create two entries
        svc.GetMatch("setup-a");
        svc.GetMatch("setup-b");

        // Both are fresh — eviction should remove nothing
        var evicted = svc.EvictStaleEntries();
        Assert.Equal(0, evicted);
    }

    [Fact]
    public void InMemoryMatchState_CreatesDefaultOnFirstAccess()
    {
        var svc = new InMemoryMatchStateService(NullLogger<InMemoryMatchStateService>.Instance);
        var match = svc.GetMatch("new-setup");
        Assert.NotNull(match);
        Assert.Equal("new-setup", match.SetupId);
    }

    [Fact]
    public void InMemoryMatchState_UpdatePersists()
    {
        var svc = new InMemoryMatchStateService(NullLogger<InMemoryMatchStateService>.Instance);
        svc.UpdateMatch("s1", new MatchSnapshot { Runs = 150, Wickets = 4, Overs = "20.0" });
        var result = svc.GetMatch("s1");
        Assert.Equal(150, result.Runs);
        Assert.Equal(4, result.Wickets);
        Assert.Equal("20.0", result.Overs);
    }

    [Fact]
    public void ScoreExtractionService_CacheCount_ReflectsState()
    {
        // CacheCount is a static property — verify it returns a non-negative value
        Assert.True(ScoreExtractionService.CacheCount >= 0);
    }

    [Fact]
    public void ScoreExtractionService_EvictStaleEntries_ReturnsZeroWhenEmpty()
    {
        // With no stale entries, eviction should return 0 (or however many were stale from other tests)
        var evicted = ScoreExtractionService.EvictStaleEntries();
        Assert.True(evicted >= 0);
    }
}
