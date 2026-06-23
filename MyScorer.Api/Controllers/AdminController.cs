using Microsoft.AspNetCore.Mvc;
using MyScorer.Application.Services;
using MyScorer.Application.Services.Providers;
using MyScorer.Core;
using MyScorer.Core.Models;

namespace MyScorer.Api.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminStateService _adminState;
    private readonly string _adminPassword;

    public AdminController(IAdminStateService adminState, IConfiguration configuration)
    {
        _adminState = adminState;
        _adminPassword = configuration["Admin:Password"] ?? "admin@myscorer2026";
    }

    private IActionResult? VerifyAdminAuth()
    {
        var password = Request.Headers["X-Admin-Password"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(password))
            return Unauthorized(new { message = "Admin authentication required." });
        if (password != _adminPassword)
            return Unauthorized(new { message = "Invalid admin password." });
        return null;
    }

    [HttpPost("validate-password")]
    public IActionResult ValidateAdminPassword([FromBody] AdminPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Password))
            return BadRequest(new { message = "Password is required." });
        return Ok(new { valid = request.Password == _adminPassword });
    }

    [HttpGet("setups")]
    public IActionResult GetSetups([FromQuery] string? query)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        return Ok(_adminState.GetSetups(query));
    }

    [HttpPost("setups")]
    public IActionResult RegisterSetup([FromBody] SetupRegistrationRequest request)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (request.StartDate == default ||
            string.IsNullOrWhiteSpace(request.OwnerName) ||
            string.IsNullOrWhiteSpace(request.OwnerEmail) ||
            string.IsNullOrWhiteSpace(request.CameraSerialNumber) ||
            string.IsNullOrWhiteSpace(request.YoloSerialNumber) ||
            string.IsNullOrWhiteSpace(request.PowerBankSerialNumber))
        {
            return BadRequest(new { message = "All setup registration fields (except SetupId and Password) are required." });
        }

        try
        {
            var setup = _adminState.RegisterSetup(request);
            return Ok(setup);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("clients")]
    public IActionResult GetClients([FromQuery] string? query)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        return Ok(_adminState.GetClients(query));
    }

    [HttpPost("clients/{setupId}")]
    public IActionResult UpdateClient(string setupId, [FromBody] ClientUpdateRequest request)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(setupId) || !Validation.IdPattern().IsMatch(setupId))
            return BadRequest(new { message = "Invalid SetupId format." });

        try
        {
            return Ok(_adminState.UpdateClient(setupId, request));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Client not found for the supplied setup ID." });
        }
    }

    [HttpGet("matches/{setupId}")]
    public IActionResult GetMatches(string setupId)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(setupId) || !Validation.IdPattern().IsMatch(setupId))
            return BadRequest(new { message = "Invalid SetupId format." });

        return Ok(_adminState.GetMatches(setupId));
    }

    [HttpGet("cricheroes-buildid")]
    public IActionResult GetCricHeroesBuildId()
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        return Ok(new { buildId = CricHeroesScraper.GetCachedBuildId() });
    }

    [HttpGet("cricheroes-buildid/test")]
    public async Task<IActionResult> TestCricHeroesBuildId([FromQuery] string buildId)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(buildId))
            return BadRequest(new { valid = false, reason = "missing", message = "buildId is required." });

        // Probe the _next/data API and verify if it still returns usable JSON.
        var testUrl = $"https://cricheroes.com/_next/data/{buildId.Trim()}/scorecard/25215069/battle-of-champions-4.0/skill-warriors-vs-max-daredevils/live.json";
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var request = new HttpRequestMessage(HttpMethod.Get, testUrl);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("x-nextjs-data", "1");
            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode && content.TrimStart().StartsWith("{") && content.Contains("\"pageProps\""))
                return Ok(new { valid = true, status = (int)response.StatusCode, reason = "active" });

            var lower = content.ToLowerInvariant();
            if (lower.Contains("__cf_chl") || lower.Contains("just a moment") || lower.Contains("cf-challenge"))
                return Ok(new { valid = false, status = (int)response.StatusCode, reason = "cloudflare_blocked" });

            if (lower.Contains("next.cricheroes.com/_next/static/chunks/") || lower.Contains("self.__next_f") || lower.Contains("bailout_to_client_side_rendering"))
                return Ok(new { valid = false, status = (int)response.StatusCode, reason = "app_shell_changed" });

            return Ok(new { valid = false, status = (int)response.StatusCode, reason = "stale_or_invalid" });
        }
        catch
        {
            return Ok(new { valid = false, status = 0, reason = "network_error" });
        }
    }

    [HttpPost("cricheroes-buildid")]
    public IActionResult SetCricHeroesBuildId([FromBody] BuildIdRequest request)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(request.BuildId))
            return BadRequest(new { message = "buildId is required." });

        CricHeroesScraper.SetBuildId(request.BuildId.Trim());
        return Ok(new { buildId = CricHeroesScraper.GetCachedBuildId() });
    }

    [HttpGet("cricheroes-session")]
    public IActionResult GetCricHeroesSessionCookie()
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        var cookie = CricHeroesScraper.GetSessionCookieHeader();
        var preview = string.IsNullOrWhiteSpace(cookie)
            ? null
            : (cookie.Length <= 24 ? cookie : cookie[..24] + "...");

        return Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(cookie),
            preview
        });
    }

    [HttpPost("cricheroes-session")]
    public IActionResult SetCricHeroesSessionCookie([FromBody] CricHeroesSessionRequest request)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        if (string.IsNullOrWhiteSpace(request.CookieHeader))
            return BadRequest(new { message = "cookieHeader is required." });

        CricHeroesScraper.SetSessionCookieHeader(request.CookieHeader);
        return Ok(new { configured = true });
    }

    [HttpDelete("cricheroes-session")]
    public IActionResult ClearCricHeroesSessionCookie()
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        CricHeroesScraper.ClearSessionCookieHeader();
        return Ok(new { configured = false });
    }

    [HttpGet("cricheroes-session/test")]
    public async Task<IActionResult> TestCricHeroesSessionCookie([FromQuery] int matchId = 25166753)
    {
        var authError = VerifyAdminAuth();
        if (authError != null) return authError;

        var cookie = CricHeroesScraper.GetSessionCookieHeader();
        var hasCookie = !string.IsNullOrWhiteSpace(cookie);

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var url = $"https://api.cricheroes.in/api/v1/scorecard/get-mini-scorecard/{matchId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("api-key", "cr!CkH3r0s");
            request.Headers.Add("device-type", "Chrome: 149.0.0.0");
            request.Headers.Add("origin", "https://cricheroes.com");
            request.Headers.Add("referer", "https://cricheroes.com/");
            request.Headers.Add("udid", "33f1796b2918055566db36fd2aa681a4");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");

            if (hasCookie)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            var lower = content.ToLowerInvariant();

            if (response.IsSuccessStatusCode && lower.Contains("\"status\":true") && lower.Contains("\"data\""))
            {
                return Ok(new
                {
                    valid = true,
                    status = (int)response.StatusCode,
                    reason = "active",
                    hasCookie
                });
            }

            if (lower.Contains("__cf_chl") || lower.Contains("just a moment") || lower.Contains("cf-challenge"))
            {
                return Ok(new
                {
                    valid = false,
                    status = (int)response.StatusCode,
                    reason = "cloudflare_blocked",
                    hasCookie
                });
            }

            return Ok(new
            {
                valid = false,
                status = (int)response.StatusCode,
                reason = "api_unavailable",
                hasCookie
            });
        }
        catch
        {
            return Ok(new
            {
                valid = false,
                status = 0,
                reason = "network_error",
                hasCookie
            });
        }
    }
}

public record BuildIdRequest(string BuildId);
public record CricHeroesSessionRequest(string CookieHeader);
public record AdminPasswordRequest(string Password);
