namespace Runic.Application.Views;

/// <summary>
/// The application's locale: the language that every Window shows unless it sets its own with
/// <see cref="IRunicWindowLocale"/>, and that menu bars, status icons and notifications are built in. One
/// <see cref="SetLocaleAsync"/> reaches every open Window and every Window opened later.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it as a singleton. Its locale is
/// <see langword="null"/> until the application sets one, and each page then keeps its own language, as before
/// application locales existed.
/// </para>
/// <para>
/// A switch runs in this order: the followers that <see cref="Follow"/> registered, such as the application's
/// Translations context, prepare the new locale; then the locale changes, every Window that sets no locale of its own
/// follows it (its pages switch, and its <see cref="IRunicWindowLocale"/> raises <c>PropertyChanged</c>); then
/// <see cref="LocaleChanged"/> is raised, so a handler that rebuilds the menu bar or a status icon reads text that is
/// already in the new language. A typical start, on the UI thread, so the handler runs there too:
/// </para>
/// <code>
/// var locale = services.GetRequiredService&lt;IRunicApplicationLocale&gt;();
/// locale.Follow(translations.SetLocaleAsync);           // a Runic Translations TranslationContext
/// locale.LocaleChanged += (_, _) =&gt; RebuildMenuBar();
/// await locale.SetLocaleAsync(settings.Language ?? Negotiate(DesktopLanguages.Preferred));
/// </code>
/// <para>
/// Threads: each Window follows in a turn of its own model context (<see cref="WindowContentSession.ModelContext"/>),
/// as other changes of a Window reach its ViewModel, so its <see cref="IRunicWindowLocale"/> raises
/// <c>PropertyChanged</c> there; a Window without a model context, or a scope whose Window has not opened, follows on
/// the thread of the switch. Each <see cref="LocaleChanged"/> handler runs on the <see cref="SynchronizationContext"/>
/// that was current when it subscribed, such as the UI thread or a ViewModel's model context, and on the thread of the
/// switch when there was none.
/// </para>
/// <para>
/// In hosted mode (another Runic application shows this application's Windows, <c>RunicDesktopHostOptions.Guest</c>), the
/// locale follows the host application's until this application sets one of its own with <see cref="SetLocaleAsync"/>;
/// setting <see langword="null"/> follows the host's again.
/// </para>
/// <para>
/// Applications and tests may implement this interface. A minor release adds members to it only with a default
/// implementation.
/// </para>
/// </remarks>
public interface IRunicApplicationLocale
{
    /// <summary>
    /// The application's locale, a BCP 47 tag such as <c>de</c> or <c>pt-BR</c>, or <see langword="null"/> while the
    /// application sets none.
    /// </summary>
    string? Locale { get; }

