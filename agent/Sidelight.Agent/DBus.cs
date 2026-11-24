using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Sidelight.Agent;

/// <summary>A D-Bus object path (type code "o").</summary>
internal readonly record struct DBusPath(string Value)
{
    public override string ToString() => Value;
}

/// <summary>A file descriptor received with a message (type code "h"); whoever takes it closes it.</summary>
internal readonly record struct DBusFd(int Fd);

/// <summary>A value with its D-Bus signature spelled out, for a variant whose CLR type doesn't say it (a byte, an empty array).</summary>
internal sealed record DBusVariant(string Signature, object Value);

/// <summary>An error reply from the other side (org.freedesktop.DBus.Error.*, org.freedesktop.portal.Error.*).</summary>
internal sealed class DBusException(string name, string? message) : Exception($"{name}: {message}")
{
    public string Name { get; } = name;
}

/// <summary>What the Wayland screen code needs from the session bus; the tests replace it with a scripted fake.</summary>
internal interface IDBus : IDisposable
{
    /// <summary>This connection's unique name (":1.42"), which the portal puts in its request paths.</summary>
    string UniqueName { get; }
    bool Connected { get; }
    /// <summary>Raised once, when the bus connection ends (the portal then closes the sessions it holds for us).</summary>
    event Action? Disconnected;
    Task<object[]> CallAsync(string destination, string path, string iface, string member, string signature, object[] args, CancellationToken ct);
    /// <summary>Calls <paramref name="handler"/> with the body of each matching signal until the result is disposed.</summary>
    Task<IDisposable> SubscribeAsync(string path, string iface, string member, Action<object[]> handler, CancellationToken ct);
}

/// <summary>
/// The D-Bus wire format (marshalling only): enough for the xdg-desktop-portal calls of the Second screen widget on
/// Wayland. Values: byte, bool, short, ushort, int, uint, long, ulong, double, string, <see cref="DBusPath"/>,
/// signatures (written from a string where the signature says "g"), variants (any of these, or <see cref="DBusVariant"/>),
/// arrays (any IEnumerable), dictionaries (IDictionary) and structs (object[]).
/// </summary>
internal static class DBusWire
{
    public const byte MethodCall = 1, MethodReturn = 2, Error = 3, Signal = 4;
    public const byte NoReplyExpected = 1;
    private const int MaxMessage = 16 * 1024 * 1024;

    internal sealed record Message(byte Type, uint Serial, uint ReplySerial, string? Path, string? Interface, string? Member,
        string? ErrorName, string? Sender, string Signature, object[] Body, uint FdCount = 0);

    // ------------------------------------------------------------------ writing

    internal sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public int Position => (int)_ms.Length;
        public byte[] ToArray() => _ms.ToArray();

        public void Pad(int alignment) { while (_ms.Length % alignment != 0) _ms.WriteByte(0); }
        public void Byte(byte b) => _ms.WriteByte(b);

