using Microsoft.AspNetCore.Mvc;
using MyScorer.Application.Services;
using MyScorer.Core;
using MyScorer.Core.Models;

namespace MyScorer.Api.Controllers;

[ApiController]
[Route("api/match")]
public class MatchController : ControllerBase
{
    private readonly IMatchStateService _matchState;
    private readonly IScoreExtractionService _scoreExtraction;

    public MatchController(IMatchStateService matchState, IScoreExtractionService scoreExtraction)
    {
        _matchState = matchState;
        _scoreExtraction = scoreExtraction;
    }

    /// <summary>
    /// Test endpoint: extract score directly from a URL without needing a registered match.
    /// </summary>
    [HttpGet("test-extract")]
    public async Task<IActionResult> TestExtract([FromQuery] string matchUrl, [FromQuery] string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(matchUrl))
            return BadRequest(new { message = "matchUrl query parameter is required." });

        var score = await _scoreExtraction.ExtractScoreAsync("test", matchUrl, providerType ?? "");
        return Ok(score);
    }

    [HttpGet("{setupId}")]
    public IActionResult Get(string setupId)
    {
        if (string.IsNullOrWhiteSpace(setupId) || !Validation.IdPattern().IsMatch(setupId))
            return BadRequest(new { message = "Invalid SetupId format." });

        var match = _matchState.GetMatch(setupId);
        return Ok(match);
    }

    [HttpPost("{setupId}/update")]
    public IActionResult Update(string setupId, [FromBody] MatchSnapshot request)
    {
        if (string.IsNullOrWhiteSpace(setupId) || !Validation.IdPattern().IsMatch(setupId))
            return BadRequest(new { message = "Invalid SetupId format." });

        var updated = _matchState.UpdateMatch(setupId, request);
        return Ok(updated);
    }
}