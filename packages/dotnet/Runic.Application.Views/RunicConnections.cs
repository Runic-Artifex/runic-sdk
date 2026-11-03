using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>
/// One page connection of a Window: a browser tab, a WebView or a reloaded page, from its <c>hello</c> until it closes.
/// </summary>
/// <remarks>
/// Both keys come from the window's transport, which authenticated the connection, never from the page's requests, so a
/// page cannot claim another page's keys. They are opaque: compare them, keep them as dictionary keys, but do not parse
/// them or show them to users. A reload is a new connection with a new <see cref="ConnectionKey"/>. On CS-WebUI the
/// <see cref="ClientKey"/> is WebUI's client id; on Runic Desktop it identifies the window's page.
/// </remarks>
/// <param name="ConnectionKey">The connection's key, unique among the Window's connections.</param>
/// <param name="ClientKey">The browser client's key. Several connections can share one, as a page's reconnects do.</param>
public sealed record RunicConnection(string ConnectionKey, string ClientKey);

/// <summary>The connection that <see cref="IRunicConnections.Connected"/> or <see cref="IRunicConnections.Disconnected"/> reports.</summary>
/// <param name="connection">The connection.</param>
public sealed class RunicConnectionEventArgs(RunicConnection connection) : EventArgs
{
    /// <summary>The connection that connected or disconnected.</summary>
    public RunicConnection Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));
}

/// <summary>
/// The page connections of the Window whose DI scope resolved the service, and the connection whose request runs now.
/// Take it in a ViewModel's constructor, for example to show who else is viewing a multi-client CS-WebUI window.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope.
/// <c>OpenWindowAsync</c> binds it to the Window it opens when the Window's Bridge attaches, on Runic Desktop and
/// CS-WebUI; <c>RunicWindowTestHost.Create</c> binds it to the test window. Resolved anywhere else, for example in a
/// WPF <c>RunicViewHost</c>, which creates no Window scope, it lists no connections and raises no events.
/// </para>
/// <para>
/// A connection is listed once its page completed the Views protocol <c>hello</c>, and removed when the transport reports
/// it closed. <see cref="Current"/> changes and the events are raised in a turn of the Window's model context, as a
/// command body runs, so a handler may change bound state. The turn runs in no invocation: a handler has no
/// <see cref="Caller"/>, whatever reported the change. A connection the window already had when the service was bound
/// is reported with <see cref="Connected"/> too. A connection that closes before its <see cref="Connected"/> turn runs
/// is never reported. Once the Window's model context takes no more turns, because the Window is closing,
/// <see cref="Current"/> still follows its connections but no event is raised; closing the Window raises no
/// <see cref="Disconnected"/>.
/// </para>
/// <para>
/// A CS-WebUI window with WebUI's <c>MultiClient</c> configuration can have several connections; Runic Desktop's
/// window has one page connection, a new one after each reload. Only the page decides who connects; see the CS-WebUI
/// package's security notes before letting several clients share a window.
/// </para>
/// </remarks>
public interface IRunicConnections
{
    /// <summary>The Window's open connections, in the order they connected. Each read returns a snapshot.</summary>
    IReadOnlyList<RunicConnection> Current { get; }

    /// <summary>
    /// The connection whose command, operation or in-page menu click of this Window runs on the current asynchronous
    /// flow, or <see langword="null"/> outside one, such as in a timer, the window scope or another Window's command.
    /// </summary>
    /// <remarks>
    /// It reads <see cref="RunicInteractionInvocation.Current"/>, which only the Bridge runtime enters, with the identity
    /// the transport authenticated for the request. Application code cannot set it. It stays the same through the
    /// command's <see langword="await"/>s, also after the connection closed, until the command ends: a timer,
    /// <see cref="Task.Run(Action)"/> or loop that the command started and that runs on after it reads
    /// <see langword="null"/>. Long-lived work of the Window enters <see cref="RunicInteractionInvocation.EnterWindow"/>.
    /// A connection key is never reused within a Window, so a command whose connection closed never matches a later
    /// page, also when the transport reused its key.
    /// </remarks>
    RunicConnection? Caller { get; }

    /// <summary>Raised in a model turn when a page connection completes its <c>hello</c>.</summary>
    event EventHandler<RunicConnectionEventArgs>? Connected;