        public void UInt32(uint v)
        {
            Pad(4);
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        public void PatchUInt32(int at, uint v)
        {
            var end = _ms.Position;
            _ms.Position = at;
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
            _ms.Position = end;
        }

        private void Raw(int alignment, ReadOnlySpan<byte> bytes) { Pad(alignment); _ms.Write(bytes); }

        public void String(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            UInt32((uint)bytes.Length);
            _ms.Write(bytes);
            _ms.WriteByte(0);
        }

        public void Signature(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            if (bytes.Length > 255) throw new ArgumentException("signature too long");
            _ms.WriteByte((byte)bytes.Length);
            _ms.Write(bytes);
            _ms.WriteByte(0);
        }

        /// <summary>Writes the values of a signature of one or more complete types, one value per type.</summary>
        public void Values(string signature, IReadOnlyList<object> values)
        {
            var types = SplitTypes(signature);
            if (types.Count != values.Count) throw new ArgumentException($"signature {signature} takes {types.Count} values, not {values.Count}");
            for (var i = 0; i < types.Count; i++) Value(types[i], values[i]);
        }

        public void Value(string type, object value)
        {
            Span<byte> b = stackalloc byte[8];
            switch (type[0])
            {
                case 'y': Byte(Convert.ToByte(value, CultureInfo.InvariantCulture)); break;
                case 'b': UInt32(value is true ? 1u : 0u); break;
                case 'n': BinaryPrimitives.WriteInt16LittleEndian(b, Convert.ToInt16(value, CultureInfo.InvariantCulture)); Raw(2, b[..2]); break;
                case 'q': BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToUInt16(value, CultureInfo.InvariantCulture)); Raw(2, b[..2]); break;
                case 'i': BinaryPrimitives.WriteInt32LittleEndian(b, Convert.ToInt32(value, CultureInfo.InvariantCulture)); Raw(4, b[..4]); break;
                case 'u' or 'h': UInt32(Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
                case 'x': BinaryPrimitives.WriteInt64LittleEndian(b, Convert.ToInt64(value, CultureInfo.InvariantCulture)); Raw(8, b); break;
                case 't': BinaryPrimitives.WriteUInt64LittleEndian(b, Convert.ToUInt64(value, CultureInfo.InvariantCulture)); Raw(8, b); break;
                case 'd': BinaryPrimitives.WriteDoubleLittleEndian(b, Convert.ToDouble(value, CultureInfo.InvariantCulture)); Raw(8, b); break;
                case 's' or 'o': String(value.ToString() ?? ""); break;
                case 'g': Signature(value.ToString() ?? ""); break;
                case 'v':
                    var (sig, inner) = value is DBusVariant dv ? (dv.Signature, dv.Value) : (SignatureOf(value), value);
                    Signature(sig);
                    Value(sig, inner);
                    break;
                case '(':
                    Pad(8);
                    Values(type[1..^1], (object[])value);
                    break;
                case 'a':
                    var element = type[1..];
                    UInt32(0);
                    var lengthAt = Position - 4;
                    Pad(Alignment(element[0]));
                    var start = Position;
                    if (element[0] == '{')
                    {
                        var kv = SplitTypes(element[1..^1]);
                        foreach (DictionaryEntry e in (IDictionary)value)
                        {
                            Pad(8);
                            Value(kv[0], e.Key);
                            Value(kv[1], e.Value ?? throw new ArgumentException("null value"));
                        }
                    }
                    else
                        foreach (var item in (IEnumerable)value) Value(element, item);
                    PatchUInt32(lengthAt, (uint)(Position - start));
                    break;
                default: throw new ArgumentException($"unsupported D-Bus type {type}");
            }
        }
    }

    /// <summary>The signature a CLR value is written with inside a variant.</summary>
    internal static string SignatureOf(object value) => value switch
    {
        DBusVariant v => v.Signature,
        byte => "y", bool => "b", short => "n", ushort => "q", int => "i", uint => "u", long => "x", ulong => "t", double => "d",
        string => "s", DBusPath => "o",
        IDictionary<string, object> => "a{sv}",
        object[] fields => "(" + string.Concat(fields.Select(SignatureOf)) + ")",
        _ => throw new ArgumentException($"no D-Bus type for {value.GetType().Name}"),
    };

    internal static int Alignment(char code) => code switch
    {
        'y' or 'g' or 'v' => 1,
        'n' or 'q' => 2,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => 4,
    };

    /// <summary>A signature split into its complete types: "ua{sv}(ii)" → "u", "a{sv}", "(ii)".</summary>
    internal static List<string> SplitTypes(string signature)
    {
        var list = new List<string>();
        var i = 0;
        while (i < signature.Length)
        {
            var start = i;
            i = EndOfType(signature, i);
            list.Add(signature[start..i]);
        }
        return list;
    }

    private static int EndOfType(string sig, int i)
    {
        if (i >= sig.Length) throw new FormatException("incomplete signature");
        switch (sig[i])
        {
            case 'a': return EndOfType(sig, i + 1);
            case '(' or '{':
                var close = sig[i] == '(' ? ')' : '}';
                i++;
                while (i < sig.Length && sig[i] != close) i = EndOfType(sig, i);
                if (i >= sig.Length) throw new FormatException("unclosed container in signature");
                return i + 1;
            default: return i + 1;
        }
    }