    /// <summary>
    /// Changes the application's locale: its followers first, then every Window without a locale of its own, then
    /// <see cref="LocaleChanged"/>. Switches run one at a time, in the order they were requested, and their events are
    /// raised in that order; setting the current locale does nothing, except for Windows that failed to follow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> calls no follower, which keeps its locale, and tells the Windows that follow the
    /// application that it sets none: their pages keep the language they show. A follower that throws stops the switch:
    /// the locale, the Windows and the followers after it keep the old locale, followers before it keep the new one, and
    /// the exception is rethrown.
    /// </para>
    /// <para>
    /// Once the followers succeeded, the switch completes: a Window whose update throws, for example because a
    /// <c>PropertyChanged</c> handler of its ViewModel throws, does not stop the other Windows or the event, and a
    /// <see cref="LocaleChanged"/> handler that throws does not stop the other handlers. After the event, the call throws
    /// an <see cref="AggregateException"/> with every such failure; the locale has changed. Calling it again with the
    /// same locale updates the Windows that failed again, and raises no event.
    /// </para>
    /// <para>
    /// When switches overlap, <see cref="LocaleChanged"/> reports each change once, in order: a switch that a later
    /// switch followed before its event was raised raises none, and the next event's
    /// <see cref="RunicApplicationLocaleChangedEventArgs.OldLocale"/> is the locale the previous event reported. A
    /// <see cref="LocaleChanged"/> handler may switch again; that call returns when the Windows followed, and its event
    /// is raised after the current handlers. A follower, or a Window's <c>PropertyChanged</c> handler during a switch,
    /// must not: the switch it would wait for is its own, so the call throws <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    /// <param name="locale">The new locale, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting for an earlier switch, and is passed to the followers.</param>
    /// <exception cref="ArgumentException">The value is not 1 to 64 letters, digits and <c>-</c>, starting with a letter.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the locale changed.</exception>
    /// <exception cref="AggregateException">The locale changed, but a Window or a <see cref="LocaleChanged"/> handler failed.</exception>
    /// <exception cref="InvalidOperationException">A follower or a Window's <c>PropertyChanged</c> handler of a running switch called it.</exception>
    ValueTask SetLocaleAsync(string? locale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a follower that prepares each new locale before the Windows and <see cref="LocaleChanged"/> see it, such
    /// as <c>TranslationContext.SetLocaleAsync</c> of Runic Translations: <c>locale.Follow(context.SetLocaleAsync)</c>.
    /// </summary>
    /// <remarks>
    /// Followers run in the order they were registered, each awaited before the next, and only for a locale that is not
    /// <see langword="null"/>. Registering one does not call it: register it before the first
    /// <see cref="SetLocaleAsync"/>, or set its locale yourself.
    /// </remarks>
    /// <param name="follower">Receives the new locale and the switch's cancellation token.</param>
    /// <returns>A registration; dispose it to stop following.</returns>
    IDisposable Follow(Func<string, CancellationToken, ValueTask> follower);

    /// <summary>
    /// Raised after the locale changed and the Windows followed it. Rebuild the application's own text here: the menu
    /// bar, status icons and notifications.
    /// </summary>
    /// <remarks>
    /// Each handler runs on the <see cref="SynchronizationContext"/> that was current when it subscribed: subscribe on
    /// the UI thread, and the handler runs there. A handler subscribed without one, as in a console start or a test, runs
    /// on the thread that completes the switch, usually a thread-pool thread, and marshals UI work itself. The handlers
    /// run one after another, in the order they subscribed, and <see cref="SetLocaleAsync"/> returns after they ran, so
    /// do not block a handler's thread waiting for that call.
    /// </remarks>
    event EventHandler<RunicApplicationLocaleChangedEventArgs>? LocaleChanged;
}

/// <summary>The change that <see cref="IRunicApplicationLocale.LocaleChanged"/> reports.</summary>
/// <param name="oldLocale">The locale before the change.</param>
/// <param name="locale">The new locale.</param>
public sealed class RunicApplicationLocaleChangedEventArgs(string? oldLocale, string? locale) : EventArgs
{
    /// <summary>The locale before the change, or <see langword="null"/>.</summary>
    public string? OldLocale { get; } = oldLocale;

    /// <summary>The new locale, or <see langword="null"/> when the application sets none.</summary>
    public string? Locale { get; } = locale;
}

// The IRunicApplicationLocale that AddRunicWindowService registers. Window contexts follow it through Attach rather than
// the public event, so every Window has followed before the application's own handlers run.
internal sealed class RunicApplicationLocale : IRunicApplicationLocale, IDisposable
{
    // The switch whose followers run in the calling flow, so that a follower calling SetLocaleAsync is detected. It flows
    // into work a follower starts; Active turns false when its switch is done, so that work may switch later.
    private static readonly AsyncLocal<FollowerFlow?> Following = new();
    // The locale whose switch updates a Window on this thread (its PropertyChanged handlers run), or whose LocaleChanged
    // handler runs on this thread.
    [ThreadStatic] private static RunicApplicationLocale? s_updatingWindow;
    [ThreadStatic] private static RunicApplicationLocale? s_raising;

    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _gate = new();
    private Func<string, CancellationToken, ValueTask>[] _followers = [];
    private RunicWindowContext[] _windows = [];
    private Subscription[] _handlers = [];
    // The Windows whose update failed in the last switch; a switch to the same locale updates them again. Under _switching.
    private RunicWindowContext[] _failed = [];
    private string? _locale;
    // Committed switches whose LocaleChanged is not raised yet, in commit order, and the locale the last event reported.
    // Under _gate.
    private readonly List<PendingEvent> _pending = [];
    private bool _raisingEvents;
    private string? _reported;

    public string? Locale => Volatile.Read(ref _locale);

    // Each handler runs on the SynchronizationContext that was current when it subscribed.
    public event EventHandler<RunicApplicationLocaleChangedEventArgs>? LocaleChanged
    {
        add
        {
            if (value is null) return;
            var subscription = new Subscription(value, SynchronizationContext.Current);
            lock (_gate) _handlers = [.. _handlers, subscription];
        }
        remove
        {
            if (value is null) return;
            lock (_gate)
            {
                var index = Array.FindLastIndex(_handlers, item => item.Handler.Equals(value));
                if (index >= 0) _handlers = [.. _handlers[..index], .. _handlers[(index + 1)..]];
            }
        }
    }

    public async ValueTask SetLocaleAsync(string? locale, CancellationToken cancellationToken = default) =>
        _ = await SwitchAsync(locale, fromHost: false, cancellationToken).ConfigureAwait(false);

