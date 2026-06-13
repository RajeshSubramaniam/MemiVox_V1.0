using System.Net.NetworkInformation;

namespace MyScorer.DeviceAgent.Services;

public class AtemDetector
{
    private readonly string _atemIp;

    public AtemDetector(IConfiguration config)
    {
        _atemIp = config["AtemIp"] ?? "192.168.1.100";
    }

    /// <summary>
    /// Check if ATEM is reachable on the network via ping.
    /// </summary>
    public bool IsAtemConnected()
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(_atemIp, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stub — stream status detection requires ATEM SDK integration.
    /// Returns false until ATEM SDK is wired up.
    /// </summary>
    public bool IsStreamActive() => false;

    /// <summary>
    /// Basic network health check.
    /// </summary>
    public string GetNetworkStatus()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable() ? "connected" : "disconnected";
        }
        catch
        {
            return "unknown";
        }
    }
}
