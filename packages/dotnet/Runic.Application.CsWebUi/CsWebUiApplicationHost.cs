using System.Security.Cryptography;
using System.Text;
using CsWebUi;
using WebUiNative = CsWebUi.Native.WebUiNative;
using Runic.Application.Bridge;
using Runic.Assets;

namespace Runic.Application.CsWebUi;

/// <summary>Configures a CS-WebUI presentation for an otherwise host-independent application.</summary>
public sealed record CsWebUiApplicationHostOptions
{
    /// <summary>Gets the manifest-owned assets to serve. No disk-root fallback is used.</summary>
    public required IAssetSource Assets { get; init; }
    /// <summary>Gets the requested installed browser.</summary>
    public WebUiBrowser Browser { get; init; } = WebUiBrowser.Any;
    /// <summary>Gets whether startup opens a browser; false serves for external browser testing.</summary>
    public bool OpenWindow { get; init; } = true;
    /// <summary>Gets the window title.</summary>
    public string Title { get; init; } = "Runic Application";
    /// <summary>Gets an optional explicit generated bridge session factory.</summary>
    public Func<ApplicationBridgeSession>? CreateBridgeSession { get; init; }
    /// <summary>Gets the bridge frame limits.</summary>
    public BridgeLimits Limits { get; init; } = BridgeLimits.Default;
    /// <summary>Gets the maximum size of a served asset, excluding HTTP headers.</summary>
    public int MaxAssetBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Gets whether native close veto is required. CS-WebUI rejects this unsupported capability before startup.</summary>
    public bool RequireNativeCloseConfirmation { get; init; }
}

/// <summary>Hosts generated Runic application members on the native WebUI transport.</summary>
/// <remarks>Owns the process-wide WebUI runtime. Only one CS-WebUI application host
/// may start in a process, and stopping it also closes any additional WebUI windows.</remarks>
public sealed class CsWebUiApplicationHost : IApplicationHost
{
    private static int _runtimeClaimed;
    private const string FrameBinding = "__runicApplicationFrame";
    private const string PollBinding = "__runicApplicationPoll";
    private readonly CsWebUiApplicationHostOptions _options;
    private readonly string _credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly List<WebUiBinding> _bindings = [];
    private BridgeMailbox? _mailbox;
    private int _started;
    private int _stopped;
    private bool _ownsRuntime;

