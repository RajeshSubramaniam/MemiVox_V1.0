using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using MyScorer.Application.Services;
using MyScorer.Core;
using MyScorer.Core.Models;

namespace MyScorer.Api.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    private readonly IMatchRegistrationService _registrations;
    private readonly IAdminStateService _adminState;
    private readonly IScoreExtractionService _scoreExtraction;
    private readonly string _adminPassword;

    // Rate limiting: track failed password attempts per setupId
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _failedAttempts = new();
    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    public SetupController(
        IMatchRegistrationService registrations,
        IAdminStateService adminState,
        IScoreExtractionService scoreExtraction,
        IConfiguration configuration)
    {
        _registrations = registrations;
        _adminState = adminState;
        _scoreExtraction = scoreExtraction;
        _adminPassword = configuration["Admin:Password"] ?? "admin@myscorer2026";
    }

    private IActionResult? ValidateSetupId(string setupId)
    {
        if (string.IsNullOrWhiteSpace(setupId) || !Validation.IdPattern().IsMatch(setupId))
            return BadRequest(new { message = "Invalid SetupId format." });
        return null;
    }

    /// <summary>
    /// Verify the X-Setup-Password header against the stored password for this setup.
    /// Returns null on success, or an IActionResult with the error on failure.
    /// </summary>
    private IActionResult? VerifyAuthHeader(string setupId)
    {
        // Admin password bypasses per-setup auth
        var adminPwd = Request.Headers["X-Admin-Password"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(adminPwd) && adminPwd == _adminPassword)
            return null;

        var password = Request.Headers["X-Setup-Password"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(password))
            return Unauthorized(new { message = "Authentication required. Provide X-Setup-Password header." });

        if (!_adminState.ValidateClientPassword(setupId, password))
            return Unauthorized(new { message = "Invalid password." });

        return null;
    }

    /// <summary>
    /// Check rate limiting for password validation attempts.
    /// Returns null if under the limit, or a 429 response if rate-limited.
    /// </summary>
    private IActionResult? CheckRateLimit(string setupId)
    {
        var now = DateTime.UtcNow;
        if (_failedAttempts.TryGetValue(setupId, out var record))
        {
            if (now - record.WindowStart < RateLimitWindow)
            {
                if (record.Count >= MaxAttemptsPerWindow)
                {
                    var retryAfter = (int)(RateLimitWindow - (now - record.WindowStart)).TotalSeconds + 1;
                    Response.Headers["Retry-After"] = retryAfter.ToString();
                    return StatusCode(429, new { message = $"Too many attempts. Try again in {retryAfter} seconds." });
                }
            }
            else
            {
                // Window expired, reset
                _failedAttempts.TryRemove(setupId, out _);
            }
        }
        return null;
    }

    private void RecordFailedAttempt(string setupId)
    {
        var now = DateTime.UtcNow;
        _failedAttempts.AddOrUpdate(setupId,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart < RateLimitWindow
                ? (existing.Count + 1, existing.WindowStart)
                : (1, now));
    }

    private void ClearFailedAttempts(string setupId)
    {
        _failedAttempts.TryRemove(setupId, out _);
    }

    internal static int EvictStaleRateLimits()
    {
        var evicted = 0;
        var now = DateTime.UtcNow;
        foreach (var key in _failedAttempts.Keys.ToList())
        {
            if (_failedAttempts.TryGetValue(key, out var record) && now - record.WindowStart >= RateLimitWindow)
            {
                if (_failedAttempts.TryRemove(key, out _))
                    evicted++;
            }
        }
        return evicted;
    }

    [HttpGet("{setupId}/active-match")]
    public IActionResult GetActiveMatch(string setupId)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        var authError = VerifyAuthHeader(setupId);
        if (authError != null) return authError;

        var active = _registrations.GetActiveRegistration(setupId);
        if (active == null)
        {
            return Ok(new { setupId, provider = "None", matchUrl = string.Empty, isActive = false });
        }

        return Ok(new
        {
            setupId = active.SetupId,
            provider = active.ProviderType,
            providerType = active.ProviderType,
            matchUrl = active.MatchUrl,
            createdAt = active.CreatedAt,
            isActive = active.IsActive
        });
    }

    [HttpGet("{setupId}/matches")]
    public IActionResult GetMatches(string setupId)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        var authError = VerifyAuthHeader(setupId);
        if (authError != null) return authError;

        return Ok(_registrations.GetRegistrationsForSetup(setupId));
    }

    [HttpPost("{setupId}/validate-password")]
    public IActionResult ValidatePassword(string setupId, [FromBody] PasswordValidationRequest request)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        if (request == null || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Password is required." });
        }

        var rateLimited = CheckRateLimit(setupId);
        if (rateLimited != null) return rateLimited;

        var isValid = _adminState.ValidateClientPassword(setupId, request.Password);

        if (!isValid)
            RecordFailedAttempt(setupId);
        else
            ClearFailedAttempts(setupId);

        return Ok(new { valid = isValid });
    }

    [HttpPost("{setupId}/change-password")]
    public IActionResult ChangePassword(string setupId, [FromBody] ChangePasswordRequest request)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        if (request == null || string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { message = "Current password and new password are required." });

        if (request.NewPassword.Length < 4)
            return BadRequest(new { message = "New password must be at least 4 characters." });

        if (!_adminState.ValidateClientPassword(setupId, request.CurrentPassword))
            return BadRequest(new { message = "Current password is incorrect." });

        _adminState.UpdateClient(setupId, new ClientUpdateRequest { Password = request.NewPassword });
        return Ok(new { message = "Password changed successfully." });
    }

    [HttpPost("{setupId}/matches")]
    public async Task<IActionResult> CreateMatch(string setupId, [FromBody] CreateMatchRegistrationRequest request)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        var authError = VerifyAuthHeader(setupId);
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(request.MatchUrl))
        {
            return BadRequest(new { message = "MatchUrl is required." });
        }

        // Validate the URL is a real cricket score page
        var validation = await _scoreExtraction.ValidateMatchUrlAsync(request.MatchUrl, request.ProviderType);
        if (!validation.IsValid)
        {
            return BadRequest(new { message = validation.Message, provider = validation.ProviderType });
        }

        // Auto-set provider from validation if not provided
        if (string.IsNullOrWhiteSpace(request.ProviderType) && !string.IsNullOrWhiteSpace(validation.ProviderType))
        {
            request.ProviderType = validation.ProviderType;
        }

        try
        {
            var created = _registrations.CreateRegistration(setupId, request);
            return Ok(created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{setupId}/live-score")]
    public async Task<IActionResult> GetLiveScore(string setupId)
    {
        var invalid = ValidateSetupId(setupId);
        if (invalid != null) return invalid;

        var active = _registrations.GetActiveRegistration(setupId);
        if (active == null || string.IsNullOrWhiteSpace(active.MatchUrl))
        {
            return Ok(new LiveScoreData
            {
                SetupId = setupId,
                MatchStatus = "NoMatch",
                ErrorMessage = "No active match registered for this setup."
            });
        }

        var score = await _scoreExtraction.ExtractScoreAsync(setupId, active.MatchUrl, active.ProviderType);

        if (score.MatchStatus == "Completed")
        {
            _registrations.CompleteActiveRegistration(setupId);
        }

        return Ok(score);
    }
}

public sealed class PasswordValidationRequest
{
    public string Password { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
