using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Views;

/// <summary>
/// The Window whose DI scope resolved the service, as it appears on the desktop: a ViewModel brings it to the front,
/// minimizes, hides or shows it, makes it full-screen, marks its document edited, switches it between light and dark,
/// and reads whether it is active, minimized and visible. Take it in a constructor:
/// <code>
/// public sealed partial class InboxViewModel : ObservableObject
/// {
///     public InboxViewModel(IRunicWindowPresentation window)
///     {
///         _window = window;
///         // Poll less while the user works in another window.
///         window.ActivityChanged += (_, e) =&gt; _poll.Interval = e.Activity.IsActive ? Fast : Slow;
///     }
///
///     [RelayCommand]
///     private async Task HideToTrayAsync() =&gt; await _window.SetVisibleAsync(false);
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope, next to
/// <see cref="IRunicWindowContext"/>. It works on every host without naming one: Runic Desktop's embedded windows
/// (WebView2, WKWebView, WebKitGTK with GTK 3 or GTK 4), a WPF window through
/// <c>WpfPlatformServices.ForWindowPresentation</c>, and CS-WebUI as far as it can. <see cref="Features"/> says what
/// the host can do; a request it cannot do returns <see cref="PlatformResult{T}.Unavailable"/> with
/// <see cref="PlatformUnavailableReason.BackendUnavailable"/> instead of throwing. Before the Window opens, and in a
/// scope that no Window opened, requests report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>; once it has
/// closed, <see cref="PlatformUnavailableReason.OwnerClosed"/>. Code outside the scope reads the same object from
/// <see cref="IRunicWindow.Presentation"/>.
/// </para>
/// <para>
/// The state changes (<see cref="ActivityChanged"/> and <see cref="INotifyPropertyChanged.PropertyChanged"/> for
/// <see cref="Activity"/>, <see cref="IsActive"/>, <see cref="IsMinimized"/>, <see cref="IsVisible"/> and
/// <see cref="ColorScheme"/>) are raised in a turn of the Window's model context, as a command runs, so a handler sets
/// ViewModel state directly. With CommunityToolkit, subscribe to the event; with ReactiveUI, use
/// <c>WhenAnyValue(x =&gt; x.IsActive)</c> on the service, or <see cref="RunicWindowPresentationExtensions.ObserveActivity"/>.
/// Where the host reports no activity (<see cref="Features"/> without <see cref="RunicWindowPresentationFeatures.Activity"/>),
/// the Window counts as active and visible (<see cref="RunicWindowActivity.Assumed"/>), so work that pauses while it is
/// inactive keeps running.
/// </para>
/// <para>
/// This is not a ViewModel activation hook (the dropped I-142): it reports the native window, not whether a View
/// shows the ViewModel. In tests, use <c>RunicTestWindowPresentation</c> from Runic.Application.Testing, which records
/// every request and lets the test set the activity.
/// </para>
/// </remarks>
public interface IRunicWindowPresentation : INotifyPropertyChanged
{
    /// <summary>Gets what the Window's host can do now; <see cref="RunicWindowPresentationFeatures.None"/> before it opens and after it closed.</summary>
    RunicWindowPresentationFeatures Features { get; }

    /// <summary>Gets whether the Window is active, minimized and visible, as last reported.</summary>
    RunicWindowActivity Activity { get; }

    /// <summary>Gets whether the Window has the desktop's keyboard focus: <see cref="RunicWindowActivity.IsActive"/> of <see cref="Activity"/>.</summary>
    bool IsActive { get; }

    /// <summary>Gets whether the Window is minimized: <see cref="RunicWindowActivity.IsMinimized"/> of <see cref="Activity"/>.</summary>
    bool IsMinimized { get; }

    /// <summary>Gets whether the Window is shown: <see cref="RunicWindowActivity.IsVisible"/> of <see cref="Activity"/>.</summary>
    bool IsVisible { get; }

    /// <summary>
    /// Gets the Window's own light or dark appearance, from the last <see cref="SetColorSchemeAsync"/> or the host's
    /// window options; <see cref="DesktopColorScheme.NoPreference"/> follows the application's, and that the system's.
    /// </summary>
    DesktopColorScheme ColorScheme { get; }

