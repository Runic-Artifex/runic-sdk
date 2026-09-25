using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;
using Runic.Assets;

/// <summary>
/// One manual fixed-route Notes window for the headless Chromium callback
/// check. It intentionally uses neither the old mailbox nor a generator.
/// </summary>
internal static class WindowBridgeBrowserFixture
{
    internal static async Task<int> RunAsync()
    {
        var editor = new EditorModel();
        var title = new WindowBridgeCheckedTitleField(() => editor.Title, value => editor.Title = value);
        var scope = new OwnedScope();
        var heldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowBridgeSession? session = null;
        WindowBridgeReference? editorReference = null;
        WindowBridgeReference? dialogReference = null;
        WindowBridgeReference? batchReference = null;
        BatchModel? batchModel = null;
        WindowBridgeConnection? saveOwner = null;
        WindowBridgeDocumentEpoch? saveDocument = null;
        string? saveRequestId = null;
        var saveOwnerGate = new object();
        var dialogGate = new object();
        var batchGate = new object();
        WindowBridgeEndpointLease? refreshEndpoint = null;
        CsWebUiWindowBridgeInvalidationPublisher? invalidations = null;
        var refreshRegistration = new InvalidationRegistration();
        await using var host = new CsWebUiWindowBridgeHost(new CsWebUiWindowBridgeHostOptions
        {
            Assets = new EntryAssets(),
            OpenWindow = false,
            Title = "Window Bridge Notes browser fixture",
            CreateWindowSession = transport =>
            {
                session = new WindowBridgeSession(transport, scope);
                var shell = new ShellModel();
                WindowBridgeReference shellReference = session.Expose("shell", shell, (routes, _, route) =>
                    routes.Bind(route, _ => "{\"ok\":true,\"kind\":\"shell\"}"));
                editorReference = session.Present(shellReference, "main", "editor", editor, (routes, _, route) =>
                {
                    WindowBridgeEndpointLease endpoint = routes.Bind(route, _ => "{\"ok\":true,\"kind\":\"editor\"}");
                    WindowBridgeEndpointLease refresh = routes.Bind(route + ".refresh", _ => "{\"ok\":false,\"kind\":\"rejected\"}");
                    refreshEndpoint = refresh;
                    try
                    {
                        WindowBridgeEndpointLease[] titleEndpoints = CsWebUiWindowBridgeNotesDescriptor.AttachEndpoints(routes, title,
                            (connection, epoch, presentationId) => session.HasPresentation(editorReference!, connection, epoch, presentationId));
                        return WindowBridgeAttachment.Create(refreshRegistration, [endpoint, refresh, .. titleEndpoints]);
                    }
                    catch { endpoint.Dispose(); refresh.Dispose(); throw; }
                });
                scope.Add(transport.Bind("notes.editor.mount", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryPresentationRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        _ = session.Mount(editorReference!, arguments.Connection, epoch, presentationId!);
                        return "{\"ok\":true}";
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                    {
                        return "{\"ok\":false,\"kind\":\"rejected\"}";
                    }
                }));
                scope.Add(transport.Bind("notes.editor.unmount", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryPresentationRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId)
                            || !session.Unmount(editorReference!, arguments.Connection, epoch, presentationId!))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        return "{\"ok\":true}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                // Browser-reload witness only. It is deliberately a fixture
                // route, so it cannot become an application bridge surface.
                scope.Add(transport.Bind("notes.debug.connection", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        return JsonSerializer.Serialize(new
                        {
                            ok = true,
                            clientId = arguments.Connection.ClientId,
                            connectionId = arguments.Connection.ConnectionId,
                            documentEpoch = epoch.Value,
                            current = session.IsCurrentDocument(arguments.Connection, epoch),
                            mounted = session.HasPresentation(editorReference!, arguments.Connection, epoch),
                        });
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                scope.Add(transport.Bind("notes.save.start", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (request.RootElement.ValueKind != JsonValueKind.Object
                            || request.RootElement.EnumerateObject().Count() != 3
                            || !request.RootElement.TryGetProperty("documentEpoch", out JsonElement document)
                            || document.ValueKind != JsonValueKind.String
                            || !request.RootElement.TryGetProperty("presentationId", out JsonElement presentation)
                            || presentation.ValueKind != JsonValueKind.String
                            || !request.RootElement.TryGetProperty("requestId", out JsonElement id)
                            || id.ValueKind != JsonValueKind.String)
                            return "{\"accepted\":false,\"kind\":\"rejected\"}";
                        WindowBridgeDocumentEpoch epoch = WindowBridgeDocumentEpoch.Create(document.GetString()!);
                        string? presentationId = presentation.GetString();
                        string? requestId = id.GetString();
                        if (string.IsNullOrWhiteSpace(presentationId) || presentationId.Length > 128 || requestId is null)
                            return "{\"accepted\":false,\"kind\":\"rejected\"}";
                        WindowBridgeOperationAcceptance acceptance = session.StartOperationFromPresentation(editorReference!, arguments.Connection,
                            epoch, presentationId, requestId,
                            async _ =>
                            {
                                string captured = title.Snapshot().Value;
                                await heldSave.Task.ConfigureAwait(false);
                                editor.SavedTitle = captured;
                                return WindowBridgeOperationResult.Succeeded();
                            }, _ => JsonSerializer.Serialize(new { savedTitle = editor.SavedTitle }));
                        if (acceptance.Accepted)
                            lock (saveOwnerGate) { saveOwner = arguments.Connection; saveDocument = epoch; saveRequestId = requestId; }
                        return JsonSerializer.Serialize(new
                        {
                            accepted = acceptance.Accepted,
                            requestId = acceptance.RequestId,
                            kind = acceptance.Status.Kind.ToString().ToLowerInvariant(),
                        });
                    }
                    catch (JsonException) { return "{\"accepted\":false,\"kind\":\"rejected\"}"; }
                }));
                scope.Add(transport.Bind("notes.save.status", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        WindowBridgeDocumentEpoch epoch = WindowBridgeDocumentEpoch.Create(request.RootElement.GetProperty("documentEpoch").GetString()!);
                        string? requestId = request.RootElement.GetProperty("requestId").GetString();
                        if (string.IsNullOrWhiteSpace(requestId))
                            return "{\"kind\":\"rejected\",\"state\":null}";
                        WindowBridgeOperationStatus status = session.LookupOperationFromPresentation(
                            editorReference, arguments.Connection, epoch, requestId);
                        return JsonSerializer.Serialize(new
                        {
                            requestId = status.RequestId,
                            kind = status.Kind.ToString().ToLowerInvariant(),
                            state = status.State,
                            error = status.Error,
                        });
                    }
                    catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                    {
                        return "{\"kind\":\"rejected\",\"state\":null}";
                    }
                }));
                // Ordinary awaited Save joins the accepted window operation.
                // The same initiating document owns observation even when the
                // Editor presentation has already detached.
                scope.Add(transport.BindAsync("notes.save.wait", async (arguments, cancellationToken) =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        WindowBridgeDocumentEpoch epoch = WindowBridgeDocumentEpoch.Create(request.RootElement.GetProperty("documentEpoch").GetString()!);
                        string? requestId = request.RootElement.GetProperty("requestId").GetString();
                        if (string.IsNullOrWhiteSpace(requestId))
                            return "{\"kind\":\"rejected\",\"state\":null}";
                        WindowBridgeOperationStatus status = await session.WaitForTerminalFromPresentationAsync(
                            editorReference!, arguments.Connection, epoch, requestId, cancellationToken).ConfigureAwait(false);
                        return JsonSerializer.Serialize(new
                        {
                            requestId = status.RequestId,
                            kind = status.Kind.ToString().ToLowerInvariant(),
                            state = status.State,
                            error = status.Error,
                        });
                    }
                    catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                    {
                        return "{\"kind\":\"rejected\",\"state\":null}";
                    }
                }));
                // This is a window-level navigation action: leaving Editor
                // retires its shared logical content, including all mounts.
                scope.Add(transport.Bind("notes.navigate.preview", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        session.Clear(shellReference, "main");
                        return "{\"ok\":true}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                scope.Add(transport.Bind("notes.dialog.open", arguments =>
                {
                    WindowBridgeDocumentEpoch epoch;
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? parsed)
                            || !session.HasPresentation(editorReference!, arguments.Connection, parsed))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        epoch = parsed;
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                    WindowBridgeReference dialog;
                    lock (dialogGate)
                    {
                        if (dialogReference is not null)
                        {
                            _ = session.Mount(dialogReference, arguments.Connection, epoch, "dialog");
                            return JsonSerializer.Serialize(new { ok = true, reference = dialogReference, existing = true });
                        }
                        dialog = session.Present(shellReference, "dialog", "dialog", new DialogModel(), (routes, _, route) =>
                            WindowBridgeAttachment.Create(routes.Bind(route, dialogArguments =>
                            {
                                WindowBridgeDocumentEpoch? dialogEpoch;
                                try
                                {
                                    using JsonDocument request = JsonDocument.Parse(dialogArguments.GetString());
                                    if (!TryDocumentOnly(request.RootElement, out dialogEpoch))
                                        return "{\"ok\":false,\"kind\":\"rejected\"}";
                                }
                                catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                                WindowBridgeReference? active;
                                lock (dialogGate) active = dialogReference;
                                return active is not null && session.HasPresentation(active, dialogArguments.Connection, dialogEpoch)
                                    ? "{\"ok\":true,\"kind\":\"dialog\"}"
                                    : "{\"ok\":false,\"kind\":\"rejected\"}";
                            })));
                        dialogReference = dialog;
                    }
                    _ = session.Mount(dialog, arguments.Connection, epoch, "dialog");
                    return JsonSerializer.Serialize(new { ok = true, reference = dialog, existing = false });
                }));
                scope.Add(transport.Bind("notes.dialog.close", arguments =>
                {
                    WindowBridgeDocumentEpoch? epoch;
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                    WindowBridgeReference? dialog;
                    lock (dialogGate) dialog = dialogReference;
                    if (dialog is null || !session.HasPresentation(dialog, arguments.Connection, epoch))
                        return "{\"ok\":false,\"kind\":\"rejected\"}";
                    session.Clear(shellReference, "dialog");
                    lock (dialogGate) dialogReference = null;
                    return "{\"ok\":true}";
                }));
                // Fixture-only browser stress seam. One Expose attachment owns
                // all 500 logical routes, so WindowBridgeSession's attachment
                // batch must publish exactly one complete map on add and one on
                // retirement. It deliberately has no generated or public API.
                scope.Add(transport.Bind("notes.debug.batch.open", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }

                    lock (batchGate)
                    {
                        if (batchReference is not null)
                            return JsonSerializer.Serialize(new { ok = true, reference = batchReference, existing = true, routeCount = BatchModel.RouteCount });

                        batchModel = new BatchModel();
                        BatchModel model = batchModel;
                        batchReference = session.Expose("batch", model, (routes, _, route) =>
                        {
                            var endpoints = new WindowBridgeEndpointLease[BatchModel.RouteCount];
                            for (var index = 0; index < endpoints.Length; index++)
                            {
                                int routeIndex = index;
                                endpoints[index] = routes.Bind(route + ".route." + routeIndex,
                                    _ => JsonSerializer.Serialize(new { ok = true, kind = "batch", index = routeIndex }));
                            }
                            return WindowBridgeAttachment.Create(endpoints);
                        });
                        return JsonSerializer.Serialize(new { ok = true, reference = batchReference, existing = false, routeCount = BatchModel.RouteCount });
                    }
                }));
                scope.Add(transport.Bind("notes.debug.batch.close", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }

                    lock (batchGate)
                    {
                        if (batchModel is null) return "{\"ok\":false,\"kind\":\"rejected\"}";
                        session.Forget(batchModel);
                        batchModel = null;
                        batchReference = null;
                    }
                    return "{\"ok\":true}";
                }));
                scope.Add(transport.Bind("notes.debug.native-bindings", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.IsCurrentDocument(arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        return JsonSerializer.Serialize(new { registrations = ((CsWebUiWindowBridgeTransport)transport).NativeRegistrationCalls });
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                // Fixture-only trigger for the adapter-owned refresh hint. Its
                // exact-presentation guard lets the browser prove that a hint
                // is followed by an authorized typed pull, while a released
                // owner cannot cause that pull to reach a peer.
                scope.Add(transport.Bind("notes.debug.refresh", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryPresentationRequest(request.RootElement, out WindowBridgeDocumentEpoch? epoch, out string? presentationId)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch, presentationId!))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        return JsonSerializer.Serialize(new { ok = invalidations?.TryPublish(editor, "editor") == true });
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                // Regression for a legal route that has special meaning in a
                // normal JavaScript object literal or prototype chain.
                scope.Add(transport.Bind("__proto__", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.IsCurrentDocument(arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        return "{\"ok\":true,\"kind\":\"special-route\"}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                scope.Add(transport.Bind("notes.save.release", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        lock (saveOwnerGate)
                            if (saveOwner != arguments.Connection || saveDocument != epoch)
                                return "{\"ok\":false,\"kind\":\"rejected\"}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                    heldSave.TrySetResult();
                    return "{\"ok\":true}";
                }));
                // Reconnect fallback witness only: an accepted Save belongs to
                // the window after admission, while its initiating document
                // retains the ordinary status route's observation authority.
                // A newly admitted and mounted Editor can inspect/release it
                // here solely through this test-only route.
                scope.Add(transport.Bind("notes.debug.accepted-save.status", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch))
                            return "{\"kind\":\"rejected\",\"state\":null}";
                        string? requestId;
                        lock (saveOwnerGate) requestId = saveRequestId;
                        if (requestId is null) return "{\"kind\":\"unknown\",\"state\":null}";
                        WindowBridgeOperationStatus status = session.LookupOperation(editorReference!, requestId);
                        return JsonSerializer.Serialize(new
                        {
                            requestId = status.RequestId,
                            kind = status.Kind.ToString().ToLowerInvariant(),
                            state = status.State,
                            error = status.Error,
                        });
                    }
                    catch (JsonException) { return "{\"kind\":\"rejected\",\"state\":null}"; }
                }));
                scope.Add(transport.Bind("notes.debug.accepted-save.release", arguments =>
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(arguments.GetString());
                        if (!TryDocumentOnly(request.RootElement, out WindowBridgeDocumentEpoch? epoch)
                            || !session.HasPresentation(editorReference!, arguments.Connection, epoch))
                            return "{\"ok\":false,\"kind\":\"rejected\"}";
                        lock (saveOwnerGate)
                            if (saveRequestId is null)
                                return "{\"ok\":false,\"kind\":\"rejected\"}";
                        heldSave.TrySetResult();
                        return "{\"ok\":true}";
                    }
                    catch (JsonException) { return "{\"ok\":false,\"kind\":\"rejected\"}"; }
                }));
                return session;
            },
            ConfigureInvalidations = (_, publisher) =>
            {
                invalidations = publisher;
                refreshRegistration.Set(publisher.Register(editorReference!, refreshEndpoint!));
            },
        });
        try
        {
            await host.StartAsync().ConfigureAwait(false);
            Console.WriteLine("WINDOW_BRIDGE_BROWSER_URL=" + host.Url);
            Console.Out.Flush();
            _ = Console.ReadLine();
            heldSave.TrySetResult();
            await host.StopAsync().ConfigureAwait(false);
            if (scope.Disposals != 1)
                throw new InvalidOperationException("The browser fixture did not release its window scope.");
            Console.WriteLine("WINDOW_BRIDGE_BROWSER_STOPPED");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static bool TryDocumentOnly(JsonElement request, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch)
    {
        epoch = null;
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Count() != 1
            || !request.TryGetProperty("documentEpoch", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return false;
        try { epoch = WindowBridgeDocumentEpoch.Create(value.GetString()!); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool TryPresentationRequest(JsonElement request, [NotNullWhen(true)] out WindowBridgeDocumentEpoch? epoch,
        [NotNullWhen(true)] out string? presentationId)
    {
        epoch = null;
        presentationId = null;
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Count() != 2
            || !request.TryGetProperty("documentEpoch", out JsonElement document) || document.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("presentationId", out JsonElement presentation) || presentation.ValueKind != JsonValueKind.String)
            return false;
        string? value = presentation.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        try { epoch = WindowBridgeDocumentEpoch.Create(document.GetString()!); presentationId = value; return true; }
        catch (ArgumentException) { return false; }
    }

    private sealed class ShellModel;
    private sealed class DialogModel;
    private sealed class BatchModel
    {
        internal const int RouteCount = 500;
    }
    private sealed class EditorModel
    {
        internal string Title { get; set; } = "Draft";
        internal string? SavedTitle { get; set; }
    }

    private sealed class InvalidationRegistration : IDisposable
    {
        private IDisposable? _value;
        internal void Set(IDisposable value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Interlocked.Exchange(ref _value, value)?.Dispose();
        }
        public void Dispose() => Interlocked.Exchange(ref _value, null)?.Dispose();
    }

    private sealed class OwnedScope : IAsyncDisposable
    {
        private readonly List<IDisposable> _leases = [];
        internal int Disposals;
        internal void Add(IDisposable lease) => _leases.Add(lease);
        public ValueTask DisposeAsync()
        {
            Disposals++;
            foreach (IDisposable lease in _leases) lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EntryAssets : IAssetSource
    {
        private static readonly byte[] Html = Encoding.UTF8.GetBytes(
            "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>Window Bridge browser fixture</body></html>");
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
}