    // Hosted mode (runic-host/1, host-protocol.md, Appearance and locale): the host's locale, which the application follows
    // unless it set a locale of its own with SetLocaleAsync; setting null gives its own locale up. Returns whether the
    // locale followed the host's.
    internal ValueTask<bool> FollowHostAsync(string? locale, CancellationToken cancellationToken = default) =>
        SwitchAsync(locale, fromHost: true, cancellationToken);

    // Whether the application set a locale of its own (SetLocaleAsync with a locale). Under _switching.
    private bool _ownLocale;

    // Whether the application set a locale of its own, which a host's locale does not replace.
    internal bool HasOwnLocale => Volatile.Read(ref _ownLocale);

    // Hosted mode: the host's current locale, which SetLocaleAsync(null) switches to directly, so giving the application's
    // own locale up switches once, not to no locale and then to the host's. Null outside hosted mode.
    internal Func<string?>? HostLocale { get; set; }

    private async ValueTask<bool> SwitchAsync(string? locale, bool fromHost, CancellationToken cancellationToken)
    {
        if (locale is not null && !BridgeDispatcher.IsLocale(locale))
            throw new ArgumentException($"'{locale}' is not a locale tag, such as \"de\" or \"pt-BR\".", nameof(locale));
        if ((Following.Value is { Active: true } flow && ReferenceEquals(flow.Owner, this)) || ReferenceEquals(s_updatingWindow, this))
            throw new InvalidOperationException(
                "IRunicApplicationLocale.SetLocaleAsync was called by a follower, or by a Window's PropertyChanged handler, of a " +
                "switch that is still running, and would wait for that switch to finish. Change the locale from a LocaleChanged " +
                "handler or after SetLocaleAsync returned.");
        // A LocaleChanged handler that switches again: its event is raised after the current handlers.
        var fromHandler = ReferenceEquals(s_raising, this);
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        PendingEvent? pending = null;
        List<Exception>? errors = null;
        try
        {
            if (fromHost && _ownLocale) return false;
            if (!fromHost) Volatile.Write(ref _ownLocale, locale is not null);
            // Giving the own locale up in hosted mode follows the host's at once.
            if (!fromHost && locale is null && HostLocale?.Invoke() is { } host && BridgeDispatcher.IsLocale(host)) locale = host;
            var old = Volatile.Read(ref _locale);
            if (string.Equals(old, locale, StringComparison.Ordinal))
            {
                // The Windows that failed to follow this locale get it again; the locale did not change, so no event.
                RunicWindowContext[] retry;
                lock (_gate) retry = [.. _failed.Where(window => _windows.Contains(window))];
                _failed = [];
                if (retry.Length > 0) (errors, _failed) = await UpdateWindowsAsync(retry, retry: true).ConfigureAwait(false);
            }
            else
            {
                if (locale is not null) await RunFollowersAsync(locale, cancellationToken).ConfigureAwait(false);
                RunicWindowContext[] windows;
                lock (_gate)
                {
                    Volatile.Write(ref _locale, locale);
                    windows = _windows;
                }
                (errors, _failed) = await UpdateWindowsAsync(windows, retry: false).ConfigureAwait(false);
                pending = new PendingEvent(locale);
                lock (_gate) _pending.Add(pending);
            }
        }
        finally
        {
            _switching.Release();
        }
        if (pending is not null && await RaiseAsync(pending, fromHandler).ConfigureAwait(false) is { } handlerErrors)
            (errors ??= []).AddRange(handlerErrors);
        if (errors is not null) throw new AggregateException(
            "The application's locale changed, but a Window or a LocaleChanged handler failed: " + errors[0].Message, errors);
        return true;
    }

    private async ValueTask RunFollowersAsync(string locale, CancellationToken cancellationToken)
    {
        var flow = new FollowerFlow(this);
        Following.Value = flow;
        try
        {
            foreach (var follower in Volatile.Read(ref _followers))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await follower(locale, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            flow.Active = false;
            Following.Value = null;
        }
    }

    // Updates every Window, each on its model context and all at once, and collects the failures and the Windows that
    // failed.
    private async ValueTask<(List<Exception>? Errors, RunicWindowContext[] Failed)> UpdateWindowsAsync(RunicWindowContext[] windows, bool retry)
    {
        var updates = new Task[windows.Length];
        for (var index = 0; index < windows.Length; index++) updates[index] = windows[index].FollowApplicationAsync(this, retry).AsTask();
        List<Exception>? errors = null;
        List<RunicWindowContext>? failed = null;
        for (var index = 0; index < windows.Length; index++)
        {
            try { await updates[index].ConfigureAwait(false); }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
                (failed ??= []).Add(windows[index]);
            }
        }
        return (errors, failed is null ? [] : [.. failed]);
    }

