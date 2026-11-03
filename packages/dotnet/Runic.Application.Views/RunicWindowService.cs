using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Runic.Platform;

namespace Runic.Application.Views;

/// <summary>
/// Opens Windows from ViewModels and application services, on the host that runs the application. Take it in a
/// constructor and open a Window from a command:
/// <code>
/// [RelayCommand]
/// private async Task OpenProjectAsync() =&gt; await _windows.OpenWindowAsync&lt;ProjectWindow&gt;();
/// </code>
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it as a singleton, so
/// ViewModels in every Window's scope and singleton services can take it. It works the same on every host, because
/// it calls <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> on the host that opened the
/// application's Windows. In tests, register a fake that records the requested Window type instead.
/// </remarks>
public interface IRunicWindowService
{
    /// <summary>
    /// Creates <typeparamref name="TWindow"/>, resolves its ViewModel in a new DI scope, and opens it, as
    /// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> does.
    /// </summary>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>
    /// The open Window. The service releases it once it closes; awaiting <see cref="IRunicWindow.WaitForCloseAsync"/>
    /// or disposing it earlier is optional.
    /// </returns>
    /// <exception cref="InvalidOperationException">No host has opened a Window with these services yet.</exception>
    /// <exception cref="RunicWindowConfigurationException">A check failed before anything opened.</exception>
    ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new();

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> as <see cref="OpenWindowAsync{TWindow}"/> does, and hands
    /// <paramref name="parameter"/> to the new Window's scope, where its ViewModel takes
    /// <see cref="IRunicWindowParameter{TParameter}"/>, as
    /// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow, TParameter}"/> does.
    /// </summary>
    /// <remarks>
    /// <code>
    /// await _windows.OpenWindowAsync&lt;PartWindow, PartRequest&gt;(new PartRequest(world, part));
    ///
    /// public sealed class PartViewModel(IRunicWindowParameter&lt;PartRequest&gt; request) { ... }
    /// </code>
    /// The default implementation, for a test fake that does not override it, throws <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <param name="parameter">The value the new Window's scope receives.</param>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>The open Window, released by the service once it closes.</returns>
    /// <exception cref="InvalidOperationException">No host has opened a Window with these services yet.</exception>
    /// <exception cref="RunicWindowConfigurationException">A check failed before anything opened.</exception>
    ValueTask<TWindow> OpenWindowAsync<TWindow, TParameter>(TParameter parameter, RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class =>
        throw new NotSupportedException($"{GetType().Name} does not open Windows with a parameter.");

    /// <summary>
    /// The open Windows of the application, in the order they opened: every Window that a host opened with these
    /// services, through this service or <c>host.OpenWindowAsync</c>, until its close starts.
    /// </summary>
    /// <remarks>
    /// Each read returns a new list. <see cref="WindowsChanged"/> reports each change, and
    /// <see cref="RunicWindowServiceExtensions.ObserveWindows(IRunicWindowService)"/> observes the list. The default implementation, for a test
    /// fake, throws <see cref="NotSupportedException"/>.
    /// </remarks>
    IReadOnlyList<IRunicWindow> Windows => throw new NotSupportedException($"{GetType().Name} does not list Windows.");

    /// <summary>Occurs after a Window opened or its close started, so <see cref="Windows"/> changed.</summary>
    /// <remarks>
    /// The changes are raised one at a time and in order, on a thread-pool thread, each with its own
    /// <see cref="RunicWindowsChangedEventArgs.Windows"/> list. They are not raised in any Window's model context: a
    /// ViewModel, which belongs to its own Window, posts what it does to its model context, or observes the list with
    /// <see cref="RunicWindowServiceExtensions.ObserveWindows(IRunicWindowService, Runic.Navigation.IRunicModelContext)"/>,
    /// which does that. A handler that throws is logged (event 1181) and does not stop the others. The default
    /// implementation, for a test fake, does nothing.
    /// </remarks>
    event EventHandler<RunicWindowsChangedEventArgs>? WindowsChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The open <typeparamref name="TWindow"/> that <see cref="FindOrOpenAsync{TWindow, TParameter}"/> opened with
    /// <paramref name="key"/>, or <see langword="null"/>.
    /// </summary>
    /// <param name="key">The key, compared with <see cref="object.Equals(object?)"/> among the Windows of this type.</param>
    /// <remarks>The default implementation, for a test fake, throws <see cref="NotSupportedException"/>.</remarks>
    TWindow? FindWindow<TWindow>(object key) where TWindow : class, IRunicWindow =>
        throw new NotSupportedException($"{GetType().Name} does not find Windows.");

    /// <summary>
    /// Brings the open <typeparamref name="TWindow"/> with <paramref name="key"/> to the front, or opens a new one with
    /// that key, as <see cref="OpenWindowAsync{TWindow}"/> does.
    /// </summary>
    /// <remarks>
    /// <code>
    /// [RelayCommand]
    /// private async Task OpenDocumentAsync(string path) =&gt;
    ///     await _windows.FindOrOpenAsync&lt;DocumentWindow, DocumentRequest&gt;(path, new DocumentRequest(path));
    /// </code>
    /// The key belongs to the Window type: a <c>DocumentWindow</c> and a <c>PreviewWindow</c> for the same path are two
    /// Windows. A Window that is found is brought forward with <see cref="ActivateAsync"/>, which shows a hidden one and
    /// restores a minimized one; the parameter and options are then not used. Two calls with the same key at once open
    /// one Window. A closed Window is forgotten, so the next call opens a new one. The default implementation, for a
    /// test fake, throws <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <param name="key">The Window's identity, such as a document path; compared with <see cref="object.Equals(object?)"/>.</param>
    /// <param name="parameter">The new Window's parameter, as for <see cref="OpenWindowAsync{TWindow, TParameter}"/>.</param>
    /// <param name="options">The new Window's options.</param>
    /// <param name="activation">The desktop's activation data of a launch, passed to the Window's presentation.</param>
    /// <param name="cancellationToken">Cancels opening or activating.</param>
    /// <returns>The Window that was found or opened.</returns>
    ValueTask<TWindow> FindOrOpenAsync<TWindow, TParameter>(object key, TParameter parameter, RunicWindowOptions? options = null,
        DesktopActivationContext? activation = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class =>
        throw new NotSupportedException($"{GetType().Name} does not find Windows.");

    /// <summary>
    /// Brings the open <typeparamref name="TWindow"/> with <paramref name="key"/> to the front, or opens a new one without
    /// a parameter, as <see cref="FindOrOpenAsync{TWindow, TParameter}"/> does.
    /// </summary>
    /// <param name="key">The Window's identity; compared with <see cref="object.Equals(object?)"/>.</param>
    /// <returns>The Window that was found or opened.</returns>
    ValueTask<TWindow> FindOrOpenAsync<TWindow>(object key)
        where TWindow : class, IRunicWindow, new() =>
        FindOrOpenAsync<TWindow>(key, null, null, CancellationToken.None);

    /// <summary>
    /// Brings the open <typeparamref name="TWindow"/> with <paramref name="key"/> to the front, or opens a new one without
    /// a parameter, as <see cref="FindOrOpenAsync{TWindow, TParameter}"/> does.
    /// </summary>
    /// <param name="key">The Window's identity; compared with <see cref="object.Equals(object?)"/>.</param>
    /// <param name="options">The new Window's options, or <see langword="null"/>.</param>
    /// <param name="activation">The desktop's activation data of a launch, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels opening or activating.</param>
    /// <returns>The Window that was found or opened.</returns>
    /// <remarks>The default implementation, for a test fake, throws <see cref="NotSupportedException"/>.</remarks>
    ValueTask<TWindow> FindOrOpenAsync<TWindow>(object key, RunicWindowOptions? options, DesktopActivationContext? activation,
        CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new() =>
        throw new NotSupportedException($"{GetType().Name} does not find Windows.");

    /// <summary>
    /// Brings <paramref name="window"/> to the front with <see cref="IRunicWindowPresentation.PresentAsync"/>: it shows a
    /// hidden Window, restores a minimized one and asks for the focus. When a window-modal Window blocks it, that Window
    /// is brought forward after it.
    /// </summary>
    /// <param name="window">An open Window.</param>
    /// <param name="activation">The desktop's activation data of a launch, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The result of presenting <paramref name="window"/>, such as <c>Unavailable</c> on a host without it.</returns>
    /// <remarks>The default implementation presents <paramref name="window"/> alone.</remarks>
    ValueTask<PlatformResult<PlatformUnit>> ActivateAsync(IRunicWindow window, DesktopActivationContext? activation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Presentation.PresentAsync(activation, cancellationToken);
    }

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> with <paramref name="parameter"/>, as
    /// <see cref="OpenWindowAsync{TWindow, TParameter}"/> does, and returns its result once it closed.
    /// </summary>
    /// <remarks>
    /// <code>
    /// var chosen = await _windows.OpenForResultAsync&lt;ChooseAccountWindow, AccountQuery, Account&gt;(query,
    ///     new RunicWindowOptions { Owner = _window.Window, Modality = RunicWindowModality.Window });
    /// if (chosen.TryGetValue(out var account)) Use(account);
    /// </code>
    /// The Window's ViewModel takes <see cref="IRunicWindowResult{TResult}"/> and completes it, which closes the Window. A
    /// Window that closes without a result, by the user or the application, returns none (<see cref="RunicWindowResult{TResult}.HasValue"/> is <see langword="false"/>).
    /// Give it an owner and <see cref="RunicWindowModality.Window"/> for a dialog. Cancelling
    /// <paramref name="cancellationToken"/> while the Window is open closes it without asking and throws
    /// <see cref="OperationCanceledException"/>. The default implementation, for a test fake, throws
    /// <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <param name="parameter">The value the new Window's scope receives.</param>
    /// <param name="options">The Window's options, such as its owner and modality.</param>
    /// <param name="cancellationToken">Cancels opening, or closes the open Window.</param>
    /// <returns>The result, or none (<see cref="RunicWindowResult{TResult}.HasValue"/> is <see langword="false"/>).</returns>
    ValueTask<RunicWindowResult<TResult>> OpenForResultAsync<TWindow, TParameter, TResult>(TParameter parameter,
        RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class =>
        throw new NotSupportedException($"{GetType().Name} does not open Windows for a result.");

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> without a parameter and returns its result once it closed, as
    /// <see cref="OpenForResultAsync{TWindow, TParameter, TResult}"/> does.
    /// </summary>
    /// <param name="options">The Window's options, such as its owner and modality.</param>
    /// <param name="cancellationToken">Cancels opening, or closes the open Window.</param>
    /// <returns>The result, or none (<see cref="RunicWindowResult{TResult}.HasValue"/> is <see langword="false"/>).</returns>
    /// <remarks>The default implementation, for a test fake, throws <see cref="NotSupportedException"/>.</remarks>
    ValueTask<RunicWindowResult<TResult>> OpenForResultAsync<TWindow, TResult>(RunicWindowOptions? options,
        CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new() =>
        throw new NotSupportedException($"{GetType().Name} does not open Windows for a result.");

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> without a parameter or options and returns its result once it closed, as
    /// <see cref="OpenForResultAsync{TWindow, TParameter, TResult}"/> does.
    /// </summary>
    /// <returns>The result, or none.</returns>
    ValueTask<RunicWindowResult<TResult>> OpenForResultAsync<TWindow, TResult>()
        where TWindow : class, IRunicWindow, new() =>
        OpenForResultAsync<TWindow, TResult>(null, CancellationToken.None);
}

/// <summary>
/// The parameter that <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow, TParameter}"/> or
/// <see cref="IRunicWindowService.OpenWindowAsync{TWindow, TParameter}"/> handed to the Window whose scope resolves it.
/// Take it in the constructor of the Window's ViewModel, or of a scoped service the ViewModel uses.
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope. In a test, pass a
/// <see cref="RunicWindowParameter{TParameter}"/> to a ViewModel built by hand.
/// </remarks>
/// <typeparam name="TParameter">The parameter's type, as the open named it.</typeparam>
public interface IRunicWindowParameter<out TParameter> where TParameter : class
{
    /// <summary>The parameter the Window was opened with.</summary>
    /// <exception cref="InvalidOperationException">
    /// The Window was opened without a parameter of this type, or the service was resolved outside a Window's scope.
    /// </exception>
    TParameter Value { get; }
}

/// <summary>A fixed <see cref="IRunicWindowParameter{TParameter}"/>, for a ViewModel that a test builds by hand.</summary>
/// <param name="value">The parameter.</param>
public sealed class RunicWindowParameter<TParameter>(TParameter value) : IRunicWindowParameter<TParameter> where TParameter : class
{
    /// <inheritdoc />
    public TParameter Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
}

// The parameter of the open that created this scope.
internal sealed class RunicWindowParameter
{
    private object? _value;

    internal void Set(object value)
    {
        if (Interlocked.CompareExchange(ref _value, value, null) is not null)
            throw new InvalidOperationException("The Window's scope already has a parameter.");
    }

    internal object? Peek() => Volatile.Read(ref _value);

    internal TParameter Get<TParameter>() where TParameter : class => Volatile.Read(ref _value) switch
    {
        TParameter value => value,
        null => throw new InvalidOperationException(
            $"IRunicWindowParameter<{typeof(TParameter).Name}> was resolved in a scope without a parameter. Open the Window with " +
            $"OpenWindowAsync<TWindow, {typeof(TParameter).Name}>(parameter), and resolve it in that Window's scope."),
        var other => throw new InvalidOperationException(
            $"The Window was opened with a {other.GetType().Name} parameter, not a {typeof(TParameter).Name}."),
    };
}

internal sealed class ScopedWindowParameter<TParameter>(RunicWindowParameter slot) : IRunicWindowParameter<TParameter>
    where TParameter : class
{
    public TParameter Value => slot.Get<TParameter>();
}

/// <summary>Registers <see cref="IRunicWindowService"/> for dependency injection.</summary>
public static class RunicWindowServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRunicWindowService"/> as a singleton, and <see cref="IRunicWindowContext"/>,
    /// <see cref="IRunicConnections"/>, <see cref="IRunicWindowPresentation"/>, <see cref="IRunicWindowParameter{TParameter}"/> and
    /// <see cref="IRunicWindowResult{TResult}"/> per scope. Existing registrations are kept.
    /// </summary>
    /// <remarks>
    /// The window service follows the host, such as <c>RunicDesktopHost</c> or <c>RunicCsWebUiHost</c>, that most
    /// recently opened a Window with these services. A host binds it before it resolves that Window's ViewModel, so the
    /// service is ready in every ViewModel constructor and command. A service that uses it before the application
    /// opens its first Window gets an <see cref="InvalidOperationException"/>. The window context of each Window's
    /// scope is bound to that Window before its ViewModel is resolved.
    /// </remarks>
    public static IServiceCollection AddRunicWindowService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static provider => new RunicWindowService(provider));
        services.TryAddSingleton<IRunicWindowService>(static provider => provider.GetRequiredService<RunicWindowService>());
        services.TryAddScoped(static _ => new RunicWindowContext());
        services.TryAddScoped<IRunicWindowContext>(static provider => provider.GetRequiredService<RunicWindowContext>());
        services.TryAddScoped(static _ => new RunicConnections());
        services.TryAddScoped<IRunicConnections>(static provider => provider.GetRequiredService<RunicConnections>());
        services.TryAddScoped<IRunicWindowPresentation>(static provider => provider.GetRequiredService<RunicWindowContext>().Presentation);
        services.TryAddScoped(static _ => new RunicWindowParameter());
        services.TryAddScoped(typeof(IRunicWindowParameter<>), typeof(ScopedWindowParameter<>));
        RunicWindowResultRegistration.Add(services);
        return services;
    }
}

internal sealed class RunicWindowService(IServiceProvider services) : IRunicWindowService
{
    private IRunicWindowHost? _host;
    private readonly Lock _gate = new();
    // The open Windows, replaced on every change so a read needs no lock.
    private Entry[] _windows = [];
    // FindOrOpenAsync opens in progress, by Window type and key.
    private readonly Dictionary<(Type Type, object Key), Task<IRunicWindow>> _opening = [];

    private sealed record Entry(IRunicWindow Window, object? Key, IRunicWindow? Owner, RunicWindowModality Modality);

    // Called by every OpenWindowAsync before it creates the Window's scope.
    internal void Bind(IRunicWindowHost host) => Volatile.Write(ref _host, host);

    public IReadOnlyList<IRunicWindow> Windows => [.. Volatile.Read(ref _windows).Select(static entry => entry.Window)];

    public event EventHandler<RunicWindowsChangedEventArgs>? WindowsChanged;

    // The changes to raise, in order, and whether a thread-pool work item raises them.
    private readonly Queue<RunicWindowsChangedEventArgs> _changes = new();
    private bool _raising;
    private long _sequence;

    // ObserveWindows: the handler, and the list and order number it starts from, taken together.
    internal (IReadOnlyList<IRunicWindow> Windows, long Sequence) Subscribe(EventHandler<RunicWindowsChangedEventArgs> handler)
    {
        lock (_gate)
        {
            WindowsChanged += handler;
            return (WindowList(_windows), _sequence);
        }
    }

    private static IRunicWindow[] WindowList(Entry[] entries) => [.. entries.Select(static entry => entry.Window)];

    // RunicWindow.OpenAsync: every Window that opened with these services, until its close starts.
    internal void Track(IRunicWindow window, RunicWindowOptions options, object? key)
    {
        var entry = new Entry(window, key, options.Owner, options.Owner is null ? RunicWindowModality.None : options.Modality);
        lock (_gate)
        {
            _windows = [.. _windows, entry];
            Enqueue(window, opened: true);
        }
        RaiseChanges();
        // The entry goes when the close starts, so FindOrOpenAsync never returns a closing Window; a close the Window
        // does not report, such as a host's own, still ends it.
        window.WhenClosing(() => Forget(entry));
        _ = ForgetWhenClosedAsync(entry);
    }

    private async Task ForgetWhenClosedAsync(Entry entry)
    {
        try { await entry.Window.WaitForCloseAsync().ConfigureAwait(false); }
        catch (Exception) { /* Released or failed: closed either way. */ }
        Forget(entry);
    }

    private void Forget(Entry entry)
    {
        lock (_gate)
        {
            if (!_windows.Contains(entry)) return;
            _windows = [.. _windows.Where(item => !ReferenceEquals(item, entry))];
            Enqueue(entry.Window, opened: false);
        }
        RaiseChanges();
    }

    // Under _gate, with the change: its list and its place in the order.
    private void Enqueue(IRunicWindow window, bool opened) =>
        _changes.Enqueue(new RunicWindowsChangedEventArgs(window, opened, WindowList(_windows)) { Sequence = ++_sequence });

    // One thread-pool work item raises the queued changes, one at a time and in order.
    private void RaiseChanges()
    {
        lock (_gate)
        {
            if (_raising || _changes.Count == 0) return;
            _raising = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static service => service.DrainChanges(), this, preferLocal: false);
    }

    private void DrainChanges()
    {
        while (true)
        {
            RunicWindowsChangedEventArgs args;
            lock (_gate)
            {
                if (!_changes.TryDequeue(out args!))
                {
                    _raising = false;
                    return;
                }
            }
            if (WindowsChanged is not { } handlers) continue;
            foreach (EventHandler<RunicWindowsChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    ViewsLog.WindowsChangedHandlerFailed(Logger, error, error.GetType().FullName ?? error.GetType().Name);
                }
            }
        }
    }

    public TWindow? FindWindow<TWindow>(object key) where TWindow : class, IRunicWindow
    {
        ArgumentNullException.ThrowIfNull(key);
        return Find(typeof(TWindow), key) as TWindow;
    }

    private IRunicWindow? Find(Type type, object key) => Volatile.Read(ref _windows)
        .LastOrDefault(entry => entry.Window.GetType() == type && Equals(entry.Key, key) && !entry.Window.IsClosing)?.Window;

    public async ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new() =>
        await OpenCoreAsync<TWindow>(RunicWindowOpenRequest.Plain, options, cancellationToken).ConfigureAwait(false);

    public async ValueTask<TWindow> OpenWindowAsync<TWindow, TParameter>(TParameter parameter, RunicWindowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return await OpenCoreAsync<TWindow>(new RunicWindowOpenRequest(parameter, null, null), options, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<TWindow> OpenCoreAsync<TWindow>(RunicWindowOpenRequest request, RunicWindowOptions? options,
        CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new()
    {
        var window = await RunicWindowHostExtensions.OpenAsync<TWindow>(Host<TWindow>(), options, request, cancellationToken)
            .ConfigureAwait(false);
        _ = ReleaseWhenClosedAsync(window);
        return window;
    }

    public ValueTask<TWindow> FindOrOpenAsync<TWindow, TParameter>(object key, TParameter parameter, RunicWindowOptions? options = null,
        DesktopActivationContext? activation = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return FindOrOpenCoreAsync<TWindow>(key, parameter, options, activation, cancellationToken);
    }

    public ValueTask<TWindow> FindOrOpenAsync<TWindow>(object key, RunicWindowOptions? options, DesktopActivationContext? activation,
        CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new() =>
        FindOrOpenCoreAsync<TWindow>(key, null, options, activation, cancellationToken);

    private async ValueTask<TWindow> FindOrOpenCoreAsync<TWindow>(object key, object? parameter, RunicWindowOptions? options,
        DesktopActivationContext? activation, CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new()
    {
        ArgumentNullException.ThrowIfNull(key);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IRunicWindow? found;
            TaskCompletionSource<IRunicWindow>? opening = null;
            Task<IRunicWindow>? pending = null;
            lock (_gate)
            {
                found = Find(typeof(TWindow), key);
                if (found is null && !_opening.TryGetValue((typeof(TWindow), key), out pending))
                {
                    // Registered before the open starts, so a call meanwhile waits for this Window instead of opening another.
                    opening = new TaskCompletionSource<IRunicWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _opening[(typeof(TWindow), key)] = opening.Task;
                }
            }
            if (opening is not null)
            {
                // Outside the lock: opening resolves the ViewModel, which may use this service.
                IRunicWindow opened;
                try
                {
                    opened = await OpenCoreAsync<TWindow>(new RunicWindowOpenRequest(parameter, key, null), options, cancellationToken)
                        .ConfigureAwait(false);
                    opening.TrySetResult(opened);
                }
                catch (Exception error)
                {
                    if (error is OperationCanceledException) opening.TrySetCanceled(CancellationToken.None);
                    else opening.TrySetException(error);
                    throw;
                }
                finally
                {
                    lock (_gate) _opening.Remove((typeof(TWindow), key));
                }
                // A new Window is presented with the activation of a launch only; it is in front as it opens otherwise.
                if (activation is not null) _ = await opened.Presentation.PresentAsync(activation, cancellationToken).ConfigureAwait(false);
                return (TWindow)opened;
            }
            if (found is null)
            {
                try
                {
                    found = await pending!.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                // The call that opened was cancelled, not this one: look again, and open if nothing is there. A failed open
                // is this call's failure too.
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                // Opened, but already closing: look again.
                if (found.IsClosing) continue;
            }
            _ = await ActivateAsync(found, activation, cancellationToken).ConfigureAwait(false);
            return (TWindow)found;
        }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> ActivateAsync(IRunicWindow window, DesktopActivationContext? activation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var result = await window.Presentation.PresentAsync(activation, cancellationToken).ConfigureAwait(false);
        // The window-modal Windows that block it come forward after it, the innermost last, as a dialog's owner does.
        var blocked = window;
        for (var depth = 0; depth < 16 && ModalOf(blocked) is { } modal; depth++)
        {
            _ = await modal.Presentation.PresentAsync(null, cancellationToken).ConfigureAwait(false);
            blocked = modal;
        }
        return result;
    }

    private IRunicWindow? ModalOf(IRunicWindow owner) => Volatile.Read(ref _windows)
        .LastOrDefault(entry => entry.Modality == RunicWindowModality.Window && ReferenceEquals(entry.Owner, owner) && !entry.Window.IsClosing)?.Window;

    public ValueTask<RunicWindowResult<TResult>> OpenForResultAsync<TWindow, TParameter, TResult>(TParameter parameter,
        RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
        where TParameter : class
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return OpenForResultCoreAsync<TWindow, TResult>(parameter, options, cancellationToken);
    }

    public ValueTask<RunicWindowResult<TResult>> OpenForResultAsync<TWindow, TResult>(RunicWindowOptions? options,
        CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new() =>
        OpenForResultCoreAsync<TWindow, TResult>(null, options, cancellationToken);

    private async ValueTask<RunicWindowResult<TResult>> OpenForResultCoreAsync<TWindow, TResult>(object? parameter,
        RunicWindowOptions? options, CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new()
    {
        var slot = new RunicWindowResultSlot(typeof(TResult));
        var window = await OpenCoreAsync<TWindow>(new RunicWindowOpenRequest(parameter, null, slot), options, cancellationToken)
            .ConfigureAwait(false);
        await WaitForResultAsync(window, slot, cancellationToken).ConfigureAwait(false);
        return slot.Read<TResult>();
    }

    // Until the Window closed. A cancelled wait closes it without asking: nobody would receive its result.
    internal static async Task WaitForResultAsync(IRunicWindow window, RunicWindowResultSlot slot, CancellationToken cancellationToken)
    {
        try
        {
            await window.WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            slot.Closed();
            try { _ = await window.CloseAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
            throw;
        }
        slot.Closed();
    }

    private IRunicWindowHost Host<TWindow>() => Volatile.Read(ref _host) ?? throw new InvalidOperationException(
        $"No Runic host has opened a Window yet, so {typeof(TWindow).Name} cannot open. Open the first Window with " +
        "host.OpenWindowAsync<TWindow>() in RunicDesktopHost.Run or RunicCsWebUiHost.Run; ViewModels and services " +
        "can then open more with IRunicWindowService.");

    // A Window opened from a command usually has no owner that awaits it; closing it must still release its scope.
    // Disposing an already released Window does nothing.
    private async Task ReleaseWhenClosedAsync(IRunicWindow window)
    {
        try { await window.WaitForCloseAsync().ConfigureAwait(false); }
        catch (Exception error) { ReleaseFailed(window, error); }
        try { await window.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { ReleaseFailed(window, error); }
    }

    private ILogger Logger => services.GetService<ILoggerFactory>()?.CreateLogger(RunicViewsTelemetry.LogCategory)
        ?? TraceFallbackLogger.Instance;

    private void ReleaseFailed(IRunicWindow window, Exception error) =>
        ViewsLog.WindowReleaseFailed(Logger, error, window.GetType().Name, error.GetType().Name);
}
