using System.Text;
using System.Text.Json.Nodes;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests.Conformance;

// The host inputs of the producer scenarios, shared by the in-repository runner (ProducerScenarioRunner) and the
// conformance adapter (eng/conformance/PROTOCOL.md): the setAppearance step and the upload step.
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
    // the body's length by default. The answer is the host's status and JSON body.
    internal static async Task<JsonObject> UploadAsync(IBridgeTransport transport, ScenarioConnection connection, JsonObject upload)
    {
        var dispatcher = BridgeDispatcher.For(transport);
        var key = dispatcher.ConnectionKeyOf(connection.Key)
            ?? throw new InvalidOperationException($"The connection {connection.Name} has not completed its hello.");
        var token = dispatcher.Blobs.Grant(key)?.Token
            ?? throw new InvalidOperationException("The window takes no uploads.");
        var body = Encoding.UTF8.GetBytes(upload["text"]?.GetValue<string>() ?? "");
        var exchange = new UploadExchange(body, new(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeBlobs.UploadTokenHeader] = token,
            [BridgeBlobs.UploadNameHeader] = Uri.EscapeDataString(upload["name"]?.GetValue<string>() ?? ""),
            ["Content-Type"] = upload["type"]?.GetValue<string>() ?? "",
            ["Content-Length"] = (upload["size"]?.GetValue<long>() ?? body.Length).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await dispatcher.Blobs.ReceiveUploadAsync(exchange).ConfigureAwait(false);
        return new JsonObject
        {
            ["status"] = exchange.StatusCode,
            ["body"] = JsonNode.Parse(Encoding.UTF8.GetString(exchange.Response.ToArray())),
        };
    }

    private sealed class UploadExchange(byte[] body, Dictionary<string, string> headers) : IBridgeBlobExchange
    {
        internal MemoryStream Response { get; } = new();

        public string Method => "POST";
        public string GetRequestHeader(string name) => headers.GetValueOrDefault(name, "");
        public Stream RequestBody { get; } = new MemoryStream(body, writable: false);
        public int StatusCode { get; set; } = 200;
        public string? ContentType { set { } }
        public long? ContentLength { set { } }
        public void SetResponseHeader(string name, string value) { }
        public void AppendResponseHeader(string name, string value) { }
        public Stream ResponseBody => Response;
        public CancellationToken RequestAborted => CancellationToken.None;
    }
}
