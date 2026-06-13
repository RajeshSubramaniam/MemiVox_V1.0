using System.Net.Http.Json;

namespace MyScorer.DeviceAgent.Services;

public class BackendApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BackendApiClient> _logger;
    private readonly string _backendUrl;

    public BackendApiClient(HttpClient http, IConfiguration config, ILogger<BackendApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _backendUrl = (config["BackendUrl"] ?? "http://localhost:5201").TrimEnd('/');
    }

    public async Task SendHeartbeatAsync(string deviceId, string name, bool atemConnected, bool streamActive, string networkStatus)
    {
        var payload = new
        {
            deviceId,
            name,
            deviceType = "raspberry-pi",
            atemConnected,
            streamActive,
            networkStatus
        };

        try
        {
            var response = await _http.PostAsJsonAsync($"{_backendUrl}/api/device/heartbeat", payload);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Heartbeat failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Heartbeat send error: {Message}", ex.Message);
        }
    }

    public async Task<(bool HasCommand, int CommandId, string Command, string RequestId)> PollNextCommandAsync(string deviceId)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<NextCommandResponse>($"{_backendUrl}/api/device/{deviceId}/next-command");
            if (response is { HasCommand: true })
                return (true, response.CommandId, response.Command, response.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Command poll error: {Message}", ex.Message);
        }
        return (false, 0, string.Empty, string.Empty);
    }

    public async Task AckCommandAsync(int commandId, string status)
    {
        try
        {
            await _http.PostAsJsonAsync($"{_backendUrl}/api/device/command/ack", new { commandId, status });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Command ack error: {Message}", ex.Message);
        }
    }

    private record NextCommandResponse(bool HasCommand, int CommandId, string Command, string RequestId);
}
