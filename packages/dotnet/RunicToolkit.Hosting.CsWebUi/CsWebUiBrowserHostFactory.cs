using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.Desktop;

namespace RunicToolkit.Hosting.CsWebUi;

/// <summary>Creates browser hosts backed by CsWebUi.</summary>
public sealed class CsWebUiBrowserHostFactory : IBrowserHostFactory
{
    private readonly CsWebUiAdapterOptions _options;
    private readonly ICsWebUiRuntime _runtime;

    /// <summary>Initializes a CsWebUi browser-host factory.</summary>
    public CsWebUiBrowserHostFactory(CsWebUiAdapterOptions options)
        : this(options, NativeCsWebUiRuntime.Instance)
    {
    }

    internal CsWebUiBrowserHostFactory(
        CsWebUiAdapterOptions options,
        ICsWebUiRuntime runtime)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    public ValueTask<IBrowserHost> CreateAsync(
        BrowserHostOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IBrowserHost>(
            new CsWebUiBrowserHost(options.ApplicationId, _options, _runtime));
    }
}

internal sealed class CsWebUiBrowserHost : IBrowserHost
{
    private readonly string _applicationId;
    private readonly CsWebUiAdapterOptions _options;
    private readonly ICsWebUiRuntime _runtime;
    private readonly List<CsWebUiBrowserWindow> _windows = [];
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;

    internal CsWebUiBrowserHost(
        string applicationId,
        CsWebUiAdapterOptions options,
        ICsWebUiRuntime runtime)
    {
        _applicationId = applicationId;
        _options = options;
        _runtime = runtime;
        Dispatcher = new CsWebUiDispatcher();
    }

    public IUiDispatcher Dispatcher { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Directory.Exists(_options.WebRoot))
            {
                throw new DirectoryNotFoundException(
                    "The configured CsWebUi web root does not exist.");
            }

