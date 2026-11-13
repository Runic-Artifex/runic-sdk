using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>
/// A real <see cref="RunicWindow{TViewModel}"/> opened headless by <see cref="RunicWindowTestHost.CreateWindowAsync{TWindow}"/>,
/// or by the application while it runs, for example from a command through <see cref="IRunicWindowService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Window opens as on Runic Desktop: in a new scope of the application's services, with its parameter, its result,
/// its <see cref="IRunicWindowContext"/> and its close guard. <see cref="RequestUserCloseAsync"/> is the user's close
/// button, which asks <see cref="RunicWindow{TViewModel}.CanCloseAsync"/> when the Window guards its close. Each Window
/// has a <see cref="RunicTestWindowPresentation"/>, so <see cref="IRunicWindowService.ActivateAsync"/> and
/// <see cref="IRunicWindowService.FindOrOpenAsync{TWindow, TParameter}"/> are recorded as <see cref="RunicTestWindowPresentation.PresentCount"/>.
/// </para>
/// <para>
/// An owned Window (<see cref="RunicWindowOptions.Owner"/>) closes when its owner closes, and a window-modal one
/// sets <see cref="IsBlocked"/> on its owner while it is open, as on Runic Desktop.
/// </para>
/// </remarks>
public sealed class RunicTestWindow : IAsyncDisposable
{
    private readonly RunicTestWindowApplication _application;
    private readonly ITestBridgeWindow _bridge;

    internal RunicTestWindow(RunicTestWindowApplication application, ITestBridgeWindow bridge, IRunicWindow window,
        RunicWindowOptions options, RunicTestWindow? owner, object? parameter, object? key)
    {
        _application = application;
        _bridge = bridge;
        Window = window;
        Owner = owner;
        Modality = owner is null ? RunicWindowModality.None : options.Modality;
        Options = options;
        Parameter = parameter;
        Key = key;
    }

    /// <summary>The Window.</summary>
    public IRunicWindow Window { get; }

    /// <summary>The Window's root ViewModel.</summary>
    public object ViewModel => Window.DataContext!;

    /// <summary>The options the Window opened with.</summary>
    public RunicWindowOptions Options { get; }

    /// <summary>The parameter the Window opened with (<see cref="IRunicWindowParameter{TParameter}"/>), or <see langword="null"/>.</summary>
    public object? Parameter { get; }

    /// <summary>The key of <see cref="IRunicWindowService.FindOrOpenAsync{TWindow, TParameter}"/>, or <see langword="null"/>.</summary>
    public object? Key { get; }

    /// <summary>The Window that owns this one (<see cref="RunicWindowOptions.Owner"/>), or <see langword="null"/>.</summary>
    public RunicTestWindow? Owner { get; }

    /// <summary>Whether this Window blocks its <see cref="Owner"/>; <see cref="RunicWindowModality.None"/> without one.</summary>
    public RunicWindowModality Modality { get; }

    /// <summary>Whether a window-modal Window that this Window owns is open, so the user cannot use this one.</summary>
    public bool IsBlocked => _application.IsBlocked(this);

    /// <summary>Whether the Window is open: it has not closed, by the user or the application.</summary>
    /// <remarks>A Window that drains after its close was decided (<see cref="IsDraining"/>) is still open.</remarks>
    public bool IsOpen => !_bridge.IsClosed;

    /// <summary>
    /// Whether the Window's close was decided and it stays open while its accepted work drains
    /// (<see cref="RunicWindowOptions.CloseDrain"/>, <see cref="RunicWindowTestHostOptions.CloseDrain"/>): its operation
    /// admission stopped, its page received <c>closing</c> with <c>draining: true</c> and still receives its state. It
    /// closes by itself when the work has ended or the drain's timeout passed on the Window's clock.
    /// </summary>
    /// <remarks>Experimental (<c>RUNICDESK105</c>); see <c>docs/experimental.md</c>.</remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("RUNICDESK105", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
    public bool IsDraining => _bridge.IsDraining;

