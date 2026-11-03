using System.ComponentModel;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// IRunicWindowPresentation of a Runic Window on Runic Desktop: its DesktopWindow, embedded or an installed browser after
// a fallback. RunicDesktopHost gives it to the Window once the window opened (RunicWindowOpening.UsePresentation); the
// Window raises its changes in its model turns.
internal sealed class DesktopWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    internal static readonly PlatformDiagnostic KioskStaysFullScreen = new("Runic.Desktop", 0, "A kiosk window stays full-screen.");

    private readonly DesktopWindow _window;
    private readonly Func<DesktopNativeOwner?> _owner;
    private readonly object _gate = new();
    private RunicWindowActivity _activity;

    internal DesktopWindowPresentation(DesktopWindow window, Func<DesktopNativeOwner?> owner)
    {
        _window = window;
        _owner = owner;
        _activity = Convert(window.Activity);
        window.ActivityChanged += OnActivityChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    // DesktopPlatformServices.PresentAsync, tests replace it.
    internal Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> Present
    { get; init; } = DesktopPlatformServices.PresentAsync;

    public RunicWindowPresentationFeatures Features => FeaturesOf(_window.Capabilities, _window.IsOpen, OperatingSystem.IsMacOS());

    internal static RunicWindowPresentationFeatures FeaturesOf(DesktopWindowCapabilities capabilities, bool open, bool macOS)
    {
        if (!open) return RunicWindowPresentationFeatures.None;
        var features = RunicWindowPresentationFeatures.None;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Focus)) features |= RunicWindowPresentationFeatures.Present;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Minimize)) features |= RunicWindowPresentationFeatures.Minimize;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Visibility)) features |= RunicWindowPresentationFeatures.Visibility;
        if (capabilities.HasFlag(DesktopWindowCapabilities.FullScreen)) features |= RunicWindowPresentationFeatures.FullScreen;
        if (capabilities.HasFlag(DesktopWindowCapabilities.ColorScheme)) features |= RunicWindowPresentationFeatures.ColorScheme;
        if (capabilities.HasFlag(DesktopWindowCapabilities.Activity)) features |= RunicWindowPresentationFeatures.Activity;
        // NSWindow.documentEdited: only the macOS window shows it; elsewhere DesktopWindow accepts it and does nothing.
        if (macOS && capabilities.HasFlag(DesktopWindowCapabilities.NativeHandle)) features |= RunicWindowPresentationFeatures.DocumentEdited;
        return features;
    }

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme => _window.ColorScheme;

    // A window that reports no activity counts as active and visible while it is open.
    internal static RunicWindowActivity Convert(DesktopWindowActivity? activity) => activity is null
        ? RunicWindowActivity.Assumed
        : new RunicWindowActivity(activity.IsActive, activity.IsMinimized, activity.IsVisible);

    private void OnActivityChanged(object? sender, DesktopWindowActivityChangedEventArgs args)
    {
        var activity = Convert(args.Activity);
        RunicWindowActivity previous;
        lock (_gate)
        {
            previous = _activity;
            if (Equals(previous, activity)) return;
            Volatile.Write(ref _activity, activity);
        }
        // DesktopWindow raises its events one at a time, so the changes keep their order.
        ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
        if (!_window.IsOpen) _window.ActivityChanged -= OnActivityChanged;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_window.IsOpen) return Closed;
        if (!Features.HasFlag(RunicWindowPresentationFeatures.Present)) return Unsupported;
        // The platform's presentation shows a hidden window, restores a minimized one, asks for the foreground with the
        // desktop's rules and passes the activation token. Without a configured provider, or for a custom window host
        // without native dispatch, the window's own focus shows and activates it.
        if (_owner() is { IsAvailable: true } owner)
        {
            var presented = await Present(owner, activation, cancellationToken).ConfigureAwait(false);
            if (presented is not PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured })
                return presented;
        }
        return await RunAsync(() => _window.FocusAsync(cancellationToken)).ConfigureAwait(false);
    }

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Minimize, () => _window.MinimizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.Visibility, () => _window.SetVisibleAsync(visible, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.FullScreen, () => _window.SetFullScreenAsync(fullScreen, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Request(RunicWindowPresentationFeatures.DocumentEdited, () => _window.SetDocumentEditedAsync(edited, cancellationToken), cancellationToken);

    public async ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        var previous = _window.ColorScheme;
        var result = await Request(RunicWindowPresentationFeatures.ColorScheme,
            () => _window.SetColorSchemeAsync(colorScheme, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (result is PlatformResult<PlatformUnit>.Success && previous != _window.ColorScheme)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorScheme)));
        return result;
    }

    private ValueTask<PlatformResult<PlatformUnit>> Request(RunicWindowPresentationFeatures feature, Func<ValueTask> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_window.IsOpen) return ValueTask.FromResult(Closed);
        return Features.HasFlag(feature) ? RunAsync(request) : ValueTask.FromResult(Unsupported);
    }

    // DesktopWindow throws where the presentation reports results: a window closed meanwhile, a capability it lost.
    private async ValueTask<PlatformResult<PlatformUnit>> RunAsync(Func<ValueTask> request)
    {
        try
        {
            await request().ConfigureAwait(false);
            return Done;
        }
        catch (ObjectDisposedException) { return Closed; }
        catch (DesktopException error) when (error.Code == "window-capability-unavailable") { return Unsupported; }
        catch (InvalidOperationException) when (!_window.IsOpen) { return Closed; }
        catch (InvalidOperationException) when (_window.State?.IsFullScreen == true)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict, KioskStaysFullScreen);
        }
        catch (NotSupportedException) { return Unsupported; }
    }
}
