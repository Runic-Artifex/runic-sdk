using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using TUnit.Core;
using TUnit.Core.Enums;
using static ValueTests;

// A peer that nests values deeper than D-Bus allows, or repeats a dictionary key. A bus daemon rejects too deep messages,
// so a fake peer-to-peer bus on a Unix socket sends them straight to the connection.
[RunOn(OS.Linux)]
internal sealed class NestingTests
{
    private const string Interface = "org.runic.Test.Echo";
    private const string Path = "/org/runic/Test/Echo";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test, DisplayName("a call with 100,000 nested variants is refused with InvalidArgs and the connection keeps answering")]
    public async Task DeepVariantsAsync()
    {
        await using var bus = await FakeBus.ConnectAsync();
        var refused = await bus.CallAsync(Path, Interface, "Echo", "v", Variants(100_000));
        Check(refused.Type == FakeBus.Error && refused.Fields[4] is DBusException.InvalidArgs, $"refused: {refused.Describe()}");
        var answered = await bus.CallAsync(Path, Interface, "Echo", "v", Variants(10));
        Check(answered.Type == FakeBus.Return && answered.Body.AsSpan().SequenceEqual(Variants(10)), $"answered: {answered.Describe()}");
    }

    [Test, DisplayName("arrays, dictionary entries and variants nest 64 deep together, and no deeper")]
    public async Task NestingLimitAsync()
    {
        await using var bus = await FakeBus.ConnectAsync();
        foreach (var (signature, body, accepted) in new[]
        {
            ("v", Variants(64), true), ("v", Variants(65), false),
            // a{sv} nests the array and its entries, two levels.
            ("a{sv}", Dictionary(62), true), ("a{sv}", Dictionary(63), false),
        })
        {
            var reply = await bus.CallAsync(Path, Interface, "Echo", signature, body);
            Check(accepted ? reply.Type == FakeBus.Return && reply.Body.AsSpan().SequenceEqual(body)
                : reply.Type == FakeBus.Error && reply.Fields[4] is DBusException.InvalidArgs, $"{signature} of {body.Length} bytes: {reply.Describe()}");
        }
    }

    [Test, DisplayName("a call whose a{sv} repeats a key reads with the last value, through DBusValue and a generated service")]
    public async Task RepeatedKeyAsync()
    {
        byte[] tags = StringVariants(("genre", "jazz"), ("year", "1959"), ("genre", "bebop"));
        await using (var bus = await FakeBus.ConnectAsync(new GenreObject()))
        {
            var reply = await bus.CallAsync(Path, Interface, "Genre", "a{sv}", tags);
            Check(reply.Type == FakeBus.Return && ReadString(reply.Body) == "bebop", $"GetVariantDictionary: {reply.Describe()}");
        }
        await using (var bus = await FakeBus.ConnectAsync(new GeneratedTests.TestPlayer(), "/org/runic/Test/Media"))
        {
            // Describe(a{sv} tags, a(oss) tracks) answers with tags["genre"]; the tracks are an empty array padded to 8 bytes.
            byte[] body = [.. tags, .. new byte[(4 - tags.Length % 4) % 4], 0, 0, 0, 0];
            body = [.. body, .. new byte[(8 - body.Length % 8) % 8]];
            var reply = await bus.CallAsync("/org/runic/Test/Media", "org.runic.Test.Media.Player", "Describe", "a{sv}a(oss)", body);
            Check(reply.Type == FakeBus.Return && ReadString(reply.Body) == "bebop", $"generated reader: {reply.Describe()}");
        }
    }

    // An a{sv} whose values are strings, with the entries in the given order, repeated keys included.
    private static byte[] StringVariants(params (string Key, string Value)[] entries)
    {
        var body = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 0 };
        void Pad(int alignment) { while (body.Count % alignment != 0) body.Add(0); }
        void String(string text) { Pad(4); body.AddRange(BitConverter.GetBytes(Encoding.UTF8.GetByteCount(text))); body.AddRange(Encoding.UTF8.GetBytes(text)); body.Add(0); }
        foreach (var (key, value) in entries)
        {
            Pad(8);
            String(key);
            body.AddRange("\u0001s\0"u8.ToArray());
            String(value);
        }
        // The length counts from the first entry, after the padding to its 8-byte alignment.
        var result = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)(result.Length - 8));
        return result;
    }

    private static string ReadString(byte[] body) => body.Length >= 4 ? Encoding.UTF8.GetString(body, 4, (int)BinaryPrimitives.ReadUInt32LittleEndian(body)) : "";

    // `levels` variants, each holding the next, the last a byte.
    private static byte[] Variants(int levels)
    {
        var body = new byte[3 * levels + 1];
        for (int level = 0; level < levels; level++) (level == levels - 1 ? "\u0001y\0"u8 : "\u0001v\0"u8).CopyTo(body.AsSpan(3 * level));
        body[^1] = 42;
        return body;
    }

    // a{sv} with one entry whose value is `levels` nested variants.
    private static byte[] Dictionary(int levels)
    {
        var variants = Variants(levels);
        // Array length, padding to the entry's 8-byte alignment, the key "k", then the variants.
        var body = new byte[8 + 6 + variants.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)(body.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 1);
        body[12] = (byte)'k';
        variants.CopyTo(body.AsSpan(14));
        return body;
    }

    private sealed class EchoObject : DBusObject
    {
        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context) => ValueTask.FromResult(context.Arguments);
    }

    // Answers with the "genre" of an a{sv}.
    private sealed class GenreObject : DBusObject
    {
        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context) =>
            ValueTask.FromResult<ImmutableArray<DBusValue>>([context.Arguments[0].GetVariantDictionary()["genre"]]);
    }

    // A minimal little-endian D-Bus peer: SASL EXTERNAL, Hello and method calls to an exported object.
    private sealed class FakeBus : IAsyncDisposable
    {
        internal const byte Call = 1, Return = 2, Error = 3;
        private readonly DirectoryInfo _directory;
        private readonly Socket _listener;
        private Socket? _socket;
        private NetworkStream? _stream;
        private DBusConnection? _connection;
        private IAsyncDisposable? _export;
        private uint _serial;

        private FakeBus(DirectoryInfo directory, Socket listener) { _directory = directory; _listener = listener; }

        internal static async Task<FakeBus> ConnectAsync(DBusObject? exported = null, string path = Path)
        {
            var directory = Directory.CreateTempSubdirectory("runic-dbus-");
            string socketPath = System.IO.Path.Combine(directory.FullName, "bus");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var bus = new FakeBus(directory, listener);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(1);
                using var timeout = new CancellationTokenSource(Timeout);
                var accept = bus.AcceptAsync(timeout.Token);
                var connect = DBusConnection.ConnectToAddressAsync($"unix:path={socketPath}", timeout.Token).AsTask();
                await Task.WhenAll(accept, connect).WaitAsync(Timeout);
                bus._connection = connect.Result is PlatformResult<DBusConnection>.Success(var connection) ? connection
                    : throw new InvalidOperationException($"The connection failed: {connect.Result}");
                Check(connection.UniqueName == ":1.1", $"the connection took the name from Hello: \"{connection.UniqueName}\"");
                bus._export = connection.Export(path, exported ?? new EchoObject());
                return bus;
            }
            catch
            {
                await bus.DisposeAsync();
                throw;
            }
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            _socket = await _listener.AcceptAsync(cancellationToken);
            _stream = new NetworkStream(_socket);
            var nul = new byte[1];
            await _stream.ReadExactlyAsync(nul, cancellationToken);
            Check(nul[0] == 0, "the client starts with a NUL byte");
            while (true)
            {
                string line = await ReadLineAsync(cancellationToken);
                if (line.StartsWith("AUTH EXTERNAL", StringComparison.Ordinal)) await WriteAsync("OK 0123456789abcdef0123456789abcdef\r\n"u8.ToArray(), cancellationToken);
                else if (line == "NEGOTIATE_UNIX_FD") await WriteAsync("AGREE_UNIX_FD\r\n"u8.ToArray(), cancellationToken);
                else if (line == "BEGIN") break;
                else await WriteAsync("ERROR\r\n"u8.ToArray(), cancellationToken);
            }
            var hello = await ReadMessageAsync(cancellationToken);
            Check(hello.Type == Call && hello.Fields[3] is "Hello", $"the client says Hello: {hello.Describe()}");
            await WriteAsync(Encode(Return, ++_serial, [(5, "u", hello.Serial), (6, "s", ":1.1"), (7, "s", "org.freedesktop.DBus"), (8, "g", "s")],
                [4, 0, 0, 0, .. ":1.1"u8, 0]), cancellationToken);
        }

        // Sends a call to the exported object and returns its reply, answering the client's calls to the bus meanwhile.
        internal async Task<Message> CallAsync(string path, string interfaceName, string member, string signature, byte[] body)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            uint serial = ++_serial;
            await WriteAsync(Encode(Call, serial, [(1, "o", path), (2, "s", interfaceName), (3, "s", member), (6, "s", ":1.1"), (7, "s", ":1.0"), (8, "g", signature)],
                body), timeout.Token);
            while (true)
            {
                var message = await ReadMessageAsync(timeout.Token);
                if (message.Fields.GetValueOrDefault((byte)5) is uint replySerial && replySerial == serial) return message;
                if (message.Type == Call && (message.Flags & 1) == 0)
                    await WriteAsync(Encode(Return, ++_serial, [(5, "u", message.Serial), (6, "s", ":1.1"), (7, "s", "org.freedesktop.DBus")], []), timeout.Token);
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            var next = new byte[1];
            while (!(line.Count >= 2 && line[^2] == '\r' && line[^1] == '\n'))
            {
                await _stream!.ReadExactlyAsync(next, cancellationToken);
                line.Add(next[0]);
            }
            return Encoding.ASCII.GetString(line.ToArray(), 0, line.Count - 2);
        }

        private Task WriteAsync(byte[] data, CancellationToken cancellationToken) => _stream!.WriteAsync(data, cancellationToken).AsTask();

        private async Task<Message> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var start = new byte[16];
            await _stream!.ReadExactlyAsync(start, cancellationToken);
            Check(start[0] == 'l' && start[3] == 1, "a little-endian version 1 message");
            int fieldsLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(12));
            var message = new byte[Align(16 + fieldsLength, 8) + BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(4))];
            start.CopyTo(message, 0);
            await _stream.ReadExactlyAsync(message.AsMemory(16), cancellationToken);
            var fields = new Dictionary<byte, object>();
            for (int position = 16; position < 16 + fieldsLength;)
            {
                position = Align(position, 8);
                byte code = message[position];
                char type = (char)message[position + 2];
                position += 3 + message[position + 1];
                switch (type)
                {
                    case 'u': position = Align(position, 4); fields[code] = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(position)); position += 4; break;
                    case 's' or 'o':
                        position = Align(position, 4);
                        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(position));
                        fields[code] = Encoding.UTF8.GetString(message, position + 4, length);
                        position += 5 + length;
                        break;
                    case 'g': fields[code] = Encoding.ASCII.GetString(message, position + 1, message[position]); position += 2 + message[position]; break;
                    default: throw new InvalidOperationException($"Unexpected header field type {type}.");
                }
            }
            return new Message(message[1], message[2], BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)), fields,
                message[Align(16 + fieldsLength, 8)..]);
        }

        private static byte[] Encode(byte type, uint serial, (byte Code, string Type, object Value)[] fields, byte[] body)
        {
            var message = new List<byte> { (byte)'l', type, 0, 1 };
            void Pad(int alignment) { while (message.Count % alignment != 0) message.Add(0); }
            void UInt32(uint value) { Pad(4); message.AddRange(BitConverter.GetBytes(value)); }
            message.AddRange(BitConverter.GetBytes((uint)body.Length));
            message.AddRange(BitConverter.GetBytes(serial));
            message.AddRange(new byte[4]);
            foreach (var (code, fieldType, value) in fields)
            {
                Pad(8);
                message.AddRange([code, 1, (byte)fieldType[0], 0]);
                switch (value)
                {
                    case uint number: UInt32(number); break;
                    case string text when fieldType == "g": message.Add((byte)text.Length); message.AddRange(Encoding.ASCII.GetBytes(text)); message.Add(0); break;
                    case string text: UInt32((uint)Encoding.UTF8.GetByteCount(text)); message.AddRange(Encoding.UTF8.GetBytes(text)); message.Add(0); break;
                }
            }
            byte[] length = BitConverter.GetBytes((uint)(message.Count - 16));
            for (int index = 0; index < 4; index++) message[12 + index] = length[index];
            Pad(8);
            message.AddRange(body);
            return [.. message];
        }

        private static int Align(int position, int alignment) => (position + alignment - 1) / alignment * alignment;

        public async ValueTask DisposeAsync()
        {
            if (_export is not null) await _export.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            _stream?.Dispose();
            _socket?.Dispose();
            _listener.Dispose();
            _directory.Delete(recursive: true);
        }
    }

    // Header fields by code: 3 MEMBER, 4 ERROR_NAME, 5 REPLY_SERIAL, 8 SIGNATURE.
    private sealed record Message(byte Type, byte Flags, uint Serial, Dictionary<byte, object> Fields, byte[] Body)
    {
        public string Describe() => $"type {Type}, {string.Join(", ", Fields.Select(static field => $"{field.Key}={field.Value}"))}, {Body.Length} body bytes"
            + (Type == FakeBus.Error && Body.Length > 4 ? $": {Encoding.UTF8.GetString(Body, 4, (int)BinaryPrimitives.ReadUInt32LittleEndian(Body))}" : "");
    }
}
