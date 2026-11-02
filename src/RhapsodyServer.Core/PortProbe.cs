using System.Net.NetworkInformation;

namespace RhapsodyServer.Core;

public static class PortProbe
{
    /// <summary>One cheap OS call; no sockets opened, no process spawned.</summary>
    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    public static int[] ListeningAmong(IEnumerable<int> ports)
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        return ports.Where(active.Contains).ToArray();
    }
}
