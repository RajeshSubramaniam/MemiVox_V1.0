using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public interface IMatchStateService
{
    MatchSnapshot GetMatch(string setupId);
    MatchSnapshot UpdateMatch(string setupId, MatchSnapshot snapshot);
}