    /// <summary>The Window's recording presentation.</summary>
    public RunicTestWindowPresentation Presentation => _bridge.Presentation;

    /// <summary>The services of the Window's scope.</summary>
    public IServiceProvider Services => Window.Services;

    /// <summary>
    /// The Windows the application opened after this one, in the order they opened, including those that closed since.
    /// </summary>
    public IReadOnlyList<RunicTestWindow> OpenedWindows => _application.WindowsAfter(this);

    /// <summary>
    /// The test host of the Window's content session, which drives its root ViewModel and the Views it presents.
    /// </summary>
    /// <typeparam name="TViewModel">The Window's root ViewModel type.</typeparam>
    /// <exception cref="InvalidOperationException">The Window's ViewModel is not a <typeparamref name="TViewModel"/>.</exception>
    public RunicWindowTestHost<TViewModel> Host<TViewModel>() where TViewModel : class =>
        _bridge.Host as RunicWindowTestHost<TViewModel> ?? throw new InvalidOperationException(
            $"{Window.GetType().Name} presents a {ViewModel.GetType().Name}, not a {typeof(TViewModel).Name}.");

    /// <summary>Drives the Window's root ViewModel, as <see cref="RunicWindowTestHost{TViewModel}.Root"/> does.</summary>
    /// <typeparam name="TViewModel">The Window's root ViewModel type.</typeparam>
    public RunicViewDriver<TViewModel> Root<TViewModel>() where TViewModel : class => Host<TViewModel>().Root;

    /// <summary>
    /// Closes the Window as the user's close button does, and returns whether it closed: a Window that guards its close
    /// (<see cref="RunicWindow{TViewModel}.GuardClose"/>) first asks <see cref="RunicWindow{TViewModel}.CanCloseAsync"/> and
    /// stays open when that returns <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Windows it owns are asked first, innermost first, as Runic Desktop does: one that refuses keeps this Window
    /// open, and a window-modal one that agrees closes before this Window's own guard is asked. A request while another
    /// request or <see cref="IRunicWindow.RequestCloseAsync"/> is being decided waits for the same decision. A closed
    /// Window returns <see langword="true"/>. The Windows it owns close with it.
    /// </para>
    /// <para>
    /// A Window with a close drain (<see cref="RunicWindowOptions.CloseDrain"/>) returns <see langword="true"/> once the
    /// close was decided and stays open while it drains (<see cref="IsDraining"/>); <see cref="IRunicWindow.WaitForCloseAsync"/>
    /// waits until it closed. A request during the drain is ignored and returns <see langword="true"/>, as the user's second
    /// close is.
    /// </para>
    /// <para>
    /// The user cannot close a Window that a window-modal Window blocks (<see cref="IsBlocked"/>), as on Windows and macOS:
    /// the call throws instead of passing where the application could not. Its page takes no input either: its root
    /// driver's commands, operation starts, sets and writes throw <see cref="InvalidOperationException"/>, while snapshots
    /// still read its state. <see cref="IRunicWindow.RequestCloseAsync"/>, the application's request, still works.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops this caller's wait; the decision continues.</param>
    /// <returns><see langword="true"/> when the Window closed.</returns>
    /// <exception cref="InvalidOperationException">A window-modal Window blocks this one.</exception>
    public Task<bool> RequestUserCloseAsync(CancellationToken cancellationToken = default)
    {
        if (BlockedReason() is { } blocked) throw new InvalidOperationException(blocked);
        return _bridge.RequestUserCloseAsync(cancellationToken).WaitAsync(cancellationToken);
    }

    // Why the user cannot use this Window now, or null.
    internal string? BlockedReason() => IsBlocked
        ? $"{Window.GetType().Name} is blocked by a window-modal Window it owns; close that Window first, as the user would."
        : null;

    // The Window that CreateWindowAsync opened: disposing it ends the whole test application.
    internal bool IsRoot { get; init; }

