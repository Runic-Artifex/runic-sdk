using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using CsWebUi;
using WebUiNative = CsWebUi.Native.WebUiNative;
using Runic.Application.Bridge;

namespace Runic.Application.CsWebUi;

/// <summary>
/// Internal experimental host for one manually composed View-first window.
/// It deliberately bypasses ApplicationBridgeSession and BridgeMailbox.
/// </summary>
internal sealed class CsWebUiWindowBridgeHost : IAsyncDisposable
{
    internal const string DocumentBeginRoute = "__runicBridgeDocumentBegin";
    private readonly CsWebUiWindowBridgeHostOptions _options;
    private readonly string _credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly List<WebUiBinding> _hostBindings = [];
    private int _started;
    private TaskCompletionSource? _stopCompletion;
    private bool _ownsRuntime;
    private CsWebUiWindowBridgeAssetResponder? _assets;
    private CsWebUiWindowBridgeTransport? _transport;
    private WindowBridgeSession? _session;
    private CsWebUiWindowBridgeInvalidationPublisher? _invalidations;
    private WindowBridgeEndpointLease? _documentBeginLease;

    internal CsWebUiWindowBridgeHost(CsWebUiWindowBridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Assets);
        ArgumentNullException.ThrowIfNull(options.CreateWindowSession);
        ArgumentNullException.ThrowIfNull(options.Limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAssetBytes, 1024);
        if (options.NativeCloseDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Native close drain timeout must be positive.");
        _options = options;
    }

    internal WebUiWindow? Window { get; private set; }
    internal Uri? Url { get; private set; }
    internal int BoundRouteCount => _transport?.BoundRouteCount ?? 0;
    /// <summary>Observed adapter/host native binding registration calls, not a capacity metric.</summary>
    internal int NativeRegistrationCalls => (_transport?.NativeRegistrationCalls ?? 0) + _hostBindings.Count;
    internal bool CredentialMatches(string value) => _transport?.CredentialMatches(value) ?? false;
    /// <summary>Completes after cancelled operations and the window scope have drained.</summary>
    internal Task? SessionDrain { get; private set; }

