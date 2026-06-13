namespace MyScorer.DeviceAgent.Services;

public class HeartbeatWorker : BackgroundService
{
    private readonly BackendApiClient _apiClient;
    private readonly AtemDetector _atemDetector;
    private readonly IConfiguration _config;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        BackendApiClient apiClient,
        AtemDetector atemDetector,
        IConfiguration config,
        ILogger<HeartbeatWorker> logger)
    {
        _apiClient = apiClient;
        _atemDetector = atemDetector;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deviceId = _config["DeviceId"] ?? "UNKNOWN";
        var deviceName = _config["DeviceName"] ?? Environment.MachineName;
        _logger.LogInformation("Device agent started for DeviceId: {DeviceId}", deviceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var atemConnected = _atemDetector.IsAtemConnected();
                var streamActive = _atemDetector.IsStreamActive();
                var networkStatus = _atemDetector.GetNetworkStatus();

                _logger.LogDebug("ATEM={Atem}, Stream={Stream}, Network={Network}",
                    atemConnected, streamActive, networkStatus);

                await _apiClient.SendHeartbeatAsync(deviceId, deviceName, atemConnected, streamActive, networkStatus);

                // Poll for commands
                var (hasCmd, cmdId, command, requestId) = await _apiClient.PollNextCommandAsync(deviceId);
                if (hasCmd)
                {
                    _logger.LogInformation("Received command: {Command} (id={Id}, requestId={RequestId})", command, cmdId, requestId);
                    // TODO: Execute command via ATEM SDK
                    // For now, acknowledge as completed
                    await _apiClient.AckCommandAsync(cmdId, "Completed");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat cycle error");
            }

            try { await Task.Delay(5000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