    /// <summary>
    /// Waits until the application opened a <typeparamref name="TWindow"/> after this Window and returns the last one
    /// still open, for example a dialog that a command opened with <see cref="IRunicWindowService.OpenForResultAsync{TWindow, TParameter, TResult}"/>.
    /// </summary>
    /// <param name="timeout">How long to wait in real time; <see langword="null"/> for 5 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">No such Window opened in time.</exception>
    public Task<RunicTestWindow> WaitForOpenedWindowAsync<TWindow>(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow =>
        _application.WaitForWindowAsync(this, typeof(TWindow), timeout ?? TimeSpan.FromSeconds(5), cancellationToken);

    /// <summary>
    /// Closes this Window and releases its scope. For the Window that <see cref="RunicWindowTestHost.CreateWindowAsync{TWindow}"/>
    /// returned, it closes and releases every Window of the application; for one of <see cref="OpenedWindows"/>, only that
    /// Window and the Windows it owns, which close with it.
    /// </summary>
    public ValueTask DisposeAsync() => IsRoot ? _application.DisposeAsync() : _bridge.DisposeAsync();

    internal ITestBridgeWindow Bridge => _bridge;
}

internal interface ITestBridgeWindow
{
    bool IsClosed { get; }
    bool IsDraining { get; }
    RunicTestWindowPresentation Presentation { get; }
    object Host { get; }
    Task<bool> RequestUserCloseAsync(CancellationToken cancellationToken);
    Task<bool> AskToCloseAsync(CancellationToken cancellationToken);
    ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask DisposeAsync();
}

// The headless host of RunicWindowTestHost.CreateWindowAsync: every Window opens through the production open path, so
// the application's IRunicWindowService, bound to this host, finds, activates and opens Windows for a result.
internal sealed class RunicTestWindowApplication(IServiceProvider services, RunicWindowTestHostOptions options) : IRunicWindowHost
{
    private readonly Lock _gate = new();
    private readonly List<RunicTestWindow> _windows = [];
    private TaskCompletionSource _changed = NewSignal();
    private readonly TimeProvider _time = options.TimeProvider ?? new FakeTimeProvider();
    private long _nextId;
    private int _disposed;
    private RunicTestWindow? _root;

    public IServiceProvider Services { get; } = services;

    // RunicWindowTestHostOptions.CloseDrain: the drain of the Windows whose options set none.
    internal RunicCloseDrain? CloseDrain => options.CloseDrain;

    // RunicWindowTestHostOptions.ApplicationLocale: the application's locale and the one DisposeAsync sets back.
    private (IRunicApplicationLocale Locale, string? Previous)? _restoreLocale;