            _initialized = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IBrowserWindow> CreateWindowAsync(
        BrowserWindowOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "The CsWebUi browser host must be initialized before creating a window.");
            }
        }

        ICsWebUiWindow nativeWindow = _runtime.CreateWindow(_options.ConfigureWindow);
        try
        {
            nativeWindow.SetRootFolder(_options.WebRoot);
            nativeWindow.SetSize(checked((uint)options.Width), checked((uint)options.Height));
            nativeWindow.SetResizable(options.IsResizable);
            nativeWindow.SetPublic(false);
            if (options.BrowserProfile is DesktopBrowserProfile profile)
            {
                Directory.CreateDirectory(profile.StoragePath);
                nativeWindow.SetProfile(profile.Name, profile.StoragePath);
            }

            var window = new CsWebUiBrowserWindow(
                _applicationId,
                options,
                _options,
                _runtime,
                nativeWindow,
                RemoveWindow);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _windows.Add(window);
            }

            return ValueTask.FromResult<IBrowserWindow>(window);
        }
        catch
        {
            nativeWindow.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CsWebUiBrowserWindow[] windows;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            windows = _windows.ToArray();
            _windows.Clear();
        }

        foreach (CsWebUiBrowserWindow window in windows)
        {
            await Dispatcher.InvokeAsync(
                _ => window.DisposeAsync(),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void RemoveWindow(CsWebUiBrowserWindow window)
    {
        lock (_gate)
        {
            _windows.Remove(window);
        }
    }
}

internal sealed class CsWebUiBrowserWindow : IBrowserWindow, IBrowserWindowDesktopAdapter
{
    private const int MaximumDesktopMessageCharacters = 24 * 1024 * 1024;
    private readonly string _applicationId;
    private readonly BrowserWindowOptions _windowOptions;
    private readonly CsWebUiAdapterOptions _adapterOptions;
    private readonly ICsWebUiRuntime _runtime;
    private readonly ICsWebUiWindow _nativeWindow;
    private readonly Action<CsWebUiBrowserWindow> _onDisposed;
    private readonly IDisposable _eventsBinding;
    private readonly IDisposable _desktopBinding;
    private readonly TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _desktopRequests = [];
    private string? _entryPath;
    private bool _shown;
    private bool _closedSignaled;
    private bool _disposed;

    internal CsWebUiBrowserWindow(
        string applicationId,
        BrowserWindowOptions windowOptions,
        CsWebUiAdapterOptions adapterOptions,
        ICsWebUiRuntime runtime,
        ICsWebUiWindow nativeWindow,
        Action<CsWebUiBrowserWindow> onDisposed)
    {
        _applicationId = applicationId;
        _windowOptions = windowOptions;
        _adapterOptions = adapterOptions;
        _runtime = runtime;
        _nativeWindow = nativeWindow;
        _onDisposed = onDisposed;
        _eventsBinding = nativeWindow.BindEvents(OnNativeEvent);
        _desktopBinding = nativeWindow.BindDesktopMessages(OnDesktopMessage);
        Capabilities = CreateCapabilityReport(windowOptions);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler<BrowserDesktopEventArgs>? DesktopEventReceived;

    public DesktopCapabilityReport Capabilities { get; }

    public ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string relativePath = CsWebUiEntryPointPath.Translate(entryPoint, _applicationId);
        bool navigate;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closedSignaled)
            {
                throw new InvalidOperationException("The CsWebUi window is closed.");
            }

            _entryPath = relativePath;
            navigate = _shown;
        }

        if (navigate)
        {
            _nativeWindow.Navigate(relativePath);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string entryPath;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closedSignaled)
            {
                throw new InvalidOperationException("The CsWebUi window is closed.");
            }

            if (_shown)
            {
                return ValueTask.CompletedTask;
            }

            entryPath = _entryPath ??
                throw new InvalidOperationException(
                    "NavigateAsync must supply an entry point before the CsWebUi window is shown.");
            _shown = true;
        }

        try
        {
            _nativeWindow.Show(
                entryPath,
                _adapterOptions.PresentationMode,
                _adapterOptions.Browser);
            _nativeWindow.SetTitle(_windowOptions.Title);
            _nativeWindow.RunJavaScript(CsWebUiDesktopBridgeScript.Bootstrap);
            _ = ObserveApplicationExitAsync();
            return ValueTask.CompletedTask;
        }
        catch
        {
            lock (_gate)
            {
                _shown = false;
            }

            throw;
        }
    }

    public Task WaitForCloseAsync(CancellationToken cancellationToken) =>
        _closed.Task.WaitAsync(cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool closeNative;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            closeNative = !_closedSignaled;
        }

        if (closeNative)
        {
            _nativeWindow.Close();
            SignalClosed(raiseEvent: false);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FocusWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        _nativeWindow.Focus();
        return ValueTask.CompletedTask;
    }

    public ValueTask FocusElementAsync(string elementId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        string serializedId = JsonSerializer.Serialize(
            elementId,
            CsWebUiJsonContext.Default.String);
        _nativeWindow.RunJavaScript(
            $"document.getElementById({serializedId})?.focus({{preventScroll:false}});");
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSizeAsync(DesktopSize size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        _nativeWindow.SetSize(checked((uint)size.Width), checked((uint)size.Height));
        return ValueTask.CompletedTask;
    }

    public ValueTask SetPositionAsync(DesktopPosition position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        _nativeWindow.SetPosition(checked((uint)position.X), checked((uint)position.Y));
        return ValueTask.CompletedTask;
    }

    public ValueTask CenterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        _nativeWindow.Center();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetStateAsync(
        DesktopWindowState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        switch (state)
        {
            case DesktopWindowState.Minimized:
                _nativeWindow.Minimize();
                break;
            case DesktopWindowState.Maximized:
                _nativeWindow.Maximize();
                break;
            case DesktopWindowState.Normal:
                _nativeWindow.SetSize(
                    checked((uint)_windowOptions.Width),
                    checked((uint)_windowOptions.Height));
                _nativeWindow.Focus();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<string> InvokeBrowserAsync(
        string operation,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (operation.Length > 64
            || operation.IndexOfAnyExcept(
                "abcdefghijklmnopqrstuvwxyz.-".AsSpan()) >= 0)
        {
            throw new ArgumentException(
                "A desktop bridge operation must use lowercase ASCII letters, periods, or hyphens.",
                nameof(operation));
        }

        if (payloadJson.Length > MaximumDesktopMessageCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadJson),
                "A desktop bridge payload exceeds the 24 MiB character limit.");
        }

        using (JsonDocument payload = JsonDocument.Parse(payloadJson))
        {
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "A desktop bridge payload must be a JSON object.",
                    nameof(payloadJson));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_shown || _closedSignaled)
            {
                throw new InvalidOperationException(
                    "The CsWebUi browser bridge requires a shown, connected window.");
            }

            _desktopRequests.Add(id, completion);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using CancellationTokenRegistration registration = timeout.Token.Register(
            static state =>
            {
                var request = (DesktopCancellation)state!;
                request.Owner.CancelDesktopRequest(request.Id, request.Token);
            },
            new DesktopCancellation(this, id, timeout.Token));
        try
        {
            string serializedId = JsonSerializer.Serialize(id, CsWebUiJsonContext.Default.String);
            string serializedOperation = JsonSerializer.Serialize(
                operation,
                CsWebUiJsonContext.Default.String);
            _nativeWindow.RunJavaScript(
                $"globalThis.__runicToolkitDesktop.invoke({serializedId},{serializedOperation},{payloadJson});");
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _desktopRequests.Remove(id);
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        TaskCompletionSource<string>[] desktopRequests;
        lock (_gate)
        {
            desktopRequests = _desktopRequests.Values.ToArray();
            _desktopRequests.Clear();
        }

        foreach (TaskCompletionSource<string> request in desktopRequests)
        {
            request.TrySetException(
                new ObjectDisposedException(nameof(CsWebUiBrowserWindow)));
        }

        SignalClosed(raiseEvent: false);
        _desktopBinding.Dispose();
        _eventsBinding.Dispose();
        _nativeWindow.Dispose();
        _lifetime.Dispose();
        _onDisposed(this);
        return ValueTask.CompletedTask;
    }

    private void OnNativeEvent(WebUiEventType eventType)
    {
        if (eventType == WebUiEventType.Disconnected)
        {
            SignalClosed(raiseEvent: true);
        }
    }

    private void OnDesktopMessage(string message)
    {
        if (message.Length > MaximumDesktopMessageCharacters)
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            string? kind = root.TryGetProperty("kind", out JsonElement kindElement)
                ? kindElement.GetString()
                : null;
            string? id = root.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString()
                : null;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (kind == "result")
            {
                CompleteDesktopRequest(id, root);
                return;
            }

            if (kind == "event"
                && root.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.GetString() is string name
                && root.TryGetProperty("payload", out JsonElement payload))
            {
                EventHandler<BrowserDesktopEventArgs>? handlers = DesktopEventReceived;
                if (handlers is null)
                {
                    return;
                }

                var args = new BrowserDesktopEventArgs(name, id, payload.GetRawText());
                foreach (EventHandler<BrowserDesktopEventArgs> handler
                         in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private void CompleteDesktopRequest(string id, JsonElement response)
    {
        TaskCompletionSource<string>? completion;
        lock (_gate)
        {
            if (!_desktopRequests.Remove(id, out completion))
            {
                return;
            }
        }

        bool ok = response.TryGetProperty("ok", out JsonElement okElement)
            && okElement.ValueKind is JsonValueKind.True;
        if (ok)
        {
            string value = response.TryGetProperty("value", out JsonElement valueElement)
                ? valueElement.GetRawText()
                : "null";
            completion.TrySetResult(value);
        }
        else
        {
            string error = response.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() ?? "Browser desktop operation failed."
                : "Browser desktop operation failed.";
            completion.TrySetException(new InvalidOperationException(error));
        }
    }

    private void CancelDesktopRequest(string id, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string>? completion;
        lock (_gate)
        {
            if (!_desktopRequests.Remove(id, out completion))
            {
                return;
            }
        }

        completion.TrySetCanceled(cancellationToken);
    }

    private sealed record DesktopCancellation(
        CsWebUiBrowserWindow Owner,
        string Id,
        CancellationToken Token);

    private void ThrowIfClosed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closedSignaled)
            {
                throw new InvalidOperationException("The CsWebUi window is closed.");
            }
        }
    }

    private static DesktopCapabilityReport CreateCapabilityReport(
        BrowserWindowOptions options)
    {
        string platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";
        var supported = DesktopCapabilityStatus.Supported;
        var permission = DesktopCapabilityStatus.PermissionRequired;
        return new DesktopCapabilityReport(
            "cswebui",
            platform,
            [
                new(DesktopCapability.UiDispatch, supported),
                new(DesktopCapability.WindowFocus, supported),
                new(DesktopCapability.ElementFocus, supported),
                new(DesktopCapability.WindowPlacement, supported),
                new(DesktopCapability.WindowState, supported),
                new(
                    DesktopCapability.KeyboardAccelerators,
                    supported),
                new(
                    DesktopCapability.Clipboard,
                    permission,
                    "Browser clipboard access requires document permission."),
                new(DesktopCapability.FileDialogs, supported),
                new(DesktopCapability.DragAndDrop, supported),
                new(DesktopCapability.ExternalUri, supported),
                new(
                    DesktopCapability.Notifications,
                    permission,
                    "Browser notifications require document permission."),
                new(DesktopCapability.BrowserProfile, supported),
                new(DesktopCapability.BrowserStorage, supported),
                new(DesktopCapability.MultipleWindows, supported),
            ]);
    }

    private async Task ObserveApplicationExitAsync()
    {
        try
        {
            await _runtime.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            SignalClosed(raiseEvent: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void SignalClosed(bool raiseEvent)
    {
        EventHandler? handler = null;
        lock (_gate)
        {
            if (_closedSignaled)
            {
                return;
            }

            _closedSignaled = true;
            if (raiseEvent)
            {
                handler = CloseRequested;
            }
        }

        _closed.TrySetResult();
        if (handler is not null)
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A consumer event handler cannot escape CsWebUi's native callback boundary.
            }
        }
    }
}
