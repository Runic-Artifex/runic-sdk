using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Application.Bridge;
using Runic.Application.Bridge.PostMvvmFixture;
using Runic.Application.Bridge.PostMvvmFixture.Generated;
using Runic.Application.CsWebUi;
using Runic.Assets;

string readyManifest = Required("RUNIC_VIEW_BRIDGE_READY_MANIFEST");
string hostReady = Required("RUNIC_VIEW_BRIDGE_HOST_READY");
string startupPath = Required("RUNIC_PROBE_STARTUP_PATH");
string? hostReadyGate = Environment.GetEnvironmentVariable("RUNIC_PROBE_HOST_READY_GATE_PATH");
string? hostReadyGateReached = Environment.GetEnvironmentVariable("RUNIC_PROBE_HOST_READY_GATE_REACHED_PATH");
Uri viteOrigin = ReadPrivateViteOrigin(Required("RUNIC_APPLICATION_VITE_DEV_SERVER"));
string fingerprint = ReadFingerprint(readyManifest);
if (!string.Equals(fingerprint, PostMvvmDiscoveryAdapter.Fingerprint, StringComparison.Ordinal))
    throw new InvalidOperationException("The ready manifest fingerprint does not match the compiled bridge adapter.");

var model = new NotesViewModel();
var scope = new OwnedScope();
WindowBridgeSession? session = null;
WindowBridgeReference? reference = null;
CsWebUiWindowBridgeInvalidationPublisher? invalidations = null;
string? generatedRoute = null;
var mountGate = new object();
await using var host = new CsWebUiWindowBridgeHost(new CsWebUiWindowBridgeHostOptions
{
    Assets = new EntryAssets(viteOrigin),
    OpenWindow = false,
    Title = "Generated dev native probe",
    ConfigureInvalidations = (_, publisher) => invalidations = publisher,
    CreateWindowSession = transport =>
    {
        session = new WindowBridgeSession(transport, scope);
        reference = session.Expose("notes-editor", model, (routes, selected, route) =>
        {
            generatedRoute = route;
            return PostMvvmDiscoveryAdapter.AttachFixtureRoutes(routes, selected, route, session, () => reference!,
                (connection, epoch, presentationId) => session.HasPresentation(reference!, connection, epoch, presentationId),
                () => invalidations?.TryPublish(model, "notes-editor"));
        });
        scope.Add(transport.Bind("postmvvm.fixture", arguments =>
        {
            if (!TryDocument(arguments.GetString(), out WindowBridgeDocumentEpoch? epoch)
                || !session.IsCurrentDocument(arguments.Connection, epoch)) return Rejected();
            return JsonSerializer.Serialize(new FixtureResponse(true, new FixturePage(
                "notes-editor", reference!.Id, PostMvvmDiscoveryAdapter.Fingerprint)),
                ProbeJsonContext.Default.FixtureResponse);
        }));
        scope.Add(transport.Bind("postmvvm.mount", arguments =>
        {
            if (!TryPresentation(arguments.GetString(), true, reference!.Id,
                out WindowBridgeDocumentEpoch? epoch, out string? presentationId)) return Rejected();
            try
            {
                lock (mountGate)
                {
                    if (generatedRoute is null || session.HasPresentation(reference!, arguments.Connection, epoch, presentationId!))
                        return Rejected();
                    _ = session.Mount(reference!, arguments.Connection, epoch, presentationId!);
                    return JsonSerializer.Serialize(new MountResponse(true, presentationId!, generatedRoute,
                        PostMvvmDiscoveryAdapter.Fingerprint), ProbeJsonContext.Default.MountResponse);
                }
            }
            catch (InvalidOperationException) { return Rejected(); }
        }));
        scope.Add(transport.Bind("postmvvm.unmount", arguments =>
        {
            if (!TryPresentation(arguments.GetString(), false, reference!.Id,
                out WindowBridgeDocumentEpoch? epoch, out string? presentationId)
                || !session.Unmount(reference!, arguments.Connection, epoch, presentationId!)) return Rejected();
            return "{\"ok\":true}";
        }));
        return session;
    },
});

await host.StartAsync();
string nativeUrl = host.Url?.AbsoluteUri ?? throw new InvalidOperationException("Native host URL is missing.");
await WaitForHostReadyGateAsync(hostReadyGate, hostReadyGateReached, fingerprint);
WriteAtomically(hostReady, fingerprint);
WriteAtomically(startupPath, JsonSerializer.Serialize(new StartupDescriptor(Environment.ProcessId,
    Guid.NewGuid().ToString("N"), fingerprint, nativeUrl, readyManifest), ProbeJsonContext.Default.StartupDescriptor));
Console.WriteLine($"GENERATED_DEV_NATIVE_HOST_READY {fingerprint}");
await Task.Delay(Timeout.InfiniteTimeSpan);

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} is required.");

