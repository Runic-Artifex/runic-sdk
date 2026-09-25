using System.Text;
using System.Text.Json;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;

internal static class StableWindowBridgeDispatchTests
{
    private const string Credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    internal static async Task RunAsync()
    {
        await RetiresEndpointsWithoutGrowingNativeRegistrationsAsync();
        await DrainsInFlightHandlerAndLinksTransportCloseAsync();
        await HandsOffDynamicEndpointSnapshotsWithoutNewNativeBindingsAsync();
        await BatchesAttachmentEndpointManifestsAsync();
        await KeepsBootstrapSnapshotAtomicAcrossAnOpenBatchAsync();
        await KeepsAuthoritativeRoutesWhenManifestBroadcastFailsAsync();
        RejectsUnauthenticatedAndMalformedEnvelopes();
        Console.WriteLine("CS-WebUI stable dispatch: fixed native registration, endpoint retirement, dynamic handoff, in-flight drain, and guards passed.");
    }

    private static async Task RetiresEndpointsWithoutGrowingNativeRegistrationsAsync()
    {
        var native = new FakeNative();
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        Check(native.RegistrationCount == 1 && native.BindingName == CsWebUiWindowBridgeTransport.DispatchBinding,
            "The stable dispatcher did not install exactly one native BindAsync registration.");

        var calls = 0;
        using var first = transport.BindEndpoint("content-editor", arguments =>
        {
            calls++;
            Check(arguments.Connection == WindowBridgeConnection.Create("42", "7"),
                "The dispatcher did not retain the physical callback connection.");
            Check(arguments.GetString() == "{\"title\":\"Draft\"}", "The dispatcher changed the typed JSON payload.");
            return "{\"ok\":true}";
        });

        string firstReply = await native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(first), "{\"title\":\"Draft\"}"));
        Check(firstReply == "{\"ok\":true}" && calls == 1, "The first endpoint was not dispatched exactly once.");

        CsWebUiWindowBridgeEndpoint oldEndpoint = transport.DescriptorFor(first);
        string wrongGenerationReply = await native.DispatchAsync(Credential,
            Envelope(oldEndpoint with { Generation = oldEndpoint.Generation + 1 }, "{}"));
        Check(IsDisconnected(wrongGenerationReply) && !first.Drain.IsCompleted,
            "A same-ID wrong-generation dispatch changed the active endpoint's in-flight accounting.");
        first.Dispose();
        Check(first.Drain.IsCompleted, "A quiescent endpoint did not drain immediately on retirement.");
        using var replacement = transport.BindEndpoint("content-editor", _ =>
        {
            calls += 100;
            return "{\"ok\":true}";
        });
        string retiredReply = await native.DispatchAsync(Credential, Envelope(oldEndpoint, "{}"));
        Check(IsDisconnected(retiredReply) && calls == 1,
            "A retired endpoint reached its replacement handler.");

        for (int index = 0; index < 100; index++)
        {
            using WindowBridgeEndpointLease temporary = transport.BindEndpoint("dialog-" + index, _ => "{\"ok\":true}");
        }
        Check(native.RegistrationCount == 1 && transport.BoundRouteCount == 1 && transport.PeakLogicalEndpointCount == 2,
            "Logical endpoint churn changed the bounded native registration count or retained endpoints.");
    }

    private static async Task DrainsInFlightHandlerAndLinksTransportCloseAsync()
    {
        var native = new FakeNative();
        var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceAlive = true;
        var cancellationWasRequestedByRetirement = false;
        var lease = transport.BindEndpointAsync("content-async", async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            cancellationWasRequestedByRetirement = token.IsCancellationRequested;
            Check(resourceAlive, "Endpoint resources were released while an admitted callback was still running.");
            return "{\"ok\":true}";
        });

        Task<string> callback = native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(lease), "{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        lease.Dispose();
        Check(!lease.Drain.IsCompleted, "Endpoint retirement did not retain the in-flight handler.");
        release.TrySetResult();
        Check(await callback.ConfigureAwait(false) == "{\"ok\":true}", "An in-flight callback lost its truthful reply after retirement.");
        await lease.Drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        resourceAlive = false;
        Check(!cancellationWasRequestedByRetirement, "Endpoint retirement cancelled the callback token.");

        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using WindowBridgeEndpointLease cancellationLease = transport.BindEndpointAsync("content-close", async (_, token) =>
        {
            cancellationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "{\"ok\":true}";
        });
        Task<string> closingCallback = native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(cancellationLease), "{}"));
        await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        transport.Dispose();
        Check(IsDisconnected(await closingCallback.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)),
            "Transport close did not cancel a native BindAsync callback through its linked token.");
        await cancellationLease.Drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        var blockingNative = new FakeNative();
        var blockingTransport = new CsWebUiWindowBridgeTransport(blockingNative, Credential, BridgeLimits.Default);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblockCancellationCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using WindowBridgeEndpointLease blockingLease = blockingTransport.BindEndpointAsync("content-blocking-close", async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                cancellationCallbackEntered.TrySetResult();
                unblockCancellationCallback.Task.GetAwaiter().GetResult();
            });
            handlerEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "{\"ok\":true}";
        });
        Task<string> blockedCallback = blockingNative.DispatchAsync(Credential, Envelope(blockingTransport.DescriptorFor(blockingLease), "{}"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Task.Run(blockingTransport.Dispose).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await cancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        unblockCancellationCallback.TrySetResult();
        Check(IsDisconnected(await blockedCallback.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)),
            "A blocking cancellation callback prevented terminal callback cleanup.");
    }

    private static async Task HandsOffDynamicEndpointSnapshotsWithoutNewNativeBindingsAsync()
    {
        var native = new FakeNative();
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        using WindowBridgeEndpointLease first = transport.BindEndpoint("dialog.after-load", _ => "{\"ok\":true,\"dialog\":\"first\"}");
        EndpointHandoff opened = ReadHandoff(native.LastScript!);
        CsWebUiWindowBridgeEndpoint firstDescriptor = opened.Endpoint("dialog.after-load");
        Check(opened.Revision == 1 && firstDescriptor == transport.DescriptorFor(first),
            "A dialog endpoint added after bootstrap did not publish its current descriptor snapshot.");
        Check(native.RegistrationCount == 1,
            "A dynamic endpoint handoff installed an additional native binding.");

        first.Dispose();
        EndpointHandoff closed = ReadHandoff(native.LastScript!);
        Check(closed.Revision == 2 && !closed.Endpoints.ContainsKey("dialog.after-load"),
            "A retired dialog endpoint did not publish a descriptor removal snapshot.");
        using WindowBridgeEndpointLease replacement = transport.BindEndpoint("dialog.after-load", _ => "{\"ok\":true,\"dialog\":\"replacement\"}");
        EndpointHandoff reopened = ReadHandoff(native.LastScript!);
        CsWebUiWindowBridgeEndpoint replacementDescriptor = reopened.Endpoint("dialog.after-load");
        Check(reopened.Revision == 3 && replacementDescriptor != firstDescriptor && replacementDescriptor == transport.DescriptorFor(replacement),
            "A replacement dialog endpoint did not supersede its retired descriptor.");
        Check(native.RegistrationCount == 1,
            "A dynamic replacement endpoint changed the fixed native BindAsync count.");

        string stale = await native.DispatchAsync(Credential, Envelope(firstDescriptor, "{}")).ConfigureAwait(false);
        Check(IsDisconnected(stale), "A stale dynamic endpoint descriptor reached its replacement dialog.");
        string current = await native.DispatchAsync(Credential, Envelope(replacementDescriptor, "{}")).ConfigureAwait(false);
        Check(current.Contains("replacement", StringComparison.Ordinal), "The current dynamic dialog descriptor was not dispatchable.");

        using JsonDocument bootstrap = JsonDocument.Parse(transport.EndpointManifestBootstrapJson);
        Check(bootstrap.RootElement.GetProperty("revision").GetInt64() == reopened.Revision
            && bootstrap.RootElement.GetProperty("endpoints").GetProperty("dialog.after-load").GetProperty("endpoint").GetString() == replacementDescriptor.Id,
            "A fresh document bootstrap did not receive the latest dynamic endpoint map.");
    }

    private static async Task BatchesAttachmentEndpointManifestsAsync()
    {
        const int routeCount = 500;
        var native = new FakeNative();
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        await using var session = new WindowBridgeSession(transport);
        var model = new object();
        WindowBridgeEndpointLease[] endpoints = [];
        _ = session.Expose("batched", model, (routes, _, route) =>
        {
            endpoints = new WindowBridgeEndpointLease[routeCount];
            for (var index = 0; index < endpoints.Length; index++)
                endpoints[index] = routes.Bind(route + ".route." + index, _ => "{\"ok\":true}");
            return WindowBridgeAttachment.Create(endpoints);
        });
        Check(native.ScriptCount == 1, "One 500-route attachment published more than one endpoint manifest.");
        EndpointHandoff attached = ReadHandoff(native.LastScript!);
        Check(attached.Revision == 1 && attached.Endpoints.Count == routeCount,
            "The attachment batch did not publish one complete initial endpoint map.");
        string live = await native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(endpoints[routeCount / 2]), "{}"));
        Check(live.Contains("\"ok\":true", StringComparison.Ordinal), "A descriptor from the committed attachment map was not dispatchable.");

        session.Suspend(model);
        Check(native.ScriptCount == 2, "One 500-route attachment retirement published more than one endpoint manifest.");
        EndpointHandoff retired = ReadHandoff(native.LastScript!);
        Check(retired.Revision == 2 && retired.Endpoints.Count == 0,
            "The attachment retirement did not publish the authoritative empty endpoint map.");
        string stale = await native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(endpoints[routeCount / 2]), "{}"));
        Check(IsDisconnected(stale), "An attachment descriptor remained live after its batched retirement.");
        using JsonDocument bootstrap = JsonDocument.Parse(transport.EndpointManifestBootstrapJson);
        Check(bootstrap.RootElement.GetProperty("revision").GetInt64() == 2
            && bootstrap.RootElement.GetProperty("endpoints").EnumerateObject().Count() == 0,
            "A fresh document did not reconcile to the final batched retirement map.");
    }

    private static async Task KeepsBootstrapSnapshotAtomicAcrossAnOpenBatchAsync()
    {
        var native = new FakeNative();
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        using IDisposable batch = transport.BeginAttachmentUpdate();
        using WindowBridgeEndpointLease endpoint = transport.BindEndpoint("dialog.mid-batch", _ => "{\"ok\":true}");

        // BeginDocument reads this property after it has admitted the document.
        // Keep the batch open until the other thread has completed the read, so
        // this deterministically exercises the concurrent interleaving.
        string duringBatch = await Task.Run(() => transport.EndpointManifestBootstrapJson)
            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        using (JsonDocument snapshot = JsonDocument.Parse(duringBatch))
        {
            Check(snapshot.RootElement.GetProperty("revision").GetInt64() == 0
                && snapshot.RootElement.GetProperty("endpoints").EnumerateObject().Count() == 0,
                "A document-begin bootstrap observed a partial endpoint map under the old revision.");
        }
        Check(native.ScriptCount == 0, "An open manifest batch published before its commit.");

        batch.Dispose();
        using JsonDocument committed = JsonDocument.Parse(transport.EndpointManifestBootstrapJson);
        Check(committed.RootElement.GetProperty("revision").GetInt64() == 1
            && committed.RootElement.GetProperty("endpoints").GetProperty("dialog.mid-batch").GetProperty("endpoint").GetString() == transport.DescriptorFor(endpoint).Id
            && native.ScriptCount == 1,
            "The committed bootstrap did not atomically replace the prior complete map.");
    }

    private static async Task KeepsAuthoritativeRoutesWhenManifestBroadcastFailsAsync()
    {
        var native = new FakeNative { ThrowNextScript = true };
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, BridgeLimits.Default);
        WindowBridgeEndpointLease endpoint = transport.BindEndpoint("dialog.unacknowledged", _ => "{\"ok\":true}");
        Check(transport.BoundRouteCount == 1
            && transport.EndpointManifestJson.Contains("dialog.unacknowledged", StringComparison.Ordinal)
            && await native.DispatchAsync(Credential, Envelope(transport.DescriptorFor(endpoint), "{}")) == "{\"ok\":true}",
            "A failed browser handoff rolled back a valid authoritative endpoint.");
        native.ThrowNextScript = true;
        CsWebUiWindowBridgeEndpoint retired = transport.DescriptorFor(endpoint);
        endpoint.Dispose();
        Check(transport.BoundRouteCount == 0
            && !transport.EndpointManifestJson.Contains("dialog.unacknowledged", StringComparison.Ordinal)
            && IsDisconnected(await native.DispatchAsync(Credential, Envelope(retired, "{}"))),
            "A failed retirement handoff kept a stale endpoint live on the server.");
    }

    private static void RejectsUnauthenticatedAndMalformedEnvelopes()
    {
        var native = new FakeNative();
        using var transport = new CsWebUiWindowBridgeTransport(native, Credential, new BridgeLimits
        {
            MaxFrameBytes = 1024,
            MaxStringBytes = 64,
            MaxCollectionItems = 2,
        });
        var invoked = 0;
        using WindowBridgeEndpointLease endpoint = transport.BindEndpoint("content-guard", _ =>
        {
            invoked++;
            return "{\"ok\":true}";
        });

        CsWebUiWindowBridgeEndpoint descriptor = transport.DescriptorFor(endpoint);
        var badCredential = native.DispatchAsync(new string('B', 64), Envelope(descriptor, "{}")).GetAwaiter().GetResult();
        Check(IsDisconnected(badCredential) && native.LastCallback!.Closed && invoked == 0,
            "An unauthenticated callback reached a logical endpoint.");
        string duplicateMember = $"{{\"v\":1,\"v\":1,\"endpoint\":\"{descriptor.Id}\",\"generation\":{descriptor.Generation},\"payload\":{{}}}}";
        string malformed = native.DispatchAsync(Credential, duplicateMember).GetAwaiter().GetResult();
        Check(IsDisconnected(malformed) && native.LastCallback!.Closed && invoked == 0,
            "A malformed envelope reached a logical endpoint.");
        string unknown = native.DispatchAsync(Credential, "{\"v\":1,\"endpoint\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"generation\":1,\"payload\":{}}").GetAwaiter().GetResult();
        Check(IsDisconnected(unknown) && !native.LastCallback!.Closed && invoked == 0,
            "An unknown endpoint was not a non-invoking disconnected result.");
        string oversizedString = native.DispatchAsync(Credential, Envelope(descriptor,
            "{\"value\":\"" + new string('x', 65) + "\"}")).GetAwaiter().GetResult();
        Check(IsDisconnected(oversizedString) && native.LastCallback!.Closed && invoked == 0,
            "An oversized payload string reached a logical endpoint.");
        string oversizedCollection = native.DispatchAsync(Credential, Envelope(descriptor, "[1,2,3]")).GetAwaiter().GetResult();
        Check(IsDisconnected(oversizedCollection) && native.LastCallback!.Closed && invoked == 0,
            "An oversized payload collection reached a logical endpoint.");
    }

    private static string Envelope(CsWebUiWindowBridgeEndpoint endpoint, string payload) =>
        $"{{\"v\":1,\"endpoint\":\"{endpoint.Id}\",\"generation\":{endpoint.Generation},\"payload\":{payload}}}";

    private static bool IsDisconnected(string reply) => reply.Contains("\"disconnected\"", StringComparison.Ordinal);

    private static EndpointHandoff ReadHandoff(string script)
    {
        const string prefix = "?.(";
        int start = script.IndexOf(prefix, StringComparison.Ordinal);
        int end = script.LastIndexOf(");", StringComparison.Ordinal);
        if (start < 0 || end <= start + prefix.Length) throw new InvalidOperationException("The endpoint handoff script had no JSON payload.");
        string expression = script[(start + prefix.Length)..end];
        const string parsePrefix = "JSON.parse(";
        if (!expression.StartsWith(parsePrefix, StringComparison.Ordinal) || !expression.EndsWith(')'))
            throw new InvalidOperationException("The endpoint handoff did not parse its JSON safely.");
        string json = JsonSerializer.Deserialize<string>(expression[parsePrefix.Length..^1])!;
        using JsonDocument document = JsonDocument.Parse(json);
        var endpoints = new Dictionary<string, CsWebUiWindowBridgeEndpoint>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("endpoints").EnumerateObject())
        {
            endpoints.Add(property.Name, new CsWebUiWindowBridgeEndpoint(
                property.Value.GetProperty("endpoint").GetString()!,
                property.Value.GetProperty("generation").GetInt64()));
        }
        return new(document.RootElement.GetProperty("revision").GetInt64(), endpoints);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeNative : IWindowBridgeNative
    {
        private Func<IWindowBridgeNativeCallback, CancellationToken, ValueTask<string>>? _handler;
        internal int RegistrationCount { get; private set; }
        internal string? BindingName { get; private set; }
        internal FakeCallback? LastCallback { get; private set; }
        internal string? LastScript { get; private set; }
        internal int ScriptCount { get; private set; }
        internal bool ThrowNextScript { get; set; }

        public IDisposable BindAsync(string name, Func<IWindowBridgeNativeCallback, CancellationToken, ValueTask<string>> handler)
        {
            if (_handler is not null) throw new InvalidOperationException("The fake native window supports one dispatcher binding.");
            RegistrationCount++;
            BindingName = name;
            _handler = handler;
            return new Lease(this);
        }

        public void RunJavaScript(string script)
        {
            if (ThrowNextScript)
            {
                ThrowNextScript = false;
                throw new IOException("The browser socket failed during an unacknowledged handoff.");
            }
            LastScript = script;
            ScriptCount++;
        }

        internal async Task<string> DispatchAsync(string credential, string envelope, CancellationToken cancellationToken = default)
        {
            var callback = new FakeCallback(true, [Encoding.ASCII.GetBytes(credential), Encoding.UTF8.GetBytes(envelope)]);
            LastCallback = callback;
            return await (_handler?.Invoke(callback, cancellationToken)
                ?? ValueTask.FromResult("{\"ok\":false,\"error\":{\"kind\":\"disconnected\"}}"));
        }

        private void Unbind() => _handler = null;

        private sealed class Lease(FakeNative owner) : IDisposable
        {
            private FakeNative? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unbind();
        }
    }

    private sealed record EndpointHandoff(long Revision, Dictionary<string, CsWebUiWindowBridgeEndpoint> Endpoints)
    {
        internal CsWebUiWindowBridgeEndpoint Endpoint(string route) => Endpoints.TryGetValue(route, out CsWebUiWindowBridgeEndpoint? endpoint)
            ? endpoint
            : throw new InvalidOperationException($"The handoff did not contain {route}.");
    }

    private sealed class FakeCallback(bool isCallback, byte[][] arguments) : IWindowBridgeNativeCallback
    {
        public bool IsCallback { get; } = isCallback;
        public int ArgumentCount => arguments.Length;
        public ulong ClientId => 42;
        public ulong ConnectionId => 7;
        public bool Closed { get; private set; }
        public int ArgumentLength(int index) => arguments[index].Length;
        public byte[] GetBytes(int index) => arguments[index];
        public void CloseClient() => Closed = true;
    }
}
