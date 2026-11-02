using System.Diagnostics;
using System.Text;

namespace RhapsodyServer.Core.Profiling;

public delegate void PacketHandler(byte sequence, ReadOnlySpan<byte> payloadPrefix, int payloadLength);

/// <summary>
/// Reads one direction of a MySQL/MariaDB connection and reports complete packets. Never alters the data:
/// it only keeps the first bytes of each packet (enough for the SQL text) and counts the rest.
/// </summary>
public sealed class PacketReader
{
    public const int KeepBytes = 16 * 1024;

    readonly byte[] _header = new byte[4];
    int _headerHave;
    int _payloadLen, _payloadSeen;
    byte _seq;
    byte[] _keep = new byte[256];
    int _kept;

    /// <summary>Called with (sequence id, first bytes of the payload, full payload length) once a packet is complete.</summary>
    public void Feed(ReadOnlySpan<byte> data, PacketHandler onPacket)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (_headerHave < 4)
            {
                var take = Math.Min(4 - _headerHave, data.Length - i);
                data.Slice(i, take).CopyTo(_header.AsSpan(_headerHave));
                _headerHave += take; i += take;
                if (_headerHave < 4) return;
                _payloadLen = _header[0] | (_header[1] << 8) | (_header[2] << 16);
                _seq = _header[3];
                _payloadSeen = 0; _kept = 0;
                if (_payloadLen == 0) { Complete(onPacket); }
                continue;
            }

            var n = Math.Min(_payloadLen - _payloadSeen, data.Length - i);
            var room = KeepBytes - _kept;
            if (room > 0)
            {
                var c = Math.Min(room, n);
                if (_kept + c > _keep.Length) Array.Resize(ref _keep, Math.Min(KeepBytes, Math.Max(_keep.Length * 2, _kept + c)));
                data.Slice(i, c).CopyTo(_keep.AsSpan(_kept));
                _kept += c;
            }
            _payloadSeen += n; i += n;
            if (_payloadSeen == _payloadLen) Complete(onPacket);
        }
    }

    void Complete(PacketHandler onPacket)
    {
        _headerHave = 0;
        onPacket(_seq, _keep.AsSpan(0, _kept), _payloadLen);
    }
}

/// <summary>
/// Follows one client connection and turns the conversation into <see cref="QueryRecord"/>s.
/// Commands always start with sequence id 0; everything the server sends until the next command answers it.
/// </summary>
public sealed class MySqlTap
{
    const byte ComQuery = 0x03, ComStmtPrepare = 0x16, ComStmtExecute = 0x17, ComStmtClose = 0x19;

    readonly long _connId;
    readonly int _clientPort;
    readonly ProfileStore _store;
    readonly Func<(int? Pid, string? Name)> _owner;
    readonly PacketReader _fromClient = new(), _fromServer = new();
    readonly Dictionary<uint, string> _prepared = new();
    readonly object _gate = new();

    // what the server is currently answering
    enum Pending { None, Query, Prepare }
    Pending _pending;
    string _pendingSql = "";
    bool _pendingPrepared;
    long _pendingStart;                       // Stopwatch ticks
    DateTimeOffset _pendingWall;
    QueryRecord? _current;

    public MySqlTap(long connectionId, int clientPort, ProfileStore store, Func<(int? Pid, string? Name)> owner)
    {
        _connId = connectionId; _clientPort = clientPort; _store = store; _owner = owner;
    }

    public void FromClient(byte[] buf, int count)
    {
        lock (_gate) _fromClient.Feed(buf.AsSpan(0, count), OnClientPacket);
    }

    public void FromServer(byte[] buf, int count)
    {
        lock (_gate) _fromServer.Feed(buf.AsSpan(0, count), OnServerPacket);
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    void OnClientPacket(byte seq, ReadOnlySpan<byte> p, int fullLen)
    {
        if (seq != 0 || p.Length == 0) return;            // handshake / auth / continuation packets
        _current = null;                                   // a new command ends the previous answer
        _pending = Pending.None;

        switch (p[0])
        {
            case ComQuery:
                Begin(Pending.Query, Text(p, 1, fullLen), prepared: false);
                break;
            case ComStmtPrepare:
                Begin(Pending.Prepare, Text(p, 1, fullLen), prepared: false);
                break;
            case ComStmtExecute when p.Length >= 5:
            {
                var id = BitConverter.ToUInt32(p.Slice(1, 4));
                Begin(Pending.Query, _prepared.TryGetValue(id, out var sql) ? sql : $"(prepared statement #{id})", prepared: true);
                break;
            }
            case ComStmtClose when p.Length >= 5:
                _prepared.Remove(BitConverter.ToUInt32(p.Slice(1, 4)));
                break;
        }
    }

    void Begin(Pending kind, string sql, bool prepared)
    {
        _pending = kind;
        _pendingSql = sql;
        _pendingPrepared = prepared;
        _pendingStart = Stopwatch.GetTimestamp();
        _pendingWall = DateTimeOffset.UtcNow;
    }

    static string Text(ReadOnlySpan<byte> p, int from, int fullLen)
    {
        var s = Encoding.UTF8.GetString(p.Slice(from));
        return fullLen - 1 > p.Length - 1 ? s + " …" : s;     // payload was longer than we kept
    }

    void OnServerPacket(byte seq, ReadOnlySpan<byte> p, int fullLen)
    {
        var now = Stopwatch.GetTimestamp();

        if (_current != null)                              // more of the answer: result rows, EOF...
        {
            _current.TotalMs = Ms(_pendingStart, now);
            _store.Touch();
            return;
        }
        if (_pending == Pending.None || p.Length == 0) return;

        var kind = _pending;
        _pending = Pending.None;
        var first = p[0];
        string? error = null;
        if (first == 0xFF)
        {
            // ERR: 0xFF, code(2), then '#'+sqlstate(5)+message, or just message on old servers
            var code = p.Length >= 3 ? p[1] | (p[2] << 8) : 0;
            var msgAt = p.Length > 9 && p[3] == (byte)'#' ? 9 : 3;
            error = $"#{code} " + (p.Length > msgAt ? Encoding.UTF8.GetString(p.Slice(msgAt)) : "");
        }

        if (kind == Pending.Prepare)
        {
            if (first == 0x00 && p.Length >= 5) _prepared[BitConverter.ToUInt32(p.Slice(1, 4))] = _pendingSql;
            if (error == null) return;                      // a successful prepare isn't a query worth listing
        }

        var (pid, name) = _owner();
        var ms = Ms(_pendingStart, now);
        _current = new QueryRecord
        {
            ConnectionId = _connId,
            ClientPort = _clientPort,
            Pid = pid,
            Process = name,
            StartedAt = _pendingWall,
            Sql = _pendingSql,
            Prepared = _pendingPrepared,
            FirstByteMs = ms,
            TotalMs = ms,
            Error = error,
        };
        _store.Add(_current);
    }
}