    /// <summary>A whole message: the fixed header, the header fields, padding, then the body.</summary>
    internal static byte[] Encode(byte type, byte flags, uint serial, string? destination, string? path, string? iface, string? member,
        uint replySerial, string signature, IReadOnlyList<object> body)
    {
        var bodyWriter = new Writer();
        if (signature.Length > 0) bodyWriter.Values(signature, body);
        var bodyBytes = bodyWriter.ToArray();

        var fields = new List<object>();
        void Field(byte code, string sig, object? value) { if (value is not null) fields.Add(new object[] { code, new DBusVariant(sig, value) }); }
        Field(1, "o", path);
        Field(2, "s", iface);
        Field(3, "s", member);
        Field(5, "u", replySerial == 0 ? null : replySerial);
        Field(6, "s", destination);
        Field(8, "g", signature.Length == 0 ? null : signature);

        var w = new Writer();
        w.Byte((byte)'l');
        w.Byte(type);
        w.Byte(flags);
        w.Byte(1);
        w.UInt32((uint)bodyBytes.Length);
        w.UInt32(serial);
        w.Value("a(yv)", fields);
        w.Pad(8);
        var head = w.ToArray();
        var all = new byte[head.Length + bodyBytes.Length];
        head.CopyTo(all, 0);
        bodyBytes.CopyTo(all, head.Length);
        return all;
    }

    // ------------------------------------------------------------------ reading

    internal sealed class Reader(byte[] data, bool bigEndian)
    {
        public int Pos;

        private void Align(int a) { Pos = (Pos + a - 1) / a * a; if (Pos > data.Length) throw new FormatException("truncated"); }

        private ReadOnlySpan<byte> Take(int alignment, int n)
        {
            Align(alignment);
            if (Pos + n > data.Length) throw new FormatException("truncated");
            var span = data.AsSpan(Pos, n);
            Pos += n;
            return span;
        }

        public uint UInt32() { var s = Take(4, 4); return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s); }

        private string Text(int length)
        {
            var s = Encoding.UTF8.GetString(Take(1, length));
            Take(1, 1); // the terminating nul
            return s;
        }