    internal async ValueTask StartApplicationLocaleAsync(string locale, CancellationToken cancellationToken)
    {
        var applicationLocale = Services.GetService<IRunicApplicationLocale>() ?? throw new InvalidOperationException(
            "RunicWindowTestHostOptions.ApplicationLocale sets the application's IRunicApplicationLocale, which the services do " +
            "not register. Call AddRunicWindowService() on them.");
        _restoreLocale = (applicationLocale, applicationLocale.Locale);
        await applicationLocale.SetLocaleAsync(locale, cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RunicTestWindow Find(IRunicWindow window)
    {
        lock (_gate) return _windows.Single(item => ReferenceEquals(item.Window, window));
    }

    internal IReadOnlyList<RunicTestWindow> WindowsAfter(RunicTestWindow window)
    {
        lock (_gate) return [.. _windows.SkipWhile(item => !ReferenceEquals(item, window)).Skip(1)];
    }

    internal bool IsBlocked(RunicTestWindow window)
    {
        lock (_gate) return _windows.Any(item => ReferenceEquals(item.Owner, window) && item.Modality == RunicWindowModality.Window && item.IsOpen);
    }

    // The open Windows that window owns, the last opened first.
    internal RunicTestWindow[] OwnedBy(RunicTestWindow window)
    {
        lock (_gate) return [.. _windows.Where(item => ReferenceEquals(item.Owner, window) && item.IsOpen).Reverse()];
    }

    // The owned Windows that their owner hid with it, shown again with it.
    private readonly Dictionary<RunicTestWindow, List<RunicTestWindow>> _hiddenWithOwner = [];

    // SetVisibleAsync of an owner: its owned Windows hide and show with it, as on Runic Desktop.
    internal void OwnerVisibilitySet(RunicTestWindow owner, bool visible)
    {
        List<RunicTestWindow> windows;
        lock (_gate)
        {
            if (visible)
            {
                windows = _hiddenWithOwner.Remove(owner, out var hidden) ? hidden : [];
            }
            else
            {
                windows = [.. _windows.Where(item => ReferenceEquals(item.Owner, owner) && item.IsOpen && item.Presentation.IsVisible)];
                if (_hiddenWithOwner.TryGetValue(owner, out var hidden)) hidden.AddRange(windows);
                else _hiddenWithOwner[owner] = [.. windows];
            }
        }
        foreach (var window in windows)
        {
            if (!window.IsOpen) continue;
            window.Presentation.SetVisibleWithOwner(visible);
            // Nested owned Windows follow their own owner.
            OwnerVisibilitySet(window, visible);
        }
    }

    // Closes the open Windows owner owns, without asking, as Runic Desktop does when an owner closes.
    internal async Task CloseOwnedAsync(IRunicWindow owner)
    {
        RunicTestWindow[] owned;
        lock (_gate) owned = [.. _windows.Where(item => item.Owner?.Window == owner && item.IsOpen)];
        foreach (var window in owned) _ = await window.Bridge.CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    internal async Task<RunicTestWindow> WaitForWindowAsync(RunicTestWindow after, Type type, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (WindowsAfter(after).LastOrDefault(item => item.Window.GetType() == type && item.IsOpen) is { } found) return found;
                changed = _changed.Task;
            }
            try { await changed.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No {type.Name} opened within {timeout}.");
            }
        }
    }

    public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) =>
        options.Owner is { } owner && !IsOurs(owner)
            ? [new RunicWindowDiagnostic("window-owner-unavailable", "The owner was not opened by this test host.") { Severity = RunicWindowDiagnosticSeverity.Warning }]
            : [];

    private bool IsOurs(IRunicWindow window)
    {
        lock (_gate) return _windows.Any(item => ReferenceEquals(item.Window, window) && item.IsOpen);
    }

