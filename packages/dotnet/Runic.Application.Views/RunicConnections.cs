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
/// command body runs, so a handler may change bound state. A connection the window already had when the service was
/// bound is reported with <see cref="Connected"/> too. Closing the Window raises no <see cref="Disconnected"/>.
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
    /// command's <see langword="await"/>s and in the work it starts on the same flow, also after the connection closed.
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

    // Whether the window's dispatcher still has the connection open; the per-connection state checks it before it adds
    // a value, so a value is never added after the connection's Disconnected.
    internal bool IsOpen(string connectionKey) =>
        Volatile.Read(ref _session)?.Dispatcher.FindConnection(connectionKey) is { IsOpen: true };

    private RunicConnection? Find(string connectionKey)
    {
        foreach (var connection in Volatile.Read(ref _current))
            if (connection.ConnectionKey == connectionKey) return connection;
        return null;
    }

    private void Opened(BridgeConnection connection) =>
        Post(() =>
        {
            // A connection that closed before this turn is not reported: its Closed turn found nothing to remove.
            if (!connection.IsOpen || Find(connection.Key) is not null) return;
            var added = new RunicConnection(connection.Key, connection.ClientKey);
            Volatile.Write(ref _current, [.. Volatile.Read(ref _current), added]);
            Raise(Connected, added, "Connected");
        });

    private void Closed(string connectionKey) =>
        Post(() =>
        {
            if (Find(connectionKey) is not { } removed) return;
            Volatile.Write(ref _current, [.. Volatile.Read(ref _current).Where(item => item.ConnectionKey != connectionKey)]);
            Raise(Disconnected, removed, "Disconnected");
        });

    // Runs a change in a turn of the window's model context, in order. A window whose context no longer takes turns, or
    // has none, runs it on the reporting thread, one change at a time.
    private void Post(Action change)
    {
        var session = Volatile.Read(ref _session);
        if (session?.ModelContext is { } context && context.TryPost(change)) return;
        lock (_gate) change();
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
/// A value is created on first use, only for a connection that is open, and is removed, and disposed when it is
/// <see cref="IDisposable"/>, when the connection's <see cref="IRunicConnections.Disconnected"/> is raised.
/// <see cref="Dispose"/> stops it and disposes every value; dispose it with the ViewModel. It is thread-safe.
/// </para>
/// </remarks>
/// <typeparam name="T">The value of one connection.</typeparam>
public sealed class RunicConnectionState<T> : IDisposable
{
    private readonly IRunicConnections _connections;
    private readonly Func<RunicConnection, T> _create;
    private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Keeps a value for each connection of <paramref name="connections"/>, created with <paramref name="create"/>.</summary>
    /// <param name="connections">The Window's connections.</param>
    /// <param name="create">Creates the value of a connection on first use.</param>
    public RunicConnectionState(IRunicConnections connections, Func<RunicConnection, T> create)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        connections.Disconnected += OnDisconnected;
    }

    /// <summary>
    /// Gets the value of <see cref="IRunicConnections.Caller"/>, creating it on first use, while that connection is open.
    /// </summary>
    /// <param name="value">The caller's value.</param>
    /// <returns><see langword="false"/> outside a command of this Window, or when the caller's connection closed.</returns>
    public bool TryGetCaller([MaybeNullWhen(false)] out T value)
    {
        if (_connections.Caller is { } caller) return TryGet(caller, out value);
        value = default;
        return false;
    }

    /// <summary>Gets the value of <paramref name="connection"/>, creating it on first use, while the connection is open.</summary>
    /// <param name="connection">A connection of the Window, such as one from <see cref="IRunicConnections.Current"/>.</param>
    /// <param name="value">The connection's value.</param>
    /// <returns><see langword="false"/> when the connection is not open, or the state was disposed.</returns>
    public bool TryGet(RunicConnection connection, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_values)
        {
            if (!_disposed && _values.TryGetValue(connection.ConnectionKey, out value)) return true;
            // Checked under the lock that Disconnected takes, after the dispatcher marked the connection closed, so a
            // value is never added once its connection's removal ran.
            if (_disposed || !IsOpen(connection.ConnectionKey))
            {
                value = default;
                return false;
            }
            value = _create(connection);
            _values.Add(connection.ConnectionKey, value);
            return true;
        }
    }

    /// <summary>The values that exist now, by connection key.</summary>
    public IReadOnlyDictionary<string, T> Values
    {
        get { lock (_values) return new Dictionary<string, T>(_values, StringComparer.Ordinal); }
    }

    private bool IsOpen(string connectionKey) =>
        _connections is RunicConnections window
            ? window.IsOpen(connectionKey)
            : _connections.Current.Any(item => item.ConnectionKey == connectionKey);

    private void OnDisconnected(object? sender, RunicConnectionEventArgs e)
    {
        T? removed;
        lock (_values)
            if (!_values.Remove(e.Connection.ConnectionKey, out removed)) return;
        (removed as IDisposable)?.Dispose();
    }

    /// <summary>Stops following the connections and disposes the values that are <see cref="IDisposable"/>.</summary>
    public void Dispose()
    {
        T[] values;
        lock (_values)
        {
            if (_disposed) return;
            _disposed = true;
            values = [.. _values.Values];
            _values.Clear();
        }
        _connections.Disconnected -= OnDisconnected;
        foreach (var value in values) (value as IDisposable)?.Dispose();
    }
}
