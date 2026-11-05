using System.Net.NetworkInformation;

namespace RhapsodyServer.Core;

public static class PortProbe
{
    /// <summary>One cheap OS call; no sockets opened, no process spawned.</summary>
    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>Really tries to connect (loopback, short timeout). Catches a listener the OS table doesn't show yet.</summary>
    public static bool CanConnect(int port, int timeoutMs = 300)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            return c.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(timeoutMs) && c.Connected;
        }
        catch { return false; }
    }

    public static bool IsOpen(int port) => IsListening(port) || CanConnect(port);

    public static int[] ListeningAmong(IEnumerable<int> ports)
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        return ports.Where(active.Contains).ToArray();
    }
}