    public async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening, CancellationToken cancellationToken)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(opening);
        var scope = opening.Scope;
        TestBridgeWindow<TViewModel>? bridge = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var provider = scope.ServiceProvider;
            var presentation = provider.GetService<RunicTestWindowPresentation>() ?? new RunicTestWindowPresentation();
            var loggerFactory = options.LoggerFactory ?? provider.GetService<ILoggerFactory>();
            var host = new RunicWindowTestHost<TViewModel>(opening.ViewModel, (transport, content, _) => opening.AttachBridge(transport, content),
                new RunicWindowTestHostOptions
                {
                    ViewLocator = options.ViewLocator ?? provider.GetService<IRunicViewLocator>(),
                    OperationShutdown = options.OperationShutdown,
                    ModelContext = options.ModelContext ?? provider.GetService<IRunicModelContext>(),
                    LoggerFactory = loggerFactory,
                    // One clock and one id source for the application's Windows: a test advances one clock.
                    TimeProvider = _time,
                    CreateId = options.CreateId ?? (() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture)),
                    IncludeFailureDetail = opening.Options.IncludeFailureDetail ?? options.IncludeFailureDetail,
                    // As Runic Desktop and CS-WebUI: the Window scope's RunicValidationOptions (TextFor,
                    // configureFieldErrorText's reasons) unless the test host's options name others.
                    Validation = options.Validation ?? provider.GetService<RunicValidationOptions>(),
                    FlowCultureFromLocale = opening.Options.FlowCultureFromLocale ?? options.FlowCultureFromLocale,
                    // Each Window's own locale; ApplicationLocale is the application's, which StartApplicationLocaleAsync set.
                    Locale = options.Locale,
                    MaximumInputBytes = opening.Options.MaximumInputBytes ?? options.MaximumInputBytes,
                    MaximumUploadBytes = opening.Options.MaximumUploadBytes ?? options.MaximumUploadBytes,
                    PublicationTimeout = options.PublicationTimeout,
                })
            { Services = provider, WindowPresentation = presentation };
            bridge = new TestBridgeWindow<TViewModel>(this, opening, host, presentation);
            RunicTestWindow? owner = null;
            if (opening.Options.Owner is { } ownerWindow)
            {
                lock (_gate) owner = _windows.FirstOrDefault(item => ReferenceEquals(item.Window, ownerWindow) && item.IsOpen);
                if (owner is null) opening.ReportOwnerIgnored("the owner is not an open Window of this test host");
            }
            // RunicWindow.RequestCloseAsync and the user's close share one decision; the application may request it while a
            // modal Window blocks this one.
            var closing = bridge;
            opening.UseCloseRequest(cancellationToken => new ValueTask<bool>(closing.RequestUserCloseAsync(cancellationToken)));
            opening.UsePresentation(presentation);
            var window = new RunicTestWindow(this, bridge, opening.Window, opening.Options, owner,
                provider.GetService<RunicWindowParameter>()?.Peek(), opening.Key)
            { IsRoot = Volatile.Read(ref _root) is null };
            Interlocked.CompareExchange(ref _root, window, null);
            bridge.TestWindow = window;
            host.Transport.InputBlocked = window.BlockedReason;
            presentation.VisibilitySet += visible => OwnerVisibilitySet(window, visible);
            Add(window);
            return bridge;
        }
        catch
        {
            if (bridge is not null) await bridge.DisposeAsync().ConfigureAwait(false);
            else await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Add(RunicTestWindow window)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _windows.Add(window);
            (changed, _changed) = (_changed, NewSignal());
        }
        changed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RunicTestWindow[] windows;
        lock (_gate) windows = [.. _windows];
        // Owned Windows first, as an application closes a dialog before its owner.
        try
        {
            foreach (var window in windows.Reverse()) await window.Bridge.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_restoreLocale is { } restore) await RunicWindowTestHost.RestoreApplicationLocaleAsync(restore).ConfigureAwait(false);
        }
    }
}

internal sealed class TestBridgeWindow<TViewModel>(RunicTestWindowApplication application, RunicWindowOpening<TViewModel> opening,
    RunicWindowTestHost<TViewModel> host, RunicTestWindowPresentation presentation) : IBridgeWindow, ITestBridgeWindow
    where TViewModel : class
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closeWithoutAsking = new();
    private readonly Lock _gate = new();
    private Task<RunicWindowCloseResult>? _close;
    private Task<bool>? _decision;
    private int _disposed;
    // RunicCloseDrain: the Window's, else the test host's default.
    private readonly RunicCloseDrain _drain = opening.Options.CloseDrain ?? application.CloseDrain ?? RunicCloseDrain.None;
    // Ends the visible phase of a drain that a decided close started: the application's own close or disposal.
    private CancellationTokenSource? _endDrain;
    private int _draining;

    internal RunicTestWindow? TestWindow { get; set; }

    public bool IsClosed => _closed.Task.IsCompleted;

    public bool IsDraining => Volatile.Read(ref _draining) != 0 && !IsClosed;

    public RunicTestWindowPresentation Presentation { get; } = presentation;

    public object Host => host;

    public Task<bool> RequestUserCloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_decision is null || _decision.IsCompleted) _decision = Task.Run(DecideAsync, CancellationToken.None);
            return _decision.WaitAsync(cancellationToken);
        }
    }

    // As Runic Desktop's embedded window: the guard decides, then the window closes; an application close meanwhile ends
    // the decision.
    // With a close drain, the decision returns once the guards agreed and the window stays open while it drains; a request
    // meanwhile, such as the user's second close, is ignored.
    private async Task<bool> DecideAsync()
    {
        if (IsClosed || Volatile.Read(ref _draining) != 0) return true;
        if (!await AskToCloseAsync(_closeWithoutAsking.Token).ConfigureAwait(false)) return IsClosed;
        if (_drain.KeepsWindow)
        {
            StartDrain();
            return true;
        }
        _ = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        return true;
    }

    // The closing phase of a decided close with a drain (Runic Desktop's, sdk#900): the Window leaves the open Windows,
    // admission stops, the page gets closing with draining: true and stays attached, and the window closes when its work
    // has ended or the drain's timeout passed.
    // The drain starts before the decision returns, so its timeout runs from then on the Window's clock.
    private void StartDrain()
    {
        Task<Task<RunicWindowCloseResult>> start;
        lock (_gate)
        {
            if (_close is not null) return;
            var end = _endDrain = new CancellationTokenSource();
            Volatile.Write(ref _draining, 1);
            start = new Task<Task<RunicWindowCloseResult>>(() => CloseCoreAsync(_drain.Timeout, draining: true, end.Token));
            _close = start.Unwrap();
        }
        start.RunSynchronously(TaskScheduler.Default);
    }

    // As Runic Desktop's owner close: the owned Windows first, innermost first; a window-modal one that agrees closes
    // before this Window's own guard is asked, so that guard never asks behind it.
    public async Task<bool> AskToCloseAsync(CancellationToken cancellationToken)
    {
        if (TestWindow is { } window)
        {
            foreach (var owned in application.OwnedBy(window))
            {
                if (!await owned.Bridge.AskToCloseAsync(cancellationToken).ConfigureAwait(false)) return false;
                if (owned.Modality == RunicWindowModality.Window) _ = await owned.Bridge.CloseAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
        }
        return opening.CloseGuard is not { } guard || await guard(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task<RunicWindowCloseResult> close;
        CancellationTokenSource? drain = null;
        lock (_gate)
        {
            if (_close is null) _close = Task.Run(() => CloseCoreAsync(timeout, draining: false, CancellationToken.None), CancellationToken.None);
            else drain = _endDrain;
            close = _close;
        }
        // The application's own close during a drain keeps its timeout: the visible window closes then at the latest.
        // Outside the gate, because ending the drain continues the close on this thread.
        if (drain is not null && !close.IsCompleted) EndDrainAfter(drain, timeout);
        return new(close.WaitAsync(cancellationToken));
    }

    private void EndDrainAfter(CancellationTokenSource end, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return;
        if (timeout == TimeSpan.Zero)
        {
            end.Cancel();
            return;
        }
        _ = Task.Delay(timeout, host.Time).ContinueWith(_ => end.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<RunicWindowCloseResult> CloseCoreAsync(TimeSpan timeout, bool draining, CancellationToken endDrain)
    {
        opening.Window.MarkClosing();
        // A drain starts from the decided close, which no pending decision can still need.
        if (draining) _closeWithoutAsking.Cancel();
        else await _closeWithoutAsking.CancelAsync().ConfigureAwait(false);
        RunicWindowCloseResult drained;
        try { drained = await host.DrainAsync(timeout, draining, endDrain).ConfigureAwait(false); }
        // The application closed or disposed the Window during its drain: from here on a close without a timeout, which
        // asks the remaining operations to cancel, except durable ones.
        catch (OperationCanceledException) when (endDrain.IsCancellationRequested)
        {
            drained = await host.DrainAsync(TimeSpan.Zero, draining, CancellationToken.None).ConfigureAwait(false);
        }
        Presentation.SetActivity(RunicWindowActivity.Closed);
        _closed.TrySetResult();
        await application.CloseOwnedAsync(opening.Window).ConfigureAwait(false);
        return drained;
    }

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        new(_closed.Task.WaitAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var closed = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        // Durable work is never asked to cancel, so the Window's scope stays until it ended, as on Runic Desktop.
        if (closed.RemainingDurableWork > 0) await closed.Completion.ConfigureAwait(false);
        try { host.Dispose(); }
        finally { await opening.Scope.DisposeAsync().ConfigureAwait(false); }
    }
}