    /// <summary>Occurs after <see cref="Activity"/> has changed, in a turn of the Window's model context.</summary>
    event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    /// <summary>
    /// Brings the Window to the front and gives it the keyboard focus: shows it when it is hidden and restores it when
    /// it is minimized.
    /// </summary>
    /// <param name="activation">
    /// The activation token or startup ID that lets the desktop grant focus, such as <c>ApplicationLaunch.PlatformContext</c>
    /// of a launch that a second instance forwarded, or <see langword="null"/>. Use it once, promptly.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> when the desktop presented the Window. A desktop may refuse to take the
    /// focus from the application the user works in: Windows then flashes the taskbar button and reports
    /// <see cref="PlatformFailureCode.PermissionDenied"/>, and a Wayland compositor without an activation token only asks
    /// for attention.
    /// </returns>
    ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default);

    /// <summary>Minimizes the Window to the taskbar or Dock.</summary>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was asked to minimize.</returns>
    ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Hides the Window without closing it, for example to a status icon, or shows it again.</summary>
    /// <param name="visible"><see langword="true"/> to show the Window; <see langword="false"/> to hide it.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was shown or hidden.</returns>
    /// <remarks>
    /// A hidden Window stays open: its page keeps running and its Bridge stays connected. Only the application can show
    /// it again, so keep a way back, such as a status icon whose command calls <see cref="PresentAsync"/>.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Enters or leaves full screen.</summary>
    /// <param name="fullScreen"><see langword="true"/> to fill the Window's monitor; <see langword="false"/> to return to its bounds.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window was asked to change.</returns>
    ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default);

    /// <summary>Marks whether the Window's document has unsaved changes, where the platform shows it: the dot in the macOS close button.</summary>
    /// <param name="edited"><see langword="true"/> while the document has unsaved changes.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window shows it; <see cref="PlatformUnavailableReason.BackendUnavailable"/>
    /// on Windows, Linux and in WPF, which have no such convention. Show unsaved changes in the page or title there.
    /// </returns>
    ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default);

    /// <summary>Switches the Window between light and dark while it is open.</summary>
    /// <param name="colorScheme">
    /// <see cref="DesktopColorScheme.Light"/> or <see cref="DesktopColorScheme.Dark"/>, or
    /// <see cref="DesktopColorScheme.NoPreference"/> to follow the application's scheme, which by default follows the system.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns><see cref="PlatformResult{T}.Success"/> once the window and its page show the scheme.</returns>
    /// <remarks>
    /// The page's <c>prefers-color-scheme</c> follows. On Linux the GTK windows share one scheme, so the call changes every
    /// window of the application; on Runic Desktop, <c>DesktopHost.SetColorSchemeAsync</c> switches the whole application.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorScheme"/> is not a defined value.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default);
}

/// <summary>What a Window's host can do through <see cref="IRunicWindowPresentation"/>.</summary>
/// <remarks>Open: a later minor release can add values, so test for the flags you use.</remarks>
[Flags]
public enum RunicWindowPresentationFeatures
{
    /// <summary>Nothing: the Window has not opened, has closed, or its host presents no native window.</summary>
    None = 0,

    /// <summary><see cref="IRunicWindowPresentation.PresentAsync"/> brings the Window to the front.</summary>
    Present = 1 << 0,

    /// <summary><see cref="IRunicWindowPresentation.MinimizeAsync"/> minimizes the Window.</summary>
    Minimize = 1 << 1,

    /// <summary><see cref="IRunicWindowPresentation.SetVisibleAsync"/> hides and shows the Window.</summary>
    Visibility = 1 << 2,

    /// <summary><see cref="IRunicWindowPresentation.SetFullScreenAsync"/> enters and leaves full screen.</summary>
    FullScreen = 1 << 3,

    /// <summary><see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/> shows unsaved changes in the window's frame.</summary>
    DocumentEdited = 1 << 4,