static string ReadFingerprint(string path)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
    string? value = document.RootElement.GetProperty("fingerprint").GetString();
    return !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException("The generated ready manifest had no fingerprint.");
}

static Uri ReadPrivateViteOrigin(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin)
        || origin.Scheme != Uri.UriSchemeHttp || origin.Host != "127.0.0.1"
        || origin.Port is < 1 or > 65535 || origin.Query.Length != 0
        || origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
        throw new InvalidOperationException("The Vite origin must be one private 127.0.0.1 HTTP origin.");
    return origin;
}

static void WriteAtomically(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A probe path needs a parent."));
    string temporary = path + "." + Environment.ProcessId + ".tmp";
    File.WriteAllText(temporary, content + Environment.NewLine);
    File.Move(temporary, path, overwrite: true);
}

static async Task WaitForHostReadyGateAsync(string? path, string? reachedPath, string fingerprint)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
    if (!string.IsNullOrWhiteSpace(reachedPath)) WriteAtomically(reachedPath, fingerprint);
    Console.WriteLine("GENERATED_DEV_NATIVE_HOST_READY_GATED");
    for (int attempt = 0; File.Exists(path); attempt++)
    {
        if (attempt >= 600) throw new TimeoutException("The fixture host-ready gate was not released.");
        await Task.Delay(25).ConfigureAwait(false);
    }
}

static string Rejected() => "{\"ok\":false,\"kind\":\"rejected\"}";

static bool TryDocument(string payload, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch)
{
    epoch = null;
    try
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement request = document.RootElement;
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Count() != 1
            || !request.TryGetProperty("documentEpoch", out JsonElement value) || value.ValueKind != JsonValueKind.String) return false;
        epoch = WindowBridgeDocumentEpoch.Create(value.GetString()!);
        return true;
    }
    catch (JsonException) { return false; }
    catch (ArgumentException) { return false; }
}

static bool TryPresentation(string payload, bool mount, string expectedReference,
    [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
    [NotNullWhen(true)] out string? presentationId)
{
    epoch = null;
    presentationId = null;
    try
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement request = document.RootElement;
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Count() != (mount ? 4 : 3)
            || !request.TryGetProperty("documentEpoch", out JsonElement documentEpoch) || documentEpoch.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("presentationId", out JsonElement presentation) || presentation.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("reference", out JsonElement reference) || reference.ValueKind != JsonValueKind.String
            || reference.GetString() != expectedReference
            || (mount && (!request.TryGetProperty("fingerprint", out JsonElement fingerprint)
                || fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() != PostMvvmDiscoveryAdapter.Fingerprint))) return false;
        string? value = presentation.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        epoch = WindowBridgeDocumentEpoch.Create(documentEpoch.GetString()!);
        presentationId = value;
        return true;
    }
    catch (JsonException) { return false; }
    catch (ArgumentException) { return false; }
}

sealed class OwnedScope : IAsyncDisposable
{
    private readonly List<IDisposable> _leases = [];
    internal void Add(IDisposable lease) => _leases.Add(lease);
    public ValueTask DisposeAsync()
    {
        foreach (IDisposable lease in _leases) lease.Dispose();
        return ValueTask.CompletedTask;
    }
}

sealed class EntryAssets : IAssetSource
{
    private readonly byte[] _html;
    internal EntryAssets(Uri viteOrigin)
    {
        string origin = viteOrigin.AbsoluteUri;
        string html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><span id=\"value\">loading</span>"
            + "<script type=\"module\" src=\"" + origin + "@vite/client\"></script>"
            + "<script type=\"module\" src=\"" + origin + "src/main.js\"></script></body></html>";
        _html = Encoding.UTF8.GetBytes(html);
        Manifest = new([new AssetDescriptor("index.html", "text/html", _html.Length,
            Convert.ToHexString(SHA256.HashData(_html)).ToLowerInvariant(), isEntryPoint: true)]);
    }
    public AssetManifest Manifest { get; }
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relativePath != "index.html") throw new FileNotFoundException();
        return ValueTask.FromResult<Stream>(new MemoryStream(_html, writable: false));
    }
}

internal sealed record FixturePage(string Kind, string Reference, string Fingerprint);
internal sealed record FixtureResponse(bool Ok, FixturePage Page);
internal sealed record MountResponse(bool Ok, string PresentationId, string ReferencePrefix, string Fingerprint);
internal sealed record StartupDescriptor(int ProcessId, string InstanceId, string Fingerprint, string NativeUrl, string ReadyManifest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FixtureResponse))]
[JsonSerializable(typeof(MountResponse))]
[JsonSerializable(typeof(StartupDescriptor))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext { }
