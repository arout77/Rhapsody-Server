using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace RhapsodyServer.Core.Profiling;

/// <summary>Finds which process owns the client end of a local TCP connection.</summary>
public interface IConnectionOwners
{
    /// <param name="clientPort">The client's (ephemeral) port.</param>
    /// <param name="serverPort">The port the client connected to.</param>
    (int? Pid, string? Name) Lookup(int clientPort, int serverPort);
}

public sealed class NullConnectionOwners : IConnectionOwners
{
    public static readonly NullConnectionOwners Instance = new();
    public (int? Pid, string? Name) Lookup(int clientPort, int serverPort) => (null, null);
}

/// <summary>
/// Windows: asks the TCP table which PID owns the connection. A php-cgi worker handles one request at a time,
/// so a worker's PID plus a time window identifies the request a query belongs to.
/// </summary>
public sealed class WindowsConnectionOwners : IConnectionOwners
{
    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

    public (int? Pid, string? Name) Lookup(int clientPort, int serverPort)
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        try
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            var buf = Marshal.AllocHGlobal(size + 4096);          // table can grow between the two calls
            try
            {
                size += 4096;
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return (null, null);
                var count = Marshal.ReadInt32(buf);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 x 4 bytes)
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * 24;
                    var local = NetPort(Marshal.ReadInt32(row, 8));
                    var remote = NetPort(Marshal.ReadInt32(row, 16));
                    if (local != clientPort || remote != serverPort) continue;
                    var pid = Marshal.ReadInt32(row, 20);
                    return (pid, NameOf(pid));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* best effort: attribution just falls back to the time window */ }
        return (null, null);
    }

    static int NetPort(int raw) => IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF)) & 0xFFFF;

    static string? NameOf(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return null; }
    }
}