    /// <summary><see cref="IRunicWindowPresentation.SetColorSchemeAsync"/> switches the Window between light and dark.</summary>
    ColorScheme = 1 << 5,

    /// <summary>
    /// <see cref="IRunicWindowPresentation.Activity"/> follows the window. Without it the Window counts as active and
    /// visible (<see cref="RunicWindowActivity.Assumed"/>).
    /// </summary>
    Activity = 1 << 6,
}

/// <summary>Whether a Window is active, minimized and visible, for <see cref="IRunicWindowPresentation.Activity"/>.</summary>
/// <param name="IsActive">Whether the Window has the desktop's keyboard focus.</param>
/// <param name="IsMinimized">Whether the Window is minimized.</param>
/// <param name="IsVisible">Whether the Window is shown: not hidden and not minimized, though other windows may cover it.</param>
public sealed record RunicWindowActivity(bool IsActive, bool IsMinimized, bool IsVisible)
{
    /// <summary>Active and visible: what a Window reports when its host cannot tell, and before it opens.</summary>
    public static RunicWindowActivity Assumed { get; } = new(IsActive: true, IsMinimized: false, IsVisible: true);

    /// <summary>Neither active, minimized nor visible: what a closed Window reports.</summary>
    public static RunicWindowActivity Closed { get; } = new(IsActive: false, IsMinimized: false, IsVisible: false);
}

/// <summary>Provides the previous and new activity for <see cref="IRunicWindowPresentation.ActivityChanged"/>.</summary>
/// <param name="previous">The activity before the change.</param>
/// <param name="activity">The new activity.</param>
public sealed class RunicWindowActivityChangedEventArgs(RunicWindowActivity previous, RunicWindowActivity activity) : EventArgs
{
    /// <summary>Gets the activity before the change.</summary>
    public RunicWindowActivity Previous { get; } = previous ?? throw new ArgumentNullException(nameof(previous));

    /// <summary>Gets the new activity.</summary>
    public RunicWindowActivity Activity { get; } = activity ?? throw new ArgumentNullException(nameof(activity));
}

/// <summary>Observes a Window's activity without an MVVM library.</summary>
public static class RunicWindowPresentationExtensions
{
    /// <summary>
    /// Returns the Window's activity as an observable sequence: the current value when subscribed, then each change, in
    /// the turns that raise <see cref="IRunicWindowPresentation.ActivityChanged"/>.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="IObservable{T}"/>, so ReactiveUI composes it with either flavor, for example to pause a timer:
    /// <code>
    /// window.ObserveActivity().Select(activity =&gt; activity.IsActive).DistinctUntilChanged().Subscribe(...)
    /// </code>
    /// Disposing the subscription removes its handler. The sequence never completes; it ends with the Window's scope.
    /// </remarks>
    /// <param name="presentation">The Window's presentation.</param>
    public static IObservable<RunicWindowActivity> ObserveActivity(this IRunicWindowPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return new ActivityObservable(presentation);
    }

