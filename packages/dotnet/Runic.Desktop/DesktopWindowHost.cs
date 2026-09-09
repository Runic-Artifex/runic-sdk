namespace Runic.Desktop;

/// <summary>Creates platform-native embedded WebView presentations.</summary>
public interface IDesktopWindowHostFactory
{
    /// <summary>Gets whether the required platform runtime is available.</summary>
    bool IsSupported { get; }

    /// <summary>Creates a new, initially closed host.</summary>
    IDesktopWindowHost Create();
}

/// <summary>An optional native host exposing its owning dispatcher to platform services.</summary>
public interface IDesktopNativeDispatchWindowHost : IDesktopWindowHost
{
    bool SupportsNativeDispatch { get; }
    bool CheckNativeAccess();
    ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken);
}

/// <summary>Hosts one Desktop surface in a platform-native window.</summary>
public interface IDesktopWindowHost : IAsyncDisposable
{
    /// <summary>Whether user close requests invoke the configured CloseRequested callback instead of closing.</summary>
    bool SupportsCloseConfirmation => false;

    event EventHandler? Closed;
    bool IsOpen { get; }
    nint NativeHandle { get; }
    ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default);
    ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
    ValueTask FocusAsync(CancellationToken cancellationToken = default);
    ValueTask MinimizeAsync(CancellationToken cancellationToken = default);
    ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default);
    ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default);
    ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);
    ValueTask BeginMoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Describes the immutable initial state of an embedded window host.</summary>
public sealed record DesktopWindowHostOptions
{
    /// <summary>When set, suppress user close requests and invoke this callback. CloseAsync must bypass it.</summary>
    public Action? CloseRequested { get; init; }

    public uint Width { get; init; } = 800;
    public uint Height { get; init; } = 600;
    public uint? MinimumWidth { get; init; }
    public uint? MinimumHeight { get; init; }
    public uint? X { get; init; }
    public uint? Y { get; init; }
    public bool Centered { get; init; }
    public bool Resizable { get; init; } = true;
    public bool Frameless { get; init; }
    public bool Transparent { get; init; }
    public bool Hidden { get; init; }
    public bool Kiosk { get; init; }
    public bool HighContrast { get; init; }
    public string? IconFile { get; init; }
    public string? ProfilePath { get; init; }
    public string? CustomArguments { get; init; }
    public DesktopPermissionGrant AllowedPermissions { get; init; }
}

internal sealed class DesktopWindowHostFactoryAdapter(IDesktopWindowHostFactory factory) : IWebUiEmbeddedHostFactory
{
    public bool IsSupported => factory.IsSupported;

    public IWebUiEmbeddedHost Create() => new DesktopWindowHostAdapter(factory.Create());
}

internal sealed class DesktopWindowHostAdapter : IWebUiEmbeddedHost
{
    private readonly IDesktopWindowHost _host;

    internal DesktopWindowHostAdapter(IDesktopWindowHost host)
    {
        _host = host;
        _host.Closed += OnClosed;
    }

    public event EventHandler? Closed;

    public bool IsOpen => _host.IsOpen;

    public bool SupportsCloseConfirmation => _host.SupportsCloseConfirmation;
    public bool SupportsNativeDispatch => _host is IDesktopNativeDispatchWindowHost { SupportsNativeDispatch: true };
    public bool CheckNativeAccess() => _host is IDesktopNativeDispatchWindowHost native && native.CheckNativeAccess();
    public ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken) =>
        _host is IDesktopNativeDispatchWindowHost native
            ? native.DispatchNativeAsync(action, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("This host does not expose native dispatch."));

    public nint NativeHandle => _host.NativeHandle;

    public ValueTask ShowAsync(
        Uri url,
        WebUiEmbeddedHostOptions options,
        CancellationToken cancellationToken = default) =>
        _host.OpenAsync(url, new DesktopWindowHostOptions
        {
            CloseRequested = options.CloseRequested,
            Width = options.Width,
            Height = options.Height,
            MinimumWidth = options.MinimumWidth,
            MinimumHeight = options.MinimumHeight,
            X = options.X,
            Y = options.Y,
            Centered = options.Centered,
            Resizable = options.Resizable,
            Frameless = options.Frameless,
            Transparent = options.Transparent,
            Hidden = options.Hidden,
            Kiosk = options.Kiosk,
            HighContrast = options.HighContrast,
            IconFile = options.IconFile,
            ProfilePath = options.ProfilePath,
            CustomArguments = options.CustomParameters,
            AllowedPermissions = options.AllowedPermissions,
        }, cancellationToken);

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        _host.NavigateAsync(url, cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
        _host.CloseAsync(cancellationToken);

    public ValueTask FocusAsync(CancellationToken cancellationToken = default) =>
        _host.FocusAsync(cancellationToken);

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) =>
        _host.MinimizeAsync(cancellationToken);

    public ValueTask MaximizeAsync(CancellationToken cancellationToken = default) =>
        _host.ToggleMaximizedAsync(cancellationToken);

    public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
        _host.ResizeAsync(width, height, cancellationToken);

    public ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default) =>
        _host.MoveAsync(x, y, cancellationToken);

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        _host.SetVisibleAsync(visible, cancellationToken);

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) =>
        _host.BeginMoveAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _host.Closed -= OnClosed;
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private void OnClosed(object? sender, EventArgs eventArgs) => Closed?.Invoke(this, eventArgs);
}