    internal async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A CS-WebUI Window Bridge host can start exactly once.");
        await _options.Assets.ValidateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CsWebUiNativeRuntimeClaim.Claim();
            _ownsRuntime = true;
            WebUiApplication.SetConfiguration(WebUiConfiguration.UseCookies, true);
            Window = new WebUiWindow();
            Window.SetPublic(false);
            WebUiNative.SetEventBlocking(Window.Id, 0);
            _assets = new CsWebUiWindowBridgeAssetResponder(_options.Assets, _credential, _options.Title,
                _options.Limits.MaxFrameBytes, _options.MaxAssetBytes)
            {
                ReadinessPath = "/.runic-ready/" + Guid.NewGuid().ToString("N")
            };
            Window.SetFileHandler(_assets.Serve, new() { MaxResponseBytes = checked(_options.MaxAssetBytes + 16 * 1024) });
            _transport = new CsWebUiWindowBridgeTransport(Window, _credential, _options.Limits);
            var session = _options.CreateWindowSession(_transport);
            _session = session;
            _invalidations = new CsWebUiWindowBridgeInvalidationPublisher(session, _transport);
            _options.ConfigureInvalidations?.Invoke(session, _invalidations);
            _assets.DispatchEndpointBootstrap = () => _transport.EndpointManifestBootstrapJson;
            _documentBeginLease = _transport.Bind(DocumentBeginRoute, BeginDocument);
            _hostBindings.Add(Window.Bind("", eventData =>
            {
                if (eventData.EventType == WebUiEventType.Disconnected)
                    session.Disconnect(WindowBridgeConnection.Create(
                        eventData.ClientId.ToString(CultureInfo.InvariantCulture),
                        eventData.ConnectionId.ToString(CultureInfo.InvariantCulture)));
                return WebUiResult.None;
            }));
            string entry = _options.Assets.Manifest.EntryPoint.RelativePath;
            var url = new UriBuilder(Window.StartServer(entry)) { Host = "127.0.0.1" }.Uri;
            await NativeServerReadiness.WaitAsync(new Uri(url, _assets.ReadinessPath), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            _assets.ReadinessPath = null;
            if (_options.OpenWindow) Window.ShowInBrowser(url.AbsoluteUri, _options.Browser);
            Url = url;
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal string BeginDocument(WindowBridgeArguments arguments)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(arguments.GetString());
            JsonElement root = request.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("documentEpoch", out JsonElement value) || value.ValueKind != JsonValueKind.String)
                return "{\"ok\":false,\"kind\":\"rejected\"}";
            WindowBridgeDocumentEpoch epoch = WindowBridgeDocumentEpoch.Create(value.GetString()!);
            if (_assets?.IsIssuedDocumentEpoch(epoch) != true)
                return "{\"ok\":false,\"kind\":\"unissued-document\"}";
            WindowBridgeDocumentAdmission admission = _session!.BeginDocument(arguments.Connection, epoch);
            if (!admission.Accepted)
                return WindowBridgeJson.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("ok", false);
                    writer.WriteString("kind", "stale-document");
                    writer.WriteString("error", admission.Error);
                    writer.WriteEndObject();
                });
            // A new page can miss RunJavaScript handoffs between its HTML
            // response and this call. The current atomic map/revision in the
            // reply closes that gap without adding a native binding.
            string manifest = _transport!.EndpointManifestBootstrapJson;
            return WindowBridgeJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteString("kind", "accepted");
                writer.WritePropertyName("manifest");
                writer.WriteRawValue(manifest, skipInputValidation: false);
                writer.WriteEndObject();
            });
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return "{\"ok\":false,\"kind\":\"rejected\"}";
        }
    }

    internal ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _stopCompletion, completion, null) is { } existing)
            return new(existing.Task);
        _ = StopCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        async Task Clean(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (_assets is not null) _assets.ReadinessPath = null;
        _invalidations?.StopAdmission();
        if (_session is not null)
        {
            WindowBridgeCloseAdmission close = _session.BeginClose();
            SessionDrain = close.Completion;
            if (await Task.WhenAny(close.Completion, Task.Delay(_options.NativeCloseDrainTimeout)).ConfigureAwait(false) == close.Completion)
                await Clean(() => close.Completion).ConfigureAwait(false);
            else
                ObserveDeferredDrain(close.Completion);
        }
        if (_invalidations is not null)
            await Clean(_invalidations.DrainAsync).ConfigureAwait(false);
        if (_documentBeginLease is not null)
            await Clean(() => { _documentBeginLease.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        if (_transport is not null) await Clean(() => { _transport.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        if (_ownsRuntime) await Clean(() => { WebUiApplication.Exit(); return Task.CompletedTask; }).ConfigureAwait(false);
        foreach (WebUiBinding binding in _hostBindings)
            await Clean(() => { binding.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        _hostBindings.Clear();
        await Clean(() => { Window?.Close(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Clean(() => { Window?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        Window = null;
        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    private static void ObserveDeferredDrain(Task completion)
    {
        _ = completion.ContinueWith(static finished => _ = finished.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Closes native UI using the bounded policy, then joins any retained window
    /// scope drain. <see cref="StopAsync"/> deliberately does not wait for work
    /// that ignored cancellation; disposal does so callers can safely release
    /// their root provider after an <c>await using</c> scope.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Exception? stopFailure = null;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { stopFailure = error; }
        Exception? drainFailure = null;
        if (SessionDrain is { } drain)
        {
            try { await drain.ConfigureAwait(false); }
            catch (Exception error) { drainFailure = error; }
        }
        if (stopFailure is not null && drainFailure is not null)
            throw new AggregateException(stopFailure, drainFailure);
        if (stopFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
        if (drainFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drainFailure).Throw();
    }
}
