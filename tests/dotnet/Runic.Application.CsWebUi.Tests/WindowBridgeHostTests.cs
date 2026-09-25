using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;
using Runic.Assets;

internal static class WindowBridgeHostTests
{
    private const string DocumentEpoch = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal static async Task RunAsync()
    {
        await StableWindowBridgeDispatchTests.RunAsync();
        AssetResponderPreservesManifestAndBootstrapRules();
        ManualNotesDescriptorUsesOneCheckedOwner();
        ManualNotesDescriptorPreservesCommittedSetterFailure();
        await HostOwnsManualWindowSessionAndScopeAsync();
        Console.WriteLine("CS-WebUI Window Bridge host: asset/bootstrap, fixed Notes routes, readiness, and scope shutdown passed.");
    }

    private static void AssetResponderPreservesManifestAndBootstrapRules()
    {
        var assets = new TestAssets("""
            <!doctype html><html><head><script type="module" src="/app.js"></script></head><body></body></html>
            """);
        var responder = new CsWebUiWindowBridgeAssetResponder(assets, new string('A', 64), "A <title>", 1024, 4096);
        responder.DispatchEndpointBootstrap = () => """{"revision":7,"endpoints":{"dialog":{"endpoint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","generation":3}}}""";
        string entry = Encoding.UTF8.GetString(responder.Serve("/").Response.Span);
        Check(entry.Contains("Content-Security-Policy: frame-ancestors 'none'", StringComparison.Ordinal), "Window Bridge responder lost security headers.");
        int bootstrap = entry.IndexOf("runicCsWebUi", StringComparison.Ordinal);
        int module = entry.IndexOf("type=\"module\"", StringComparison.Ordinal);
        Check(bootstrap >= 0 && module > bootstrap, "Window Bridge bootstrap did not precede application modules.");
        Match firstTicket = Regex.Match(entry, "documentEpoch:'([0-9A-F]{32})'");
        string secondEntry = Encoding.UTF8.GetString(responder.Serve("/").Response.Span);
        Match secondTicket = Regex.Match(secondEntry, "documentEpoch:'([0-9A-F]{32})'");
        Check(firstTicket.Success && secondTicket.Success && firstTicket.Groups[1].Value != secondTicket.Groups[1].Value
            && responder.IsIssuedDocumentEpoch(WindowBridgeDocumentEpoch.Create(firstTicket.Groups[1].Value))
            && responder.IsIssuedDocumentEpoch(WindowBridgeDocumentEpoch.Create(secondTicket.Groups[1].Value)),
            "Window Bridge bootstrap did not issue ordered tickets for loaded documents.");
        Check(entry.Contains(CsWebUiWindowBridgeTransport.EndpointHandoffFunction, StringComparison.Ordinal)
            && entry.Contains("endpointRevision:manifest.revision", StringComparison.Ordinal)
            && entry.Contains("Object.create(null)", StringComparison.Ordinal),
            "Window Bridge bootstrap did not install its revisioned endpoint handoff carrier.");
        Check(Encoding.UTF8.GetString(responder.Serve("../secret").Response.Span).StartsWith("HTTP/1.1 404", StringComparison.Ordinal),
            "Window Bridge responder served a traversal path.");
    }