    /// <summary>Raised in a model turn when a connection that <see cref="Connected"/> reported closes.</summary>
    event EventHandler<RunicConnectionEventArgs>? Disconnected;
}

/// <summary>Observable forms of <see cref="IRunicConnections"/>, for ReactiveUI and other <see cref="IObservable{T}"/> consumers.</summary>
public static class RunicConnectionsExtensions
{
    /// <summary>
    /// Emits <see cref="IRunicConnections.Current"/> when subscribed and again after each connection that connects or
    /// disconnects, on the thread that raised the event: a turn of the Window's model context.
    /// </summary>
    /// <remarks>
    /// It needs no System.Reactive, and works with both ReactiveUI flavors:
    /// <code>
    /// _viewers = connections.WhenChanged().Select(list =&gt; list.Count).ToProperty(this, vm =&gt; vm.Viewers);
    /// </code>
    /// Dispose the subscription to stop it.
    /// </remarks>
    /// <param name="connections">The Window's connections.</param>
    /// <returns>The connections after each change.</returns>
    public static IObservable<IReadOnlyList<RunicConnection>> WhenChanged(this IRunicConnections connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return new ChangedObservable(connections);
    }

    private sealed class ChangedObservable(IRunicConnections connections) : IObservable<IReadOnlyList<RunicConnection>>
    {
        public IDisposable Subscribe(IObserver<IReadOnlyList<RunicConnection>> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var subscription = new Subscription(connections, observer);
            connections.Connected += subscription.Changed;
            connections.Disconnected += subscription.Changed;
            observer.OnNext(connections.Current);
            return subscription;
        }
    }

    private sealed class Subscription(IRunicConnections connections, IObserver<IReadOnlyList<RunicConnection>> observer) : IDisposable
    {
        private int _disposed;

        public void Changed(object? sender, RunicConnectionEventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 0) observer.OnNext(connections.Current);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            connections.Connected -= Changed;
            connections.Disconnected -= Changed;
        }
    }
}

// The IRunicConnections of one Window scope. OpenWindowAsync (through RunicWindowOpening.AttachBridge) and the test host
// bind it to the Window's content session; it then follows the session dispatcher's connections. Unbound, it is empty.
internal sealed class RunicConnections : IRunicConnections, IDisposable
{
    private readonly object _gate = new();
    private WindowContentSession? _session;
    private IDisposable? _lease;
    private bool _disposed;
    // Changed only in the model turns that raise the events, so Current and the events agree.
    private RunicConnection[] _current = [];

    public IReadOnlyList<RunicConnection> Current => Volatile.Read(ref _current);

    public RunicConnection? Caller =>
        RunicInteractionInvocation.Current is { ConnectionKey: { } connectionKey, ClientKey: { } clientKey } invocation
        && Volatile.Read(ref _session) is { } session && ReferenceEquals(invocation.Session, session)
            ? Find(connectionKey) ?? new RunicConnection(connectionKey, clientKey)
            : null;

    public event EventHandler<RunicConnectionEventArgs>? Connected;
    public event EventHandler<RunicConnectionEventArgs>? Disconnected;