    /// <summary>Creates a host and validates capabilities before loading native code.</summary>
    public CsWebUiApplicationHost(CsWebUiApplicationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Assets);
        ArgumentNullException.ThrowIfNull(options.Limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAssetBytes, 1024);
        if (options.RequireNativeCloseConfirmation)
            throw new NotSupportedException("CS-WebUI does not support native close confirmation. Select Runic Desktop or use application-level confirmation.");
        _options = options;
    }

    /// <summary>Gets the native window after startup, for explicit CS-WebUI-specific operations.</summary>
    public WebUiWindow? Window { get; private set; }
    /// <summary>Gets the local entry URL after startup.</summary>
    public Uri? Url { get; private set; }

    /// <inheritdoc />
    public async ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A CS-WebUI application host can start exactly once.");
        await _options.Assets.ValidateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _options.CreateBridgeSession?.Invoke() ?? RunicApplicationBridgeCompositionRegistry.CreateSession(services) as ApplicationBridgeSession
                ?? throw new InvalidOperationException("No generated application bridge session was registered.");
            _mailbox = new BridgeMailbox(session, _options.Limits);
            if (Interlocked.CompareExchange(ref _runtimeClaimed, 1, 0) != 0)
                throw new InvalidOperationException("A CS-WebUI application host already owns this process's native runtime. Start another application in a separate process.");
            _ownsRuntime = true;
            WebUiApplication.SetConfiguration(WebUiConfiguration.UseCookies, true);
            Window = new WebUiWindow();
            Window.SetPublic(false);
            WebUiNative.SetEventBlocking(Window.Id, 0);
            Window.SetFileHandler(Serve, new() { MaxResponseBytes = checked(_options.MaxAssetBytes + 16 * 1024) });
            _bindings.Add(Window.BindAsync(FrameBinding, HandleFrameAsync));
            _bindings.Add(Window.BindAsync(PollBinding, HandlePollAsync));
            _bindings.Add(Window.BindAsync("", async (evt, _) =>
            {
                if (evt.EventType == WebUiEventType.Disconnected && _mailbox is { } mailbox)
                    await mailbox.DisconnectAsync((ulong)evt.ClientId, (ulong)evt.ConnectionId).ConfigureAwait(false);
                return WebUiResult.None;
            }));
            string entry = _options.Assets.Manifest.EntryPoint.RelativePath;
            Url = new Uri(Window.StartServer(entry));
            if (_options.OpenWindow) Window.ShowInBrowser(Url.AbsoluteUri, _options.Browser);
        }
        catch { await StopAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private bool Authenticate(WebUiEvent evt, nuint count) =>
        evt.EventType == WebUiEventType.Callback && evt.ArgumentCount == count &&
        WebUiNative.InterfaceGetSizeAt(evt.WindowId, evt.EventNumber, 0) == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(evt.GetString()), Encoding.ASCII.GetBytes(_credential));

    private async ValueTask<WebUiResult> HandleFrameAsync(WebUiEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            if (!Authenticate(evt, 2) || WebUiNative.InterfaceGetSizeAt(evt.WindowId, evt.EventNumber, 1) > (nuint)_options.Limits.MaxFrameBytes)
                throw new InvalidOperationException("Invalid application callback.");
            return WebUiResult.FromString(await _mailbox!.DispatchAsync((ulong)evt.ClientId, (ulong)evt.ConnectionId, evt.GetBytes(1), cancellationToken).ConfigureAwait(false));
        }
        catch { evt.CloseClient(); return WebUiResult.FromString("null"); }
    }

    private async ValueTask<WebUiResult> HandlePollAsync(WebUiEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            if (!Authenticate(evt, 1)) throw new InvalidOperationException("Invalid event poll.");
            return WebUiResult.FromString(await _mailbox!.PollAsync((ulong)evt.ClientId, (ulong)evt.ConnectionId, cancellationToken).ConfigureAwait(false));
        }
        catch { evt.CloseClient(); return WebUiResult.FromString("null"); }
    }

    private WebUiFileHandlerResult Serve(string path)
    {
        try
        {
            string normalized = string.IsNullOrEmpty(path.Trim('/')) ? _options.Assets.Manifest.EntryPoint.RelativePath : AssetPath.Normalize(path.TrimStart('/'));
            if (!_options.Assets.Manifest.TryGetAsset(normalized, out var asset) || asset is null)
                return Response(404, "text/plain", []);
            if (asset.Length > _options.MaxAssetBytes) return Response(413, "text/plain", []);
            using var input = _options.Assets.OpenReadAsync(asset.RelativePath).AsTask().GetAwaiter().GetResult();
            using var output = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                if (output.Length + read > _options.MaxAssetBytes) return Response(413, "text/plain", []);
                output.Write(buffer, 0, read);
            }
            byte[] body = output.ToArray();
            if (asset.IsEntryPoint)
            {
                string bootstrap = "<script src=\"/webui.js\"></script><script>globalThis.runicCsWebUi={credential:'" + _credential +
                    "',maxFrameBytes:" + _options.Limits.MaxFrameBytes + "};document.title=" + ("\"" + System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(_options.Title) + "\"") + ";</script>";
                string html = Encoding.UTF8.GetString(body);
                // Bootstrap must run before the application's module scripts.
                int head = html.IndexOf('>', html.IndexOf("<head", StringComparison.OrdinalIgnoreCase) is var pos && pos >= 0 ? pos : 0);
                body = Encoding.UTF8.GetBytes(head >= 0 ? html.Insert(head + 1, bootstrap) : bootstrap + html);
            }
            return Response(200, asset.MediaType, body);
        }
        catch { return Response(404, "text/plain", []); }
    }

    private static WebUiFileHandlerResult Response(int status, string mediaType, byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {mediaType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        byte[] result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0); body.CopyTo(result, header.Length);
        return WebUiFileHandlerResult.FromResponse(result);
    }

    /// <inheritdoc />
    public async ValueTask WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_options.OpenWindow) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return; }
        while (Window?.IsShown == true) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (_mailbox is { } mailbox) await mailbox.DisposeAsync().ConfigureAwait(false);
        // StartServer uses a persistent native server. Close/Destroy alone can
        // time out and free its mutexes while its thread is still running in
        // CsWebUi.Native 2.5.0-beta.4.4. End the owned process runtime first.
        if (_ownsRuntime) WebUiApplication.Exit();
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        Window?.Close(); Window?.Dispose(); Window = null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
}

/// <summary>Selects CS-WebUI without changing application members or frontend contracts.</summary>
public static class CsWebUiApplicationBuilderExtensions
{
    /// <summary>Uses the independently packaged native WebUI presentation host.</summary>
    public static RunicApplicationBuilder UseCsWebUi(this RunicApplicationBuilder builder, CsWebUiApplicationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseHost(new CsWebUiApplicationHost(options));
    }
}