        public object Value(string type)
        {
            switch (type[0])
            {
                case 'y': return Take(1, 1)[0];
                case 'b': return UInt32() != 0;
                case 'n': { var s = Take(2, 2); return bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s); }
                case 'q': { var s = Take(2, 2); return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s); }
                case 'i': { var s = Take(4, 4); return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s); }
                case 'u' or 'h': return UInt32();
                case 'x': { var s = Take(8, 8); return bigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s); }
                case 't': { var s = Take(8, 8); return bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s); }
                case 'd': { var s = Take(8, 8); return bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s); }
                case 's': return Text(checked((int)UInt32()));
                case 'o': return new DBusPath(Text(checked((int)UInt32())));
                case 'g': return Text(Take(1, 1)[0]);
                case 'v':
                    var sig = Text(Take(1, 1)[0]);
                    if (SplitTypes(sig).Count != 1) throw new FormatException("a variant holds one complete type");
                    return Value(sig);
                case '(':
                    Align(8);
                    return SplitTypes(type[1..^1]).Select(Value).ToArray();
                case 'a':
                    var length = checked((int)UInt32());
                    var element = type[1..];
                    Align(Alignment(element[0]));
                    var end = Pos + length;
                    if (length > MaxMessage || end > data.Length) throw new FormatException("array runs past the message");
                    if (element[0] == '{')
                    {
                        var kv = SplitTypes(element[1..^1]);
                        var dict = new Dictionary<object, object>();
                        while (Pos < end) { Align(8); var k = Value(kv[0]); dict[k] = Value(kv[1]); }
                        return dict;
                    }
                    var list = new List<object>();
                    while (Pos < end) list.Add(Value(element));
                    return list;
                default: throw new FormatException($"unsupported D-Bus type {type}");
            }
        }
    }

    /// <summary>The total length of the message whose first 16 bytes are given, or -1 when it is malformed or too big.</summary>
    internal static int MessageLength(ReadOnlySpan<byte> head)
    {
        if (head.Length < 16 || (head[0] != 'l' && head[0] != 'B')) return -1;
        var big = head[0] == 'B';
        var bodyLength = big ? BinaryPrimitives.ReadUInt32BigEndian(head[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        var fieldsLength = big ? BinaryPrimitives.ReadUInt32BigEndian(head[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        var total = (16L + fieldsLength + 7) / 8 * 8 + bodyLength;
        return total > MaxMessage ? -1 : (int)total;
    }

    internal static Message Decode(byte[] data)
    {
        var big = data[0] == 'B';
        var r = new Reader(data, big) { Pos = 4 };
        r.UInt32(); // the body length (the caller already read exactly one message)
        var serial = r.UInt32();
        var fields = (List<object>)r.Value("a(yv)");
        string? path = null, iface = null, member = null, error = null, sender = null, signature = "";
        uint replySerial = 0, fdCount = 0;
        foreach (object[] f in fields)
        {
            switch ((byte)f[0])
            {
                case 1: path = f[1].ToString(); break;
                case 2: iface = f[1] as string; break;
                case 3: member = f[1] as string; break;
                case 4: error = f[1] as string; break;
                case 5: replySerial = f[1] is uint u ? u : 0; break;
                case 7: sender = f[1] as string; break;
                case 8: signature = f[1] as string ?? ""; break;
                case 9: fdCount = f[1] is uint n ? n : 0; break;
            }
        }
        r.Pos = (r.Pos + 7) / 8 * 8;
        var body = SplitTypes(signature).Select(r.Value).ToArray();
        return new Message(data[1], serial, replySerial, path, iface, member, error, sender, signature, body, fdCount);
    }

    /// <summary>The socket of a "unix:path=…" or "unix:abstract=…" bus address (the first usable one of a ;-separated list).</summary>
    internal static UnixDomainSocketEndPoint? ParseAddress(string? address)
    {
        foreach (var entry in (address ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.StartsWith("unix:", StringComparison.Ordinal)) continue;
            foreach (var pair in entry[5..].Split(','))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (value.Length == 0) continue;
                switch (pair[..eq])
                {
                    case "path": return new UnixDomainSocketEndPoint(value);
                    case "abstract": return new UnixDomainSocketEndPoint("\0" + value);
                }
            }
        }
        return null;
    }
}

/// <summary>
/// A connection to the user's session bus over its Unix socket, with EXTERNAL authentication (the kernel tells the bus
/// our user id). Method calls are matched to their replies by serial; signals go to the handlers that subscribed.
/// Messages are read with recvmsg, so a reply can carry file descriptors (the portal's libei socket); the screen stream
/// itself is read by GStreamer straight from PipeWire.
/// </summary>
internal sealed class DBusConnection : IDBus
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, TaskCompletionSource<DBusWire.Message>> _pending = [];
    private readonly List<(string Path, string Iface, string Member, Action<object[]> Handler)> _handlers = [];
    private readonly CancellationTokenSource _closing = new();
    private int _serial;
    private int _ended;
    private bool _fds;
    private readonly Queue<int> _received = new();

    public string UniqueName { get; private set; } = "";
    public bool Connected => Volatile.Read(ref _ended) == 0;
    public event Action? Disconnected;

    private DBusConnection(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "close")] internal static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollFd fds, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)] private static extern nint RecvMsg(int socket, ref MsgHdr msg, int flags);

    [StructLayout(LayoutKind.Sequential)] internal struct PollFd { public int Fd; public short Events, Revents; }
    [StructLayout(LayoutKind.Sequential)] private struct IoVec { public nint Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr { public nint Name; public uint NameLength; public nint Iov; public nuint IovLength; public nint Control; public nuint ControlLength; public int Flags; }
    internal const short PollIn = 1;
    private const int SolSocket = 1, ScmRights = 1, MsgCmsgCloexec = 0x40000000, EAgain = 11, EIntr = 4;

    public static async Task<DBusConnection> ConnectSessionAsync(CancellationToken ct)
    {
        var endpoint = DBusWire.ParseAddress(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
            ?? (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? new UnixDomainSocketEndPoint(Path.Combine(run, "bus")) : null)
            ?? throw new InvalidOperationException("no session bus");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        DBusConnection? c = null;
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            c = new DBusConnection(socket);
            await c.AuthenticateAsync(ct);
            new Thread(c.ReadLoop) { IsBackground = true, Name = "dbus" }.Start();
            c.UniqueName = (string)(await c.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "Hello", "", [], ct))[0];
            return c;
        }
        catch
        {
            if (c is not null) c.Dispose(); else socket.Dispose();
            throw;
        }
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        var uid = Convert.ToHexString(Encoding.ASCII.GetBytes(GetUid().ToString(CultureInfo.InvariantCulture)));
        await _stream.WriteAsync(Encoding.ASCII.GetBytes($"\0AUTH EXTERNAL {uid}\r\n"), ct);
        var line = await ReadLineAsync(ct);
        if (!line.StartsWith("OK ", StringComparison.Ordinal)) throw new InvalidOperationException("the session bus refused us");
        // the portal's ConnectToEIS answers with a socket; without this the bus drops messages that carry one
        await _stream.WriteAsync("NEGOTIATE_UNIX_FD\r\n"u8.ToArray(), ct);
        _fds = (await ReadLineAsync(ct)).StartsWith("AGREE_UNIX_FD", StringComparison.Ordinal);
        await _stream.WriteAsync("BEGIN\r\n"u8.ToArray(), ct);
    }

    /// <summary>One line of the text handshake, byte by byte so nothing after it is read early.</summary>
    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 512)
        {
            if (await _stream.ReadAsync(one, ct) == 0) throw new IOException("the session bus closed the connection");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        throw new IOException("handshake line too long");
    }

    /// <summary>
    /// Reads with poll + recvmsg on a thread of its own (.NET's socket is non-blocking underneath), collecting the
    /// descriptors that arrive beside the bytes; each message takes as many of them as its header says, in order.
    /// </summary>
    private void ReadLoop()
    {
        const int BufferSize = 64 * 1024, ControlSize = 512;
        var socket = (int)_socket.Handle;
        // native memory, as recvmsg writes through pointers (the project has no unsafe code)
        var buffer = Marshal.AllocHGlobal(BufferSize);
        var control = Marshal.AllocHGlobal(ControlSize);
        var iov = Marshal.AllocHGlobal(Marshal.SizeOf<IoVec>());
        var bytes = new byte[BufferSize];
        var cmsg = new byte[ControlSize];
        var pending = new List<byte>();
        try
        {
            Marshal.StructureToPtr(new IoVec { Base = buffer, Length = BufferSize }, iov, false);
            while (!_closing.IsCancellationRequested)
            {
                var pfd = new PollFd { Fd = socket, Events = PollIn };
                var ready = Poll(ref pfd, 1, 500);
                if (ready < 0 && Marshal.GetLastPInvokeError() != EIntr) break;
                if (ready <= 0) continue;
                var msg = new MsgHdr { Iov = iov, IovLength = 1, Control = control, ControlLength = ControlSize };
                var n = (int)RecvMsg(socket, ref msg, MsgCmsgCloexec);
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() is EAgain or EIntr) continue;
                    break;
                }
                var controlLength = (int)Math.Min((ulong)ControlSize, msg.ControlLength);
                Marshal.Copy(control, cmsg, 0, controlLength);
                TakeDescriptors(cmsg.AsSpan(0, controlLength));
                if (n == 0) break;
                Marshal.Copy(buffer, bytes, 0, n);
                pending.AddRange(bytes.AsSpan(0, n));
                while (pending.Count >= 16)
                {
                    var length = DBusWire.MessageLength(CollectionsMarshal.AsSpan(pending)[..16]);
                    if (length < 16) throw new IOException("malformed message");
                    if (pending.Count < length) break;
                    var data = pending.GetRange(0, length).ToArray();
                    pending.RemoveRange(0, length);
                    Dispatch(DBusWire.Decode(data));
                }
            }
        }
        catch (Exception) { }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(control);
            Marshal.FreeHGlobal(iov);
            End();
        }
    }

    /// <summary>The SCM_RIGHTS descriptors in a control buffer: cmsghdr (length, level, type) then ints, 8-byte aligned.</summary>
    private void TakeDescriptors(ReadOnlySpan<byte> control)
    {
        var at = 0;
        while (at + 16 <= control.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(control[at..]);
            var level = BinaryPrimitives.ReadInt32LittleEndian(control[(at + 8)..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(control[(at + 12)..]);
            if (length < 16 || at + length > control.Length) break;
            if (level == SolSocket && type == ScmRights)
                lock (_gate)
                    for (var i = at + 16; i + 4 <= at + length; i += 4) _received.Enqueue(BinaryPrimitives.ReadInt32LittleEndian(control[i..]));
            at += (length + 7) & ~7;
        }
    }

    private void Dispatch(DBusWire.Message m)
    {
        // the message's own descriptors, in the order they came; "h" values index into them
        var fds = new int[m.FdCount];
        lock (_gate) for (var i = 0; i < fds.Length; i++) fds[i] = _received.TryDequeue(out var fd) ? fd : -1;
        var types = DBusWire.SplitTypes(m.Signature);
        var used = new bool[fds.Length];
        for (var i = 0; i < m.Body.Length && i < types.Count; i++)
            if (types[i] == "h" && m.Body[i] is uint index && index < fds.Length) { m.Body[i] = new DBusFd(fds[index]); used[index] = true; }
        for (var i = 0; i < fds.Length; i++) if (!used[i] && fds[i] >= 0) Close(fds[i]);
        if (m.Type == DBusWire.MethodReturn || m.Type == DBusWire.Error) { DispatchReply(m); return; }
        foreach (var x in m.Body) if (x is DBusFd { Fd: >= 0 } stray) Close(stray.Fd); // nobody takes a descriptor from a signal
        DispatchSignal(m);
    }

    private void DispatchReply(DBusWire.Message m)
    {
        TaskCompletionSource<DBusWire.Message>? tcs;
        lock (_gate) _pending.Remove(m.ReplySerial, out tcs);
        if (tcs is null || !tcs.TrySetResult(m))
            foreach (var x in m.Body) if (x is DBusFd { Fd: >= 0 } lost) Close(lost.Fd); // the caller gave up
    }

    private void DispatchSignal(DBusWire.Message m)
    {
        if (m.Type != DBusWire.Signal) return; // nobody calls methods on us
        List<Action<object[]>> matched;
        lock (_gate) matched = _handlers.Where(h => h.Path == m.Path && h.Iface == m.Interface && h.Member == m.Member).Select(h => h.Handler).ToList();
        foreach (var h in matched)
        {
            try { h(m.Body); } catch (Exception) { }
        }
    }

    public async Task<object[]> CallAsync(string destination, string path, string iface, string member, string signature, object[] args, CancellationToken ct)
    {
        var serial = (uint)Interlocked.Increment(ref _serial);
        var tcs = new TaskCompletionSource<DBusWire.Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!Connected) throw new IOException("the session bus connection is closed");
            _pending[serial] = tcs;
        }
        try
        {
            var bytes = DBusWire.Encode(DBusWire.MethodCall, 0, serial, destination, path, iface, member, 0, signature, args);
            await _send.WaitAsync(ct);
            try { await _stream.WriteAsync(bytes, ct); }
            finally { _send.Release(); }
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var reply = await tcs.Task;
            if (reply.Type == DBusWire.Error) throw new DBusException(reply.ErrorName ?? "error", reply.Body.FirstOrDefault() as string);
            return reply.Body;
        }
        finally { lock (_gate) _pending.Remove(serial); }
    }

    public async Task<IDisposable> SubscribeAsync(string path, string iface, string member, Action<object[]> handler, CancellationToken ct)
    {
        var entry = (path, iface, member, handler);
        lock (_gate) _handlers.Add(entry);
        var rule = $"type='signal',path='{path}',interface='{iface}',member='{member}'";
        try { await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "AddMatch", "s", [rule], ct); }
        catch { lock (_gate) _handlers.Remove(entry); throw; }
        return new Unsubscribe(() =>
        {
            lock (_gate) _handlers.Remove(entry);
            if (Connected)
                _ = CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RemoveMatch", "s", [rule], CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
        });
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) action(); }
    }

    private void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        List<TaskCompletionSource<DBusWire.Message>> pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
            while (_received.TryDequeue(out var fd)) if (fd >= 0) Close(fd);
        }
        foreach (var p in pending) p.TrySetException(new IOException("the session bus connection closed"));
        try { Disconnected?.Invoke(); } catch (Exception) { }
    }

    public void Dispose()
    {
        _closing.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _stream.Dispose();
        _socket.Dispose();
        End();
    }
}