    // Binds the service to a window's session once. False when it is bound to another session or disposed; binding it
    // again to the same session does nothing.
    internal bool Bind(WindowContentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<BridgeConnection> existing;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_session is { } bound) return ReferenceEquals(bound, session);
            _lease = session.Dispatcher.ObserveConnections(Opened, Closed, out existing);
            Volatile.Write(ref _session, session);
        }
        foreach (var connection in existing) Opened(connection);
        return true;
    }

    // The window's own connection for a key and client, while Connected reported it and Disconnected has not: the
    // per-connection state adds a value only then, so every value it adds has a Disconnected still to come. Null for a
    // key the window does not list, or a client key that is not the connection's.
    internal RunicConnection? FindListed(RunicConnection connection) =>
        Find(connection.ConnectionKey) is { } own && own.ClientKey == connection.ClientKey
        && Volatile.Read(ref _session)?.Dispatcher.FindConnection(own.ConnectionKey) is { IsOpen: true }
            ? own
            : null;

    internal ILogger Logger => Volatile.Read(ref _session)?.Logger ?? TraceFallbackLogger.Instance;

    // The session the service follows, or null while unbound.
    internal WindowContentSession? Session => Volatile.Read(ref _session);

    private RunicConnection? Find(string connectionKey)
    {
        foreach (var connection in Volatile.Read(ref _current))
            if (connection.ConnectionKey == connectionKey) return connection;
        return null;
    }

    // Each change returns null when there is nothing to report, or the notification that raises its event.
    private void Opened(BridgeConnection connection) =>
        Post(() =>
        {
            // A connection that closed before this turn is not reported: its Closed turn found nothing to remove.
            if (!connection.IsOpen || Find(connection.Key) is not null) return null;
            var added = new RunicConnection(connection.Key, connection.ClientKey);
            Volatile.Write(ref _current, [.. Volatile.Read(ref _current), added]);
            return raise => { if (raise) Raise(Connected, added, "Connected"); };
        });

    private void Closed(string connectionKey) =>
        Post(() =>
        {
            if (Find(connectionKey) is not { } removed) return null;
            Volatile.Write(ref _current, [.. Volatile.Read(ref _current).Where(item => item.ConnectionKey != connectionKey)]);
            return raise => { if (raise) Raise(Disconnected, removed, "Disconnected"); };
        });

    // Runs a change, and raises its event, in a turn of the window's model context, in order. The turn does not flow the
    // reporting thread's execution context, so a handler never runs in the invocation of a request that happened to
    // report the change. A window whose context no longer takes turns (it is closing), or has none, applies the change
    // on the reporting thread, one at a time, and raises no event: handlers only ever run in a model turn.
    private void Post(Func<Action<bool>?> change)
    {
        var session = Volatile.Read(ref _session);
        if (session?.ModelContext is { } context)
        {
            bool posted;
            using (ExecutionContext.SuppressFlow())
                posted = context.TryPost(() => Apply(change, raise: true));
            if (posted) return;
        }
        Apply(change, raise: false);
    }

    private void Apply(Func<Action<bool>?> change, bool raise)
    {
        Action<bool>? notify;
        lock (_gate) notify = change();
        notify?.Invoke(raise);
    }

    private void Raise(EventHandler<RunicConnectionEventArgs>? handlers, RunicConnection connection, string eventName)
    {
        if (handlers is null) return;
        var args = new RunicConnectionEventArgs(connection);
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((EventHandler<RunicConnectionEventArgs>)handler)(this, args); }
            catch (Exception error)
            {
                ViewsLog.ConnectionHandlerFailed(Volatile.Read(ref _session)?.Logger ?? TraceFallbackLogger.Instance, error,
                    eventName, error.GetType().FullName ?? error.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        IDisposable? lease;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            lease = _lease;
            _lease = null;
        }
        lease?.Dispose();
    }
}

