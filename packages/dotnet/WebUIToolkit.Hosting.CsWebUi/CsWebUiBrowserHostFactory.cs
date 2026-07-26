using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi;

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

internal sealed class CsWebUiBrowserWindow : IBrowserWindow
{
    private readonly string _applicationId;
    private readonly BrowserWindowOptions _windowOptions;
    private readonly CsWebUiAdapterOptions _adapterOptions;
    private readonly ICsWebUiRuntime _runtime;
    private readonly ICsWebUiWindow _nativeWindow;
    private readonly Action<CsWebUiBrowserWindow> _onDisposed;
    private readonly IDisposable _eventsBinding;
    private readonly TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
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
    }

    public event EventHandler? CloseRequested;

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
        SignalClosed(raiseEvent: false);
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