    private static void ManualNotesDescriptorUsesOneCheckedOwner()
    {
        string title = "Initial";
        var transport = new RecordingTransport();
        var owner = new WindowBridgeCheckedTitleField(() => title, value => title = value);
        using IDisposable routes = CsWebUiWindowBridgeNotesDescriptor.Attach(transport, owner,
            (connection, epoch, presentationId) => connection.ClientId == "mounted-client"
                && epoch.Value == DocumentEpoch && presentationId == "editor");
        Check(transport.RouteCount == 3, "Manual Notes descriptor did not bind its fixed route set.");
        string unmounted = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute, """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"foreign","value":"Blocked"}""", "other-client");
        Check(unmounted.Contains("\"current\":null", StringComparison.Ordinal) && title == "Initial",
            "Manual Notes descriptor read or mutated a model for an unmounted connection.");
        string staleDocument = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute, """{"documentEpoch":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","presentationId":"editor","requestId":"stale","value":"Blocked"}""", "mounted-client");
        Check(staleDocument.Contains("\"current\":null", StringComparison.Ordinal) && title == "Initial",
            "Manual Notes descriptor read or mutated a model for a stale document.");
        string releasedPresentation = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"released-editor","requestId":"released","value":"Blocked"}""", "mounted-client");
        Check(releasedPresentation.Contains("\"kind\":\"rejected\"", StringComparison.Ordinal) && title == "Initial",
            "Manual Notes descriptor let another mounted component authorize a released presentation.");
        string direct = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute, """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"set-1","value":"Direct"}""", "mounted-client");
        Check(direct.Contains("\"kind\":\"applied\"", StringComparison.Ordinal) && title == "Direct",
            "Manual direct title route did not use its shared checked owner.");
        using (JsonDocument directJson = JsonDocument.Parse(direct))
        {
            JsonElement current = directJson.RootElement.GetProperty("current");
            Check(current.GetProperty("value").GetString() == "Direct" && current.GetProperty("version").GetInt64() == 1
                && !current.TryGetProperty("Value", out _), "Manual Notes receipt did not preserve its lower-camel browser contract.");
        }
        string conflict = transport.Call(CsWebUiWindowBridgeNotesDescriptor.WriteCheckedRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"checked-1","value":"Checked","expected":{"value":"Initial","version":0}}""", "mounted-client");
        Check(conflict.Contains("\"kind\":\"conflict\"", StringComparison.Ordinal) && title == "Direct",
            "Manual checked title route did not retain the direct route's versioned owner state.");
        string strict = transport.Call(CsWebUiWindowBridgeNotesDescriptor.GetRoute, """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","unexpected":true}""", "mounted-client");
        Check(strict.Contains("\"kind\":\"rejected\"", StringComparison.Ordinal),
            "Manual Notes descriptor accepted an unknown snapshot member.");
        string duplicate = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"duplicate-1","requestId":"duplicate-2","value":"Ignored"}""", "mounted-client");
        Check(duplicate.Contains("\"kind\":\"rejected\"", StringComparison.Ordinal) && title == "Direct",
            "Manual Notes descriptor accepted duplicate request members.");
        string unknownSet = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"unknown-set","value":"Ignored","extra":true}""", "mounted-client");
        Check(unknownSet.Contains("\"kind\":\"rejected\"", StringComparison.Ordinal) && title == "Direct",
            "Manual Notes descriptor accepted an unknown direct title member.");
        string malformedBaseline = transport.Call(CsWebUiWindowBridgeNotesDescriptor.WriteCheckedRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"bad-baseline","value":"Ignored","expected":{"value":"Direct","version":1,"extra":true}}""", "mounted-client");
        Check(malformedBaseline.Contains("\"kind\":\"rejected\"", StringComparison.Ordinal) && title == "Direct",
            "Manual Notes descriptor accepted an unknown checked-title baseline member.");
    }

    private static void ManualNotesDescriptorPreservesCommittedSetterFailure()
    {
        string title = "Initial";
        int setterCalls = 0;
        var transport = new RecordingTransport();
        var owner = new WindowBridgeCheckedTitleField(
            () => title,
            value =>
            {
                setterCalls++;
                title = value;
                throw new InvalidOperationException("The setter failed after mutation.");
            });
        using IDisposable routes = CsWebUiWindowBridgeNotesDescriptor.Attach(transport, owner, (_, _, _) => true);
        string first = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"throw-after-mutation","value":"Committed"}""", "mounted-client");
        string retry = transport.Call(CsWebUiWindowBridgeNotesDescriptor.SetRoute,
            """{"documentEpoch":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presentationId":"editor","requestId":"throw-after-mutation","value":"Committed"}""", "mounted-client");
        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement root = document.RootElement;
        Check(!root.GetProperty("ok").GetBoolean()
            && root.GetProperty("kind").GetString() == "committed-with-error"
            && root.GetProperty("current").GetProperty("value").GetString() == "Committed"
            && root.GetProperty("current").GetProperty("version").GetInt64() == 1
            && root.GetProperty("error").GetString() == "The setter failed after mutation.",
            "Manual Notes descriptor did not encode a truthful committed setter failure.");
        Check(first == retry && setterCalls == 1 && title == "Committed",
            "Manual Notes descriptor replayed a committed setter failure.");
    }

    private static async Task HostOwnsManualWindowSessionAndScopeAsync()
    {
        var assets = new TestAssets("<!doctype html><head></head><body>Window Bridge</body>");
        var scope = new Scope();
        WindowBridgeReference? shellReference = null;
        WindowBridgeSession? sessionForTest = null;
        IWindowBridgeTransport? transportForTest = null;
        var heldSave = new TaskCompletionSource<WindowBridgeOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new CsWebUiWindowBridgeHost(new()
        {
            Assets = assets,
            OpenWindow = false,
            Title = "Window Bridge test",
            NativeCloseDrainTimeout = TimeSpan.FromMilliseconds(50),
            CreateWindowSession = transport =>
            {
                transportForTest = transport;
                var session = new WindowBridgeSession(transport, scope);
                sessionForTest = session;
                shellReference = session.Expose("shell", new object(), (routes, _, route) =>
                    routes.Bind(route, _ => "{\"ok\":true,\"state\":{\"kind\":\"shell\"},\"error\":null}"));
                return session;
            }
        });
        await host.StartAsync().ConfigureAwait(false);
        Check(host.Window is not null && host.Url?.Host == "127.0.0.1", "Window Bridge host did not expose a private IPv4 URL.");
        Check(shellReference is not null && host.BoundRouteCount == 2 && host.NativeRegistrationCalls == 2,
            "Manual fixed fixture did not retain its route and host document handshake over one dispatcher plus disconnect registrations.");
        using (var client = new HttpClient())
        {
            string firstEntry = await client.GetStringAsync(host.Url!).ConfigureAwait(false);
            string secondEntry = await client.GetStringAsync(host.Url!).ConfigureAwait(false);
            Check(firstEntry.Contains("runicCsWebUi", StringComparison.Ordinal), "Window Bridge host did not serve its authenticated bootstrap after readiness.");
            string firstTicket = Regex.Match(firstEntry, "documentEpoch:'([0-9A-F]{32})'").Groups[1].Value;
            string secondTicket = Regex.Match(secondEntry, "documentEpoch:'([0-9A-F]{32})'").Groups[1].Value;
            Check(firstTicket.Length == 32 && secondTicket.Length == 32 && firstTicket != secondTicket,
                "The host did not issue distinct ordered entry tickets.");
            using WindowBridgeEndpointLease late = transportForTest!.Bind("late.dialog", _ => "{\"ok\":true}");
            string secondBegin = host.BeginDocument(new DocumentArguments(secondTicket));
            using JsonDocument admitted = JsonDocument.Parse(secondBegin);
            Check(admitted.RootElement.GetProperty("ok").GetBoolean()
                && admitted.RootElement.GetProperty("manifest").GetProperty("endpoints").TryGetProperty("late.dialog", out _),
                "A document begin did not recover a route added after its HTML map was captured.");
            string delayedFirst = host.BeginDocument(new DocumentArguments(firstTicket));
            Check(delayedFirst.Contains("stale-document", StringComparison.Ordinal),
                "A delayed older entry ticket replaced the newer document.");
            string forged = host.BeginDocument(new DocumentArguments(secondTicket[..^1] + (secondTicket[^1] == 'A' ? 'B' : 'A')));
            Check(forged.Contains("unissued-document", StringComparison.Ordinal),
                "A forged document ticket was admitted by the host.");
        }
        WindowBridgeOperationAcceptance accepted = sessionForTest!.StartOperation(shellReference!, "held-save", _ => heldSave.Task);
        Check(accepted.Accepted, "Window Bridge fixture did not admit its held Save operation.");
        await host.StopAsync().ConfigureAwait(false);
        Check(host.SessionDrain is { IsCompleted: false }, "Native host waited indefinitely for a cancellation-ignoring operation.");
        Check(scope.Disposals == 0, "Window Bridge scope disposed before its accepted operation drained.");
        Task dispose = host.DisposeAsync().AsTask();
        Check(!dispose.IsCompleted, "Window Bridge disposal returned before its retained scope drained.");
        heldSave.SetResult(WindowBridgeOperationResult.Succeeded());
        await dispose.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await host.StopAsync().ConfigureAwait(false);
        Check(scope.Disposals == 1, "Window Bridge host did not dispose the session-owned scope exactly once.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Scope : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class DocumentArguments(string epoch) : WindowBridgeArguments
    {
        public WindowBridgeConnection Connection { get; } = WindowBridgeConnection.Create("test-client", "test-connection");
        public string GetString() => JsonSerializer.Serialize(new { documentEpoch = epoch });
        public long GetInt64() => throw new NotSupportedException();
        public bool GetBoolean() => throw new NotSupportedException();
    }

    private sealed class RecordingTransport : IWindowBridgeTransport
    {
        private readonly Dictionary<string, Func<WindowBridgeArguments, string>> _routes = new(StringComparer.Ordinal);
        internal int RouteCount => _routes.Count;
        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> handler)
        {
            _routes.Add(route, handler);
            return WindowBridgeEndpointLease.Direct(route, new Lease(() => _routes.Remove(route)));
        }
        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            throw new NotSupportedException();
        internal string Call(string route, string payload, string clientId) => _routes[route](new Arguments(payload, clientId));

        private sealed class Arguments(string payload, string clientId) : WindowBridgeArguments
        {
            public WindowBridgeConnection Connection { get; } = WindowBridgeConnection.Create(clientId, "test-connection");
            public string GetString() => payload;
            public long GetInt64() => throw new NotSupportedException();
            public bool GetBoolean() => throw new NotSupportedException();
        }

        private sealed class Lease(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class TestAssets : IAssetSource
    {
        private readonly byte[] _entry;
        public TestAssets(string entry)
        {
            _entry = Encoding.UTF8.GetBytes(entry);
            Manifest = new AssetManifest([
                new AssetDescriptor("index.html", "text/html", _entry.Length,
                    Convert.ToHexString(SHA256.HashData(_entry)).ToLowerInvariant(), isEntryPoint: true)
            ]);
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
            return ValueTask.FromResult<Stream>(new MemoryStream(_entry, writable: false));
        }
    }
}