/// <summary>
/// A value per connection of a Window, such as a selection, a cursor or a draft that each page keeps for itself, removed
/// when its connection disconnects.
/// </summary>
/// <remarks>
/// <para>
/// Create it in a ViewModel's constructor from the Window's <see cref="IRunicConnections"/>, and read the caller's value
/// in a command with <see cref="TryGetCaller"/>:
/// <code>
/// _drafts = new RunicConnectionState&lt;Draft&gt;(connections, _ =&gt; new Draft());
/// [RelayCommand]
/// private void Type(string text) { if (_drafts.TryGetCaller(out var draft)) draft.Text = text; }
/// </code>
/// </para>
/// <para>
/// A value is created on first use, only for a connection that <see cref="IRunicConnections.Current"/> lists, so that
/// connection's <see cref="IRunicConnections.Disconnected"/> is still to come; it then removes the value and disposes it
/// when it is <see cref="IDisposable"/>. <see cref="Dispose"/> stops it and disposes every value; dispose it with the
/// ViewModel. A value whose <c>Dispose</c> throws is logged as event 1231 <c>ConnectionStateDisposeFailed</c>, and the
/// others are still disposed. A closing Window raises no <see cref="IRunicConnections.Disconnected"/>, so its values
/// stay until <see cref="Dispose"/>.
/// </para>
/// <para>
/// It is thread-safe. The factory runs under its lock: it must not call <see cref="TryGet"/> or
/// <see cref="TryGetCaller"/> of the same state, which would create the value again.
/// </para>
/// </remarks>
/// <typeparam name="T">The value of one connection.</typeparam>
public sealed class RunicConnectionState<T> : IDisposable
{
    private readonly IRunicConnections _connections;
    private readonly Func<RunicConnection, T> _create;
    private readonly Dictionary<string, (RunicConnection Connection, T Value)> _values = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Keeps a value for each connection of <paramref name="connections"/>, created with <paramref name="create"/>.</summary>
    /// <param name="connections">The Window's connections.</param>
    /// <param name="create">
    /// Creates the value of a connection on first use. It receives the Window's own <see cref="RunicConnection"/>.
    /// </param>
    public RunicConnectionState(IRunicConnections connections, Func<RunicConnection, T> create)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        connections.Disconnected += OnDisconnected;
    }

    /// <summary>
    /// Gets the value of <see cref="IRunicConnections.Caller"/>, creating it on first use, while the Window lists that
    /// connection.
    /// </summary>
    /// <param name="value">The caller's value.</param>
    /// <returns>
    /// <see langword="false"/> outside a command of this Window, after the command ended, or when the caller's connection
    /// is not listed, for example because it closed.
    /// </returns>
    public bool TryGetCaller([MaybeNullWhen(false)] out T value)
    {
        if (_connections.Caller is { } caller) return TryGet(caller, out value);
        value = default;
        return false;
    }

    /// <summary>
    /// Gets the value of <paramref name="connection"/>, creating it on first use, while the Window lists the connection.
    /// </summary>
    /// <param name="connection">
    /// A connection of the Window, such as one from <see cref="IRunicConnections.Current"/>. Both keys must be the
    /// Window's: a <see cref="RunicConnection"/> with the right connection key and another client key gets nothing.
    /// </param>
    /// <param name="value">The connection's value.</param>
    /// <returns>
    /// <see langword="false"/> when the Window does not list the connection with these keys, or the state was disposed.
    /// </returns>
    public bool TryGet(RunicConnection connection, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_values)
        {
            if (!_disposed && _values.TryGetValue(connection.ConnectionKey, out var existing)
                && existing.Connection.ClientKey == connection.ClientKey)
            {
                value = existing.Value;
                return true;
            }
            // Checked under the lock that Disconnected takes: the connection is listed, so its Disconnected, which removes
            // the value, has not run and is still to come.
            if (_disposed || _values.ContainsKey(connection.ConnectionKey) || Listed(connection) is not { } own)
            {
                value = default;
                return false;
            }
            value = _create(own);
            _values.Add(own.ConnectionKey, (own, value));
            return true;
        }
    }

    /// <summary>The values that exist now, by connection key.</summary>
    public IReadOnlyDictionary<string, T> Values
    {
        get
        {
            lock (_values)
                return _values.ToDictionary(static pair => pair.Key, static pair => pair.Value.Value, StringComparer.Ordinal);
        }
    }

    // The Window's own connection with these keys, if it lists it.
    private RunicConnection? Listed(RunicConnection connection) =>
        _connections is RunicConnections window
            ? window.FindListed(connection)
            : _connections.Current.FirstOrDefault(item => item == connection);

    private ILogger Logger => (_connections as RunicConnections)?.Logger ?? TraceFallbackLogger.Instance;

    private void OnDisconnected(object? sender, RunicConnectionEventArgs e)
    {
        (RunicConnection Connection, T Value) removed;
        lock (_values)
            if (!_values.Remove(e.Connection.ConnectionKey, out removed)) return;
        DisposeValue(removed.Value);
    }

    private void DisposeValue(T value)
    {
        if (value is not IDisposable disposable) return;
        try { disposable.Dispose(); }
        catch (Exception error)
        {
            ViewsLog.ConnectionStateDisposeFailed(Logger, error, typeof(T).Name, error.GetType().FullName ?? error.GetType().Name);
        }
    }

    /// <summary>Stops following the connections and disposes the values that are <see cref="IDisposable"/>.</summary>
    public void Dispose()
    {
        T[] values;
        lock (_values)
        {
            if (_disposed) return;
            _disposed = true;
            values = [.. _values.Values.Select(static entry => entry.Value)];
            _values.Clear();
        }
        _connections.Disconnected -= OnDisconnected;
        foreach (var value in values) DisposeValue(value);
    }
}
