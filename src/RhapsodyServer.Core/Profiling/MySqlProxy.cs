using System.Net;
using System.Net.Sockets;

namespace RhapsodyServer.Core.Profiling;

/// <summary>
/// A passive TCP tee in front of MariaDB. Bytes are forwarded untouched in both directions (the one exception
/// is the server greeting, where the "TLS" and "compression" capability bits are cleared so the conversation
/// stays readable). A copy is parsed to log each statement and how long MariaDB took.
/// Works for any client: PHP, Node, Python, a GUI.
/// </summary>
public sealed class MySqlProxy : IDisposable
{
    readonly TcpListener _listener;
    readonly int _upstreamPort;
    readonly ProfileStore _store;
    readonly IConnectionOwners _owners;
    readonly CancellationTokenSource _cts = new();
    long _connections;
    int _started;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="listenPort">Port apps connect to (0 picks a free one, for tests).</param>
    public MySqlProxy(int listenPort, int upstreamPort, ProfileStore store, IConnectionOwners? owners = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _upstreamPort = upstreamPort;
        _store = store;
        _owners = owners ?? NullConnectionOwners.Instance;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var c = client;
        using var upstream = new TcpClient();
        try
        {
            c.NoDelay = true;
            upstream.NoDelay = true;
            await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort, _cts.Token);

            var clientPort = ((IPEndPoint)c.Client.RemoteEndPoint!).Port;
            var owner = _owners.Lookup(clientPort, Port);          // once, at connect: the process may be gone later
            var tap = new MySqlTap(Interlocked.Increment(ref _connections), clientPort, _store, () => owner);

            var cs = c.GetStream();
            var us = upstream.GetStream();
            if (!await RelayGreetingAsync(us, cs)) return;

            var up = PumpAsync(cs, us, tap.FromClient);
            var down = PumpAsync(us, cs, tap.FromServer);
            await Task.WhenAny(up, down);
            c.Dispose();                                           // unblocks the other direction
            upstream.Dispose();
            await Task.WhenAll(up, down);
        }
        catch { /* a dropped connection must never take the proxy down */ }
    }

    /// <summary>Reads the server's first packet, clears the TLS + compression bits, and forwards it.</summary>
    static async Task<bool> RelayGreetingAsync(NetworkStream from, NetworkStream to)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(from, header)) return false;
        var len = header[0] | (header[1] << 8) | (header[2] << 16);
        var payload = new byte[len];
        if (!await ReadExactAsync(from, payload)) return false;

        if (len > 0 && payload[0] == 10)                           // protocol v10 greeting
        {
            var nul = Array.IndexOf(payload, (byte)0, 1);          // end of the server version string
            var caps = nul + 1 + 4 + 8 + 1;                        // + thread id + scramble part 1 + filler
            if (nul > 0 && caps + 1 < len)
            {
                payload[caps] &= unchecked((byte)~0x20);           // CLIENT_COMPRESS
                payload[caps + 1] &= unchecked((byte)~0x08);       // CLIENT_SSL (0x0800)
            }
        }
        await to.WriteAsync(header);
        await to.WriteAsync(payload);
        return true;
    }

    static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got));
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    static async Task PumpAsync(NetworkStream from, NetworkStream to, Action<byte[], int> tap)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buf);
                if (n == 0) break;
                try { tap(buf, n); } catch { /* parsing is best effort; forwarding is not */ }
                await to.WriteAsync(buf.AsMemory(0, n));
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
