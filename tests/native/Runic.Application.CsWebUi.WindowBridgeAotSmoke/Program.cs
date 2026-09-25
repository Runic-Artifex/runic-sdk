using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;
using Runic.Assets;

if (args is not ["--window-bridge-aot-host"])
{
    Console.Error.WriteLine("Expected --window-bridge-aot-host.");
    return 2;
}

var model = new Note();
var scope = new Scope();
WindowBridgeSession? session = null;
WindowBridgeReference? reference = null;
string? editorRoute = null;
await using var host = new CsWebUiWindowBridgeHost(new CsWebUiWindowBridgeHostOptions
{
    Assets = new EntryAssets(),
    OpenWindow = false,
    Title = "Window Bridge NativeAOT host smoke",
    CreateWindowSession = transport =>
    {
        session = new WindowBridgeSession(transport, scope);
        reference = session.Expose("editor", model, (routes, selectedModel, route) =>
        {
            editorRoute = route;
            return WindowBridgeAttachment.Create(
                routes.Bind(route + ".read", arguments =>
                    HasPresentation(arguments, out _, out _)
                        ? Reply(writer => { writer.WriteStartObject(); writer.WriteBoolean("ok", true); writer.WriteString("title", model.Title); writer.WriteEndObject(); })
                        : Rejected()),
                routes.Bind(route + ".write", arguments =>
                {
                    if (!HasPresentation(arguments, out JsonElement request, out _)
                        || request.EnumerateObject().Count() != 3
                        || !request.TryGetProperty("title", out JsonElement title)
                        || title.ValueKind != JsonValueKind.String) return Rejected();
                    model.Title = title.GetString()!;
                    return Reply(writer => { writer.WriteStartObject(); writer.WriteBoolean("ok", true); writer.WriteString("title", model.Title); writer.WriteEndObject(); });
                }),
                routes.Bind(route + ".save", arguments =>
                {
                    if (!HasPresentation(arguments, out _, out _)) return Rejected();
                    model.Saves++;
                    return "{\"ok\":true}";
                }));
        });
        scope.Add(transport.Bind("aot.discover", arguments =>
            HasCurrentDocument(arguments, out _)
                ? Reply(writer => { writer.WriteStartObject(); writer.WriteBoolean("ok", true); writer.WriteString("route", editorRoute); writer.WriteEndObject(); })
                : Rejected()));
        scope.Add(transport.Bind("aot.mount", arguments =>
        {
            if (!TryPresentationRequest(arguments.GetString(), out WindowBridgeDocumentEpoch? epoch, out string? presentationId)) return Rejected();
            try { _ = session.Mount(reference, arguments.Connection, epoch, presentationId); return "{\"ok\":true}"; }
            catch (InvalidOperationException) { return Rejected(); }
        }));
        scope.Add(transport.Bind("aot.unmount", arguments =>
            TryPresentationRequest(arguments.GetString(), out WindowBridgeDocumentEpoch? epoch, out string? presentationId)
                && session.Unmount(reference, arguments.Connection, epoch, presentationId)
                ? "{\"ok\":true}" : Rejected()));
        scope.Add(transport.Bind("aot.model", arguments =>
            HasCurrentDocument(arguments, out _)
                ? Reply(writer => { writer.WriteStartObject(); writer.WriteBoolean("ok", true); writer.WriteString("title", model.Title); writer.WriteNumber("saves", model.Saves); writer.WriteEndObject(); })
                : Rejected()));
        scope.Add(transport.Bind("aot.suspend", arguments =>
        {
            if (!HasCurrentDocument(arguments, out _)) return Rejected();
            session.Suspend(model);
            return "{\"ok\":true}";
        }));
        return session;

        bool HasCurrentDocument(WindowBridgeArguments arguments, out WindowBridgeDocumentEpoch? epoch)
        {
            epoch = null;
            return TryDocument(arguments.GetString(), out epoch) && session!.IsCurrentDocument(arguments.Connection, epoch);
        }
        bool HasPresentation(WindowBridgeArguments arguments, out JsonElement request, out string? presentationId)
        {
            request = default;
            presentationId = null;
            if (!TryPresentationPayload(arguments.GetString(), out WindowBridgeDocumentEpoch? epoch, out presentationId, out request)) return false;
            return session!.HasPresentation(reference!, arguments.Connection, epoch, presentationId);
        }
    },
});

try
{
    await host.StartAsync();
    Console.WriteLine("WINDOW_BRIDGE_AOT_URL=" + host.Url);
    Console.Out.Flush();
    _ = Console.ReadLine();
    await host.StopAsync();
    if (scope.Disposals != 1) throw new InvalidOperationException("Window scope did not drain.");
    Console.WriteLine("WINDOW_BRIDGE_AOT_STOPPED");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static string Rejected() => "{\"ok\":false,\"kind\":\"rejected\"}";

static string Reply(Action<Utf8JsonWriter> write)
{
    var bytes = new ArrayBufferWriter<byte>();
    using var writer = new Utf8JsonWriter(bytes);
    write(writer);
    writer.Flush();
    return Encoding.UTF8.GetString(bytes.WrittenSpan);
}

static bool TryDocument(string payload, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch)
{
    epoch = null;
    try
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        JsonElement request = json.RootElement;
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Count() != 1
            || !request.TryGetProperty("documentEpoch", out JsonElement value) || value.ValueKind != JsonValueKind.String) return false;
        epoch = WindowBridgeDocumentEpoch.Create(value.GetString()!);
        return true;
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
}

static bool TryPresentationRequest(string payload, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
    [NotNullWhen(true)] out string? presentationId) => TryPresentationPayload(payload, out epoch, out presentationId, out _);

static bool TryPresentationPayload(string payload, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
    [NotNullWhen(true)] out string? presentationId, out JsonElement request)
{
    epoch = null;
    presentationId = null;
    request = default;
    try
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        request = json.RootElement.Clone();
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("documentEpoch", out JsonElement document) || document.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("presentationId", out JsonElement presentation) || presentation.ValueKind != JsonValueKind.String) return false;
        string? value = presentation.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        epoch = WindowBridgeDocumentEpoch.Create(document.GetString()!);
        presentationId = value;
        return true;
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
}

internal sealed class Note { internal string Title { get; set; } = "Draft"; internal int Saves { get; set; } }

internal sealed class Scope : IAsyncDisposable
{
    private readonly List<IDisposable> _leases = [];
    internal int Disposals { get; private set; }
    internal void Add(IDisposable lease) => _leases.Add(lease);
    public ValueTask DisposeAsync()
    {
        Disposals++;
        foreach (IDisposable lease in _leases) lease.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class EntryAssets : IAssetSource
{
    private static readonly byte[] Html = Encoding.UTF8.GetBytes(
        "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>NativeAOT Window Bridge fixture</body></html>");
    public AssetManifest Manifest { get; } = new([
        new AssetDescriptor("index.html", "text/html", Html.Length,
            Convert.ToHexString(SHA256.HashData(Html)).ToLowerInvariant(), isEntryPoint: true),
    ]);
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relativePath != "index.html") throw new FileNotFoundException();
        return ValueTask.FromResult<Stream>(new MemoryStream(Html, writable: false));
    }
}
