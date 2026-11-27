using System.Text;
using System.Text.Json.Nodes;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests.Conformance;

// The host inputs of the producer scenarios, shared by the in-repository runner (ProducerScenarioRunner) and the
// conformance adapter (eng/conformance/PROTOCOL.md): the setAppearance step and the upload and endUpload steps.
internal static class ConformanceHost
{
    // setAppearance: the members of an appearance push as the DesktopAppearance a host sets, and its override as the
    // members the host decided for the window.
    internal static void SetAppearance(IBridgeTransport transport, JsonObject? members) =>
        BridgeDispatcher.For(transport).SetAppearance(ReadAppearance(members), Decided(members?["override"] as JsonArray));

    private static Runic.Platform.DesktopAppearance? ReadAppearance(JsonObject? members)
    {
        if (members is null) return null;
        static Runic.Platform.DesktopAccentColor? Color(JsonNode? node) => node?.GetValue<string>() is { } text
            ? new(Convert.ToInt32(text[1..3], 16) / 255d, Convert.ToInt32(text[3..5], 16) / 255d, Convert.ToInt32(text[5..7], 16) / 255d)
            : null;
        var colors = members["systemColors"] as JsonObject;
        return new Runic.Platform.DesktopAppearance(
            members["colorScheme"]?.GetValue<string>() switch
            {
                "light" => Runic.Platform.DesktopColorScheme.Light,
                "dark" => Runic.Platform.DesktopColorScheme.Dark,
                _ => Runic.Platform.DesktopColorScheme.NoPreference,
            },
            Color(members["accentColor"]), ReducedMotion: members["reducedMotion"]?.GetValue<bool>())
        {
            Contrast = members["contrast"]?.GetValue<string>() switch
            {
                "no-preference" => Runic.Platform.DesktopContrast.NoPreference,
                "more" => Runic.Platform.DesktopContrast.More,
                "forced-colors" => Runic.Platform.DesktopContrast.ForcedColors,
                _ => null,
            },
            TextScale = members["textScale"]?.GetValue<double>(),
            SystemColors = colors is null ? null : new(Color(colors["canvas"])!, Color(colors["canvasText"])!, Color(colors["linkText"])!,
                Color(colors["grayText"])!, Color(colors["highlight"])!, Color(colors["highlightText"])!, Color(colors["buttonFace"])!,
                Color(colors["buttonText"])!),
        };
    }

    private static HostDecidedAppearance Decided(JsonArray? names)
    {
        var decided = HostDecidedAppearance.None;
        foreach (var name in names ?? [])
            decided |= name?.GetValue<string>() switch
            {
                "colorScheme" => HostDecidedAppearance.ColorScheme,
                "contrast" => HostDecidedAppearance.Contrast,
                "reducedMotion" => HostDecidedAppearance.ReducedMotion,
                _ => throw new NotSupportedException($"The appearance override names {name?.ToJsonString()}."),
            };
        return decided;
    }

    // upload: the page's POST of a file to the window's upload URL with the connection's token (README, "Uploads"),
    // through the host's upload path. The body is the UTF-8 of text; size is the Content-Length the page declares,
    // the body's length by default. The answer is the host's status and JSON body. With hold, the body's end is held
    // until EndUploadAsync: the step returns {} once the host reads the body (and so counts the upload as being
    // received), or once it answered without reading.
    internal static async Task<JsonObject> UploadAsync(IBridgeTransport transport, ScenarioConnection connection, JsonObject upload,
        HeldUploads held)
    {
        var dispatcher = BridgeDispatcher.For(transport);
        var key = dispatcher.ConnectionKeyOf(connection.Key)
            ?? throw new InvalidOperationException($"The connection {connection.Name} has not completed its hello.");
        var token = dispatcher.Blobs.Grant(key)?.Token
            ?? throw new InvalidOperationException("The window takes no uploads.");
        var body = Encoding.UTF8.GetBytes(upload["text"]?.GetValue<string>() ?? "");
        var hold = upload["hold"]?.GetValue<string>();
        var stream = new UploadBody(body, hold is not null);
        var exchange = new UploadExchange(stream, new(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeBlobs.UploadTokenHeader] = token,
            [BridgeBlobs.UploadNameHeader] = Uri.EscapeDataString(upload["name"]?.GetValue<string>() ?? ""),
            ["Content-Type"] = upload["type"]?.GetValue<string>() ?? "",
            ["Content-Length"] = (upload["size"]?.GetValue<long>() ?? body.Length).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var answer = AnswerAsync(dispatcher, exchange);
        if (hold is null) return await answer.ConfigureAwait(false);
        held.Add(hold, stream, answer);
        await Task.WhenAny(stream.Reading, answer).ConfigureAwait(false);
        return new JsonObject();
    }

    // endUpload: ends the held body and answers with the host's answer.
    internal static Task<JsonObject> EndUploadAsync(HeldUploads held, string hold) => held.End(hold);

    private static async Task<JsonObject> AnswerAsync(BridgeDispatcher dispatcher, UploadExchange exchange)
    {
        await dispatcher.Blobs.ReceiveUploadAsync(exchange).ConfigureAwait(false);
        return new JsonObject
        {
            ["status"] = exchange.StatusCode,
            ["body"] = JsonNode.Parse(Encoding.UTF8.GetString(exchange.Response.ToArray())),
        };
    }

    // The uploads of a scenario whose body's end is held, by name. Disposing ends every body still held.
    internal sealed class HeldUploads : IDisposable
    {
        private readonly Dictionary<string, (UploadBody Body, Task<JsonObject> Answer, bool Ended)> _held = new(StringComparer.Ordinal);

        internal void Add(string hold, UploadBody body, Task<JsonObject> answer)
        {
            if (!_held.TryAdd(hold, (body, answer, false))) throw new InvalidOperationException($"The upload {hold} was held before.");
        }

        internal async Task<JsonObject> End(string hold)
        {
            if (!_held.TryGetValue(hold, out var upload) || upload.Ended) throw new InvalidOperationException($"The upload {hold} is not held.");
            _held[hold] = upload with { Ended = true };
            upload.Body.End();
            return await upload.Answer.ConfigureAwait(false);
        }

        public void Dispose()
        {
            foreach (var upload in _held.Values) upload.Body.End();
        }
    }

    // A request body of the bytes given. A held body sends them, then waits for its end before it ends.
    internal sealed class UploadBody(byte[] bytes, bool held) : Stream
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        internal Task Reading => _reading.Task;

        internal void End() => _ended.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            if (held) await _ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UploadExchange(Stream body, Dictionary<string, string> headers) : IBridgeBlobExchange
    {
        internal MemoryStream Response { get; } = new();

        public string Method => "POST";
        public string GetRequestHeader(string name) => headers.GetValueOrDefault(name, "");
        public Stream RequestBody => body;
        public int StatusCode { get; set; } = 200;
        public string? ContentType { set { } }
        public long? ContentLength { set { } }
        public void SetResponseHeader(string name, string value) { }
        public void AppendResponseHeader(string name, string value) { }
        public Stream ResponseBody => Response;
        public CancellationToken RequestAborted => CancellationToken.None;
    }
}