    private sealed class ActivityObservable(IRunicWindowPresentation presentation) : IObservable<RunicWindowActivity>
    {
        public IDisposable Subscribe(IObserver<RunicWindowActivity> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            void OnChanged(object? sender, RunicWindowActivityChangedEventArgs args) => observer.OnNext(args.Activity);
            presentation.ActivityChanged += OnChanged;
            observer.OnNext(presentation.Activity);
            return new Subscription(() => presentation.ActivityChanged -= OnChanged);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

// The presentation of one Window, which its scope and IRunicWindow.Presentation share. The host attaches its own
// presentation once the native window exists (RunicWindowOpening.UsePresentation); this one forwards requests to it,
// and raises its changes in the Window's model turns.
internal sealed class RunicWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> NotOpen =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private static readonly PlatformResult<PlatformUnit> Closed =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    private readonly object _gate = new();
    private IRunicWindowPresentation? _backend;
    private Func<IRunicModelContext?>? _context;
    private Func<ILogger>? _logger;
    private RunicWindowActivity _activity = RunicWindowActivity.Assumed;
    private DesktopColorScheme _colorScheme;
    private bool _closed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    public RunicWindowPresentationFeatures Features =>
        Volatile.Read(ref _closed) ? RunicWindowPresentationFeatures.None : Volatile.Read(ref _backend)?.Features ?? RunicWindowPresentationFeatures.None;

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme => Volatile.Read(ref _backend)?.ColorScheme ?? _colorScheme;

    // Called by the host once, while it opens the Window. context and logger are read when a change arrives.
    internal void Attach(IRunicWindowPresentation backend, Func<IRunicModelContext?> context, Func<ILogger> logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (_gate)
        {
            if (_backend is not null) throw new InvalidOperationException("The Window already has a presentation.");
            if (_closed) return;
            _context = context;
            _logger = logger;
            Volatile.Write(ref _backend, backend);
            backend.ActivityChanged += OnBackendActivityChanged;
            backend.PropertyChanged += OnBackendPropertyChanged;
        }
        Deliver(backend.Features.HasFlag(RunicWindowPresentationFeatures.Activity) ? backend.Activity : RunicWindowActivity.Assumed);
    }

    // The Window was released: requests report OwnerClosed, and the activity is Closed.
    internal void Close()
    {
        IRunicWindowPresentation? backend;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            backend = _backend;
        }
        if (backend is not null)
        {
            backend.ActivityChanged -= OnBackendActivityChanged;
            backend.PropertyChanged -= OnBackendPropertyChanged;
            Deliver(RunicWindowActivity.Closed);
        }
    }

    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.PresentAsync(activation, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Forward(backend => backend.MinimizeAsync(cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetVisibleAsync(visible, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetFullScreenAsync(fullScreen, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Forward(backend => backend.SetDocumentEditedAsync(edited, cancellationToken), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return Forward(backend => backend.SetColorSchemeAsync(colorScheme, cancellationToken), cancellationToken);
    }

    private ValueTask<PlatformResult<PlatformUnit>> Forward(Func<IRunicWindowPresentation, ValueTask<PlatformResult<PlatformUnit>>> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed)) return ValueTask.FromResult(Closed);
        return Volatile.Read(ref _backend) is { } backend ? request(backend) : ValueTask.FromResult(NotOpen);
    }

    private void OnBackendActivityChanged(object? sender, RunicWindowActivityChangedEventArgs args)
    {
        if (!Volatile.Read(ref _closed)) Deliver(args.Activity);
    }

    private void OnBackendPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ColorScheme) || Volatile.Read(ref _closed) || sender is not IRunicWindowPresentation backend) return;
        var colorScheme = backend.ColorScheme;
        InTurn(() =>
        {
            if (_colorScheme == colorScheme) return;
            _colorScheme = colorScheme;
            Raise(nameof(ColorScheme));
        });
    }

    // The change is applied in a turn of the Window's model context, so a handler reads and writes ViewModel state as a
    // command does. Turns run in order, so the last change wins.
    private void Deliver(RunicWindowActivity activity) => InTurn(() =>
    {
        var previous = _activity;
        if (Equals(previous, activity)) return;
        Volatile.Write(ref _activity, activity);
        if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
        if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
        if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
        Raise(nameof(Activity));
        if (ActivityChanged is not { } handlers) return;
        var args = new RunicWindowActivityChangedEventArgs(previous, activity);
        foreach (EventHandler<RunicWindowActivityChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(ActivityChanged)); }
        }
    });

    private void InTurn(Action change)
    {
        var context = _context?.Invoke();
        if (context is null || context.IsExecuting || !context.TryPost(change)) change();
    }

    private void Raise(string property)
    {
        if (PropertyChanged is not { } handlers) return;
        var args = new PropertyChangedEventArgs(property);
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception error) when (error is not OutOfMemoryException) { HandlerFailed(error, nameof(PropertyChanged)); }
        }
    }

    private void HandlerFailed(Exception error, string handler) =>
        ViewsLog.WindowPresentationHandlerFailed(_logger?.Invoke() ?? TraceFallbackLogger.Instance, error, handler,
            error.GetType().FullName ?? error.GetType().Name);
}