    // Runs a Window's update, whose PropertyChanged handlers must not switch again.
    internal static void UpdateWindow(RunicApplicationLocale owner, Action update)
    {
        var outer = s_updatingWindow;
        s_updatingWindow = owner;
        try { update(); }
        finally { s_updatingWindow = outer; }
    }

    // Raises the pending events in commit order. The caller that finds nobody raising raises every event pending until
    // none is left; a switch that a later one committed after it, before its event was raised, raises none. Returns the
    // failures of the handlers of this switch's event.
    private async ValueTask<List<Exception>?> RaiseAsync(PendingEvent pending, bool fromHandler)
    {
        bool raise;
        lock (_gate)
        {
            raise = !_raisingEvents;
            _raisingEvents = true;
        }
        if (raise) await RaisePendingAsync().ConfigureAwait(false);
        // A handler's own switch is raised after that handler returned; waiting for it here would wait for itself.
        if (fromHandler && !pending.Raised.Task.IsCompleted) return null;
        return await pending.Raised.Task.ConfigureAwait(false);
    }

    private async Task RaisePendingAsync()
    {
        while (true)
        {
            PendingEvent[] batch;
            string? old;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _raisingEvents = false;
                    return;
                }
                batch = [.. _pending];
                _pending.Clear();
                old = _reported;
                _reported = batch[^1].Locale;
            }
            // Superseded switches report nothing; the latest reports the change since the last event.
            foreach (var superseded in batch[..^1]) superseded.Raised.TrySetResult(null);
            var latest = batch[^1];
            if (string.Equals(old, latest.Locale, StringComparison.Ordinal))
            {
                latest.Raised.TrySetResult(null);
                continue;
            }
            List<Exception>? errors;
            try { errors = await InvokeHandlersAsync(new RunicApplicationLocaleChangedEventArgs(old, latest.Locale)).ConfigureAwait(false); }
            catch (Exception error) { errors = [error]; }
            latest.Raised.TrySetResult(errors);
        }
    }

    private async ValueTask<List<Exception>?> InvokeHandlersAsync(RunicApplicationLocaleChangedEventArgs args)
    {
        Subscription[] handlers;
        lock (_gate) handlers = _handlers;
        List<Exception>? errors = null;
        foreach (var subscription in handlers)
        {
            Exception? error;
            if (subscription.Context is not { } context || ReferenceEquals(context, SynchronizationContext.Current))
                error = Invoke(subscription.Handler, args);
            else
            {
                var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    context.Post(_ => done.TrySetResult(Invoke(subscription.Handler, args)), null);
                    error = await done.Task.ConfigureAwait(false);
                }
                catch (Exception posting) { error = posting; }
            }
            if (error is not null) (errors ??= []).Add(error);
        }
        return errors;
    }

    private Exception? Invoke(EventHandler<RunicApplicationLocaleChangedEventArgs> handler, RunicApplicationLocaleChangedEventArgs args)
    {
        var outer = s_raising;
        s_raising = this;
        try
        {
            handler(this, args);
            return null;
        }
        catch (Exception error) { return error; }
        finally { s_raising = outer; }
    }

    public IDisposable Follow(Func<string, CancellationToken, ValueTask> follower)
    {
        ArgumentNullException.ThrowIfNull(follower);
        lock (_gate) Volatile.Write(ref _followers, [.. _followers, follower]);
        return new Registration(this, follower);
    }

    // A Window context that follows the application from now on; returns the locale it follows. Under the gate, so a
    // switch that races it either reaches it or is the locale it returns.
    internal string? Attach(RunicWindowContext window)
    {
        lock (_gate)
        {
            if (!_windows.Contains(window)) _windows = [.. _windows, window];
            return _locale;
        }
    }

    internal void Detach(RunicWindowContext window)
    {
        lock (_gate) _windows = [.. _windows.Where(item => !ReferenceEquals(item, window))];
    }

    // The service provider disposes the singleton with the application; a switch after that throws ObjectDisposedException.
    public void Dispose() => _switching.Dispose();

    private sealed record Subscription(EventHandler<RunicApplicationLocaleChangedEventArgs> Handler, SynchronizationContext? Context);

    private sealed class PendingEvent(string? locale)
    {
        public string? Locale { get; } = locale;

        // Completes with the failures of the event's handlers, or null when it raised none or no event.
        public TaskCompletionSource<List<Exception>?> Raised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FollowerFlow(RunicApplicationLocale owner)
    {
        private volatile bool _active = true;

        public RunicApplicationLocale Owner { get; } = owner;

        public bool Active
        {
            get => _active;
            set => _active = value;
        }
    }

    private sealed class Registration(RunicApplicationLocale owner, Func<string, CancellationToken, ValueTask> follower) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                var followers = owner._followers.ToList();
                followers.Remove(follower);
                Volatile.Write(ref owner._followers, [.. followers]);
            }
        }
    }
}
