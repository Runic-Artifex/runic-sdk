using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Desktop;
using WebUIToolkit.Hosting.WebUi;

namespace WebUIToolkit.Hosting.CsWebUi;

internal sealed class CsWebUiDesktopServices :
    IWebUiWindowAttachment,
    IWebUiNativeCloseNotification,
    IDesktopCapabilities,
    IDesktopApplicationLifetime,
    IDesktopWindow,
    IDesktopFocus,
    IDesktopDispatcher,
    IDesktopKeyboardAccelerators,
    IDesktopClipboard,
    IDesktopFileDialogs,
    IDesktopDropTarget,
    IDesktopExternalLauncher,
    IDesktopNotifications,
    IDesktopBrowserProfile,
    IDesktopBrowserStorage,
    IDesktopWindowManager,
    IDisposable
{
    private readonly object _gate = new();
    private readonly DesktopBrowserProfile? _profile;
    private readonly List<IDesktopCloseGuard> _closeGuards = [];
    private readonly Dictionary<string, Func<CancellationToken, ValueTask>> _accelerators = [];
    private readonly Dictionary<string, OwnedWindow> _ownedWindows = [];
    private readonly HashSet<string> _ownedWindowIds = [];
    private readonly CancellationTokenSource _stopping = new();
    private EventHandler<DesktopDrop>? _dropped;
    private IBrowserHost? _host;
    private IBrowserWindow? _window;
    private IBrowserWindowDesktopAdapter? _adapter;
    private int _disposed;

    internal CsWebUiDesktopServices(DesktopBrowserProfile? profile)
    {
        _profile = profile;
    }

    event EventHandler<DesktopDrop>? IDesktopDropTarget.Dropped
    {
        add
        {
            if (value is null)
            {
                return;
            }

            RequireCallable(DesktopCapability.DragAndDrop);
            lock (_gate)
            {
                _dropped += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _dropped -= value;
            }
        }
    }

    public DesktopCapabilityReport Report
    {
        get
        {
            lock (_gate)
            {
                return _adapter?.Capabilities ?? CreateDetachedReport();
            }
        }
    }

    public DesktopBrowserProfile? Current => _profile;

    public CancellationToken Stopping => _stopping.Token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        OwnedWindow[] ownedWindows;
        lock (_gate)
        {
            if (_adapter is not null)
            {
                _adapter.DesktopEventReceived -= OnDesktopEvent;
            }

            _closeGuards.Clear();
            _accelerators.Clear();
            ownedWindows = _ownedWindows.Values.ToArray();
            _ownedWindows.Clear();
            _ownedWindowIds.Clear();
            _dropped = null;
            _adapter = null;
            _window = null;
            _host = null;
        }

        foreach (OwnedWindow ownedWindow in ownedWindows)
        {
            try
            {
                ownedWindow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        if (!_stopping.IsCancellationRequested)
        {
            try
            {
                _stopping.Cancel();
            }
            catch (AggregateException)
            {
            }
        }

        _stopping.Dispose();
    }

    public void Attach(IBrowserHost browserHost, IBrowserWindow window)
    {
        ArgumentNullException.ThrowIfNull(browserHost);
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (_window is not null)
            {
                throw new InvalidOperationException(
                    "CsWebUi desktop services are already attached to a native window.");
            }

            _host = browserHost;
            _window = window;
            _adapter = window as IBrowserWindowDesktopAdapter;
            if (_adapter is not null)
            {
                _adapter.DesktopEventReceived += OnDesktopEvent;
            }
        }
    }

    public async ValueTask DetachAsync(
        IBrowserWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();
        OwnedWindow[] ownedWindows;
        lock (_gate)
        {
            if (_window is null)
            {
                return;
            }

            if (!ReferenceEquals(_window, window))
            {
                throw new InvalidOperationException(
                    "CsWebUi desktop services cannot detach a different native window.");
            }

            if (_adapter is not null)
            {
                _adapter.DesktopEventReceived -= OnDesktopEvent;
            }

            _accelerators.Clear();
            ownedWindows = _ownedWindows.Values.ToArray();
            _ownedWindows.Clear();
            _ownedWindowIds.Clear();
            _dropped = null;
            _adapter = null;
            _window = null;
            _host = null;
        }

        foreach (OwnedWindow ownedWindow in ownedWindows)
        {
            await ownedWindow.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask NativeWindowClosedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await CancelStoppingAsync().ConfigureAwait(false);
    }

    public IDisposable RegisterCloseGuard(IDesktopCloseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "A close guard cannot be registered after application stopping begins.");
            }

            _closeGuards.Add(guard);
        }

        return new CloseGuardRegistration(this, guard);
    }

    public async ValueTask<DesktopCloseDecision> RequestCloseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDesktopCloseGuard[] guards;
        IBrowserHost host;
        IBrowserWindow window;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return DesktopCloseDecision.Allow();
            }

            guards = _closeGuards.ToArray();
            host = _host ??
                throw CapabilityFailure(DesktopCapability.WindowFocus);
            window = _window ??
                throw CapabilityFailure(DesktopCapability.WindowFocus);
        }

        var request = new DesktopCloseRequest(DesktopCloseReason.Application);
        for (int index = guards.Length - 1; index >= 0; index--)
        {
            DesktopCloseDecision decision = await guards[index]
                .CanCloseAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        await CancelStoppingAsync().ConfigureAwait(false);
        await host.Dispatcher
            .InvokeAsync(window.CloseAsync, cancellationToken)
            .ConfigureAwait(false);
        return DesktopCloseDecision.Allow();
    }

    public bool CheckAccess()
    {
        lock (_gate)
        {
            return _host?.Dispatcher.CheckAccess() ?? false;
        }
    }

    public ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return GetHost().Dispatcher.InvokeAsync(operation, cancellationToken);
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken = default) =>
        InvokeAdapterAsync(
            DesktopCapability.WindowFocus,
            static (adapter, token) => adapter.FocusWindowAsync(token),
            cancellationToken);

    public ValueTask FocusElementAsync(
        string elementId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        return InvokeAdapterAsync(
            DesktopCapability.ElementFocus,
            (adapter, token) => adapter.FocusElementAsync(elementId, token),
            cancellationToken);
    }

    public ValueTask SetSizeAsync(
        DesktopSize size,
        CancellationToken cancellationToken = default) =>
        InvokeAdapterAsync(
            DesktopCapability.WindowPlacement,
            (adapter, token) => adapter.SetSizeAsync(size, token),
            cancellationToken);

    public ValueTask SetPositionAsync(
        DesktopPosition position,
        CancellationToken cancellationToken = default) =>
        InvokeAdapterAsync(
            DesktopCapability.WindowPlacement,
            (adapter, token) => adapter.SetPositionAsync(position, token),
            cancellationToken);

    public ValueTask CenterAsync(CancellationToken cancellationToken = default) =>
        InvokeAdapterAsync(
            DesktopCapability.WindowPlacement,
            static (adapter, token) => adapter.CenterAsync(token),
            cancellationToken);

    public ValueTask SetStateAsync(
        DesktopWindowState state,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        return InvokeAdapterAsync(
            DesktopCapability.WindowState,
            (adapter, token) => adapter.SetStateAsync(state, token),
            cancellationToken);
    }

    public async ValueTask<IDisposable> RegisterAsync(
        DesktopKeyboardAccelerator accelerator,
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accelerator);
        ArgumentNullException.ThrowIfNull(callback);
        string id = Guid.NewGuid().ToString("N");
        string payload = $$$"""
            {"id":{{{Serialize(id)}}},"accelerator":{"key":{{{Serialize(accelerator.Key)}}},"control":{{{JsonBoolean(accelerator.Control)}}},"alternate":{{{JsonBoolean(accelerator.Alternate)}}},"shift":{{{JsonBoolean(accelerator.Shift)}}},"meta":{{{JsonBoolean(accelerator.Meta)}}}}}
            """;
        lock (_gate)
        {
            _accelerators.Add(id, callback);
        }

        try
        {
            _ = await InvokeBrowserOperationAsync(
                DesktopCapability.KeyboardAccelerators,
                "accelerator.register",
                payload,
                cancellationToken).ConfigureAwait(false);
            return new AcceleratorRegistration(this, id);
        }
        catch
        {
            lock (_gate)
            {
                _accelerators.Remove(id);
            }

            throw;
        }
    }

    public async ValueTask<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        string json = await InvokeBrowserOperationAsync(
            DesktopCapability.Clipboard,
            "clipboard.read",
            "{}",
            cancellationToken).ConfigureAwait(false);
        return ParseNullableString(json);
    }

    public async ValueTask WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = await InvokeBrowserOperationAsync(
            DesktopCapability.Clipboard,
            "clipboard.write",
            $$$"""{"text":{{{Serialize(text)}}}}""",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DesktopFile>> OpenAsync(
        DesktopOpenFileOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentNullException.ThrowIfNull(options.FileTypes);
        string accept = string.Join(
            ',',
            options.FileTypes.SelectMany(static fileType => fileType.Extensions));
        string json = await InvokeBrowserOperationAsync(
            DesktopCapability.FileDialogs,
            "files.open",
            $$$"""{"allowMultiple":{{{JsonBoolean(options.AllowMultiple)}}},"accept":{{{Serialize(accept)}}}}""",
            cancellationToken).ConfigureAwait(false);
        return ParseFiles(json);
    }

    public async ValueTask<bool> SaveAsync(
        DesktopSaveFileOptions options,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SuggestedFileName);
        if (content.Length > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                "Desktop save content cannot exceed 16 MiB.");
        }

        string json = await InvokeBrowserOperationAsync(
            DesktopCapability.FileDialogs,
            "files.save",
            $$$"""{"fileName":{{{Serialize(options.SuggestedFileName)}}},"mediaType":"application/octet-stream","content":{{{Serialize(Convert.ToBase64String(content.Span))}}}}""",
            cancellationToken).ConfigureAwait(false);
        return ParseBoolean(json);
    }

    public ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "External launch accepts only absolute HTTP or HTTPS URIs.",
                nameof(uri));
        }

        RequireSupported(DesktopCapability.ExternalUri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask ShowAsync(
        DesktopNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentNullException.ThrowIfNull(notification.Body);
        _ = await InvokeBrowserOperationAsync(
            DesktopCapability.Notifications,
            "notification.show",
            $$$"""{"title":{{{Serialize(notification.Title)}}},"body":{{{Serialize(notification.Body)}}},"tag":{{{SerializeNullable(notification.Tag)}}}}""",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string?> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageKey(key);
        string json = await InvokeBrowserOperationAsync(
            DesktopCapability.BrowserStorage,
            "storage.read",
            $$$"""{"key":{{{Serialize(key)}}}}""",
            cancellationToken).ConfigureAwait(false);
        return ParseNullableString(json);
    }

    public async ValueTask WriteAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageKey(key);
        ArgumentNullException.ThrowIfNull(value);
        _ = await InvokeBrowserOperationAsync(
            DesktopCapability.BrowserStorage,
            "storage.write",
            $$$"""{"key":{{{Serialize(key)}}},"value":{{{Serialize(value)}}}}""",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageKey(key);
        _ = await InvokeBrowserOperationAsync(
            DesktopCapability.BrowserStorage,
            "storage.remove",
            $$$"""{"key":{{{Serialize(key)}}}}""",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IDesktopOwnedWindow> OpenAsync(
        string id,
        string title,
        Uri entryPoint,
        DesktopSize size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(entryPoint);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSupported(DesktopCapability.MultipleWindows);

        var options = new BrowserWindowOptions(
            id,
            title,
            size.Width,
            size.Height,
            isResizable: true,
            _profile);
        IBrowserHost host = GetHost();
        lock (_gate)
        {
            if (!_ownedWindowIds.Add(options.WindowId))
            {
                throw new InvalidOperationException(
                    $"A desktop window named '{options.WindowId}' is already owned.");
            }
        }

        OwnedWindow? owned = null;
        try
        {
            IBrowserWindow window = await host.Dispatcher.InvokeAsync(
                async token =>
                {
                    IBrowserWindow created = await host
                        .CreateWindowAsync(options, token)
                        .ConfigureAwait(false);
                    try
                    {
                        await created.NavigateAsync(entryPoint, token).ConfigureAwait(false);
                        await created.ShowAsync(token).ConfigureAwait(false);
                        return created;
                    }
                    catch
                    {
                        await created.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
            owned = new OwnedWindow(this, host, options.WindowId, window);
            lock (_gate)
            {
                if (!ReferenceEquals(_host, host))
                {
                    throw new InvalidOperationException(
                        "The CsWebUi application stopped while opening a secondary window.");
                }

                _ownedWindows.Add(options.WindowId, owned);
            }

            owned.StartMonitoring();
            return owned;
        }
        catch
        {
            lock (_gate)
            {
                _ownedWindowIds.Remove(options.WindowId);
                if (owned is not null)
                {
                    _ownedWindows.Remove(options.WindowId);
                }
            }

            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private ValueTask InvokeAdapterAsync(
        DesktopCapability capability,
        Func<IBrowserWindowDesktopAdapter, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IBrowserHost host = GetHost();
        IBrowserWindowDesktopAdapter adapter = GetAdapter();
        RequireSupported(capability);
        return host.Dispatcher.InvokeAsync(
            token => operation(adapter, token),
            cancellationToken);
    }

    private ValueTask<string> InvokeBrowserOperationAsync(
        DesktopCapability capability,
        string operation,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IBrowserHost host = GetHost();
        IBrowserWindowDesktopAdapter adapter = GetAdapter();
        RequireCallable(capability);
        return host.Dispatcher.InvokeAsync(
            token => adapter.InvokeBrowserAsync(operation, payloadJson, token),
            cancellationToken);
    }

    private IBrowserHost GetHost()
    {
        lock (_gate)
        {
            return _host ??
                throw CapabilityFailure(DesktopCapability.UiDispatch);
        }
    }

    private IBrowserWindowDesktopAdapter GetAdapter()
    {
        lock (_gate)
        {
            return _adapter ??
                throw CapabilityFailure(DesktopCapability.WindowFocus);
        }
    }

    private void RequireSupported(DesktopCapability capability)
    {
        DesktopCapabilityDescriptor descriptor = Report[capability];
        if (!descriptor.IsSupported)
        {
            throw new DesktopCapabilityException(descriptor);
        }
    }

    private void RequireCallable(DesktopCapability capability)
    {
        DesktopCapabilityDescriptor descriptor = Report[capability];
        if (descriptor.Status is DesktopCapabilityStatus.Unsupported
            or DesktopCapabilityStatus.Unavailable)
        {
            throw new DesktopCapabilityException(descriptor);
        }
    }

    private DesktopCapabilityException CapabilityFailure(DesktopCapability capability) =>
        new(Report[capability]);

    private static void ValidateStorageKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                "A browser storage key cannot exceed 256 characters.");
        }
    }

    private void OnDesktopEvent(object? sender, BrowserDesktopEventArgs args)
    {
        if (args.Name == "accelerator")
        {
            Func<CancellationToken, ValueTask>? callback;
            lock (_gate)
            {
                _accelerators.TryGetValue(args.Id, out callback);
            }

            if (callback is not null)
            {
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await callback(_stopping.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                        {
                        }
                        catch (Exception)
                        {
                        }
                    });
            }

            return;
        }

        if (args.Name == "drop")
        {
            DesktopDrop drop;
            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                JsonElement root = document.RootElement;
                IReadOnlyList<DesktopFile> files = root.TryGetProperty(
                    "files",
                    out JsonElement fileElement)
                    ? ParseFiles(fileElement.GetRawText())
                    : [];
                string? text = root.TryGetProperty("text", out JsonElement textElement)
                    && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                drop = new DesktopDrop(files, text);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                return;
            }

            EventHandler<DesktopDrop>? handlers;
            lock (_gate)
            {
                handlers = _dropped;
            }

            if (handlers is null)
            {
                return;
            }

            foreach (EventHandler<DesktopDrop> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, drop);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private void RemoveAccelerator(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _accelerators.Remove(id);
        }

        if (removed && Volatile.Read(ref _disposed) == 0)
        {
            _ = ObserveAsync(InvokeBrowserOperationAsync(
                DesktopCapability.KeyboardAccelerators,
                "accelerator.remove",
                $$$"""{"id":{{{Serialize(id)}}}}""",
                CancellationToken.None).AsTask());
        }
    }

    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static ReadOnlyCollection<DesktopFile> ParseFiles(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The browser file dialog returned an invalid response.");
        }

        var files = new List<DesktopFile>();
        int totalBytes = 0;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            string name = element.GetProperty("name").GetString() ??
                throw new InvalidOperationException("A selected file has no name.");
            string mediaType = element.GetProperty("mediaType").GetString() ??
                "application/octet-stream";
            byte[] content = Convert.FromBase64String(
                element.GetProperty("content").GetString() ?? string.Empty);
            totalBytes = checked(totalBytes + content.Length);
            if (totalBytes > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "Selected files exceed the 16 MiB desktop bridge limit.");
            }

            files.Add(new DesktopFile(name, mediaType, content));
        }

        return files.AsReadOnly();
    }

    private static string? ParseNullableString(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => document.RootElement.GetString(),
            _ => throw new InvalidOperationException(
                "The browser desktop bridge returned an invalid string response."),
        };
    }

    private static bool ParseBoolean(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                "The browser desktop bridge returned an invalid Boolean response."),
        };
    }

    private static string Serialize(string value) =>
        JsonSerializer.Serialize(value, CsWebUiDesktopJsonContext.Default.String);

    private static string SerializeNullable(string? value) =>
        value is null ? "null" : Serialize(value);

    private static string JsonBoolean(bool value) => value ? "true" : "false";

    private static DesktopCapabilityReport CreateDetachedReport()
    {
        List<DesktopCapabilityDescriptor> capabilities = [];
        foreach (DesktopCapability capability in Enum.GetValues<DesktopCapability>())
        {
            capabilities.Add(new(
                capability,
                DesktopCapabilityStatus.Unavailable,
                "The CsWebUi native window is not active."));
        }

        return new DesktopCapabilityReport("cswebui", GetPlatform(), capabilities);
    }

    private static string GetPlatform() =>
        OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";

    private async ValueTask CancelStoppingAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            try
            {
                await _stopping.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // Cancellation observers are consumer code and cannot block native teardown.
            }
        }
    }

    private void RemoveCloseGuard(IDesktopCloseGuard guard)
    {
        lock (_gate)
        {
            _closeGuards.Remove(guard);
        }
    }

    private void RemoveOwnedWindow(string id, OwnedWindow ownedWindow)
    {
        lock (_gate)
        {
            if (_ownedWindows.TryGetValue(id, out OwnedWindow? current)
                && ReferenceEquals(current, ownedWindow))
            {
                _ownedWindows.Remove(id);
            }

            _ownedWindowIds.Remove(id);
        }
    }

    private sealed class CloseGuardRegistration(
        CsWebUiDesktopServices owner,
        IDesktopCloseGuard guard) : IDisposable
    {
        private CsWebUiDesktopServices? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveCloseGuard(guard);
    }

    private sealed class AcceleratorRegistration(
        CsWebUiDesktopServices owner,
        string id) : IDisposable
    {
        private CsWebUiDesktopServices? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveAccelerator(id);
    }

    private sealed class OwnedWindow(
        CsWebUiDesktopServices owner,
        IBrowserHost host,
        string id,
        IBrowserWindow window) : IDesktopOwnedWindow
    {
        private int _disposed;

        public string Id { get; } = id;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            DisposeCoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => DisposeCoreAsync(CancellationToken.None);

        internal void StartMonitoring() => _ = MonitorAsync();

        private async Task MonitorAsync()
        {
            try
            {
                await window.WaitForCloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            await DisposeCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async ValueTask DisposeCoreAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await host.Dispatcher.InvokeAsync(
                    window.CloseAsync,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await host.Dispatcher.InvokeAsync(
                        _ => window.DisposeAsync(),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    owner.RemoveOwnedWindow(Id, this);
                }
            }
        }
    }
}
