using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>Arguments delivered to a generated Bridge route by a headless test.</summary>
public sealed record BridgeTestArguments(
    long? Int64Value = null,
    bool? BooleanValue = null,
    string? StringValue = null,
    string? ClientKey = null,
    string? ConnectionKey = null) : IBridgeArguments
{
    /// <inheritdoc />
    public long GetInt64() => Int64Value ?? throw new InvalidOperationException("No integer argument was supplied.");
    /// <inheritdoc />
    public bool GetBoolean() => BooleanValue ?? throw new InvalidOperationException("No Boolean argument was supplied.");
    /// <inheritdoc />
    public string GetString() => StringValue ?? throw new InvalidOperationException("No string argument was supplied.");
}

/// <summary>
/// In-memory implementation of the same host-neutral route boundary used by
/// browser adapters. Calls invoke real generated handlers; no native UI runs.
/// </summary>
/// <remarks>
/// Like the Desktop transport, it receives state frames as UTF-8 through <see cref="PublishUtf8Async"/>, so tests
/// run the Bridge's UTF-8 delivery path.
/// </remarks>
public sealed class InMemoryBridgeTransport : IBridgeUtf8Transport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly List<(string Route, string Json)> _publications = [];
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>Names of currently bound routes.</summary>
    public IReadOnlyList<string> Routes
    {
        get { lock (_gate) return _bindings.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(); }
    }

    /// <inheritdoc />
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(name, new Binding(this, name, handler, null));
    }

    /// <inheritdoc />
    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(name, new Binding(this, name, null, handler));
    }

    /// <summary>Records a state publication for <see cref="TakePublications"/>.</summary>
    public void Publish(string name, string stateJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stateJson);
        lock (_gate)
        {
            ThrowIfDisposed();
            _publications.Add(new(name, stateJson));
            _published.TrySetResult();
            _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Records a state publication, decoded from UTF-8, for <see cref="TakePublications"/>.</summary>
    public ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(name, Encoding.UTF8.GetString(stateUtf8.Span));
        return ValueTask.CompletedTask;
    }

    /// <summary>Calls a synchronous generated route.</summary>
    public string Call(string name, BridgeTestArguments? arguments = null)
    {
        var binding = Find(name);
        if (binding.Sync is null) throw new InvalidOperationException($"Route '{name}' is asynchronous.");
        return binding.Sync(arguments ?? new());
    }

    /// <summary>Calls an asynchronous generated route and observes its result.</summary>
    public ValueTask<string> CallAsync(string name, BridgeTestArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var binding = Find(name);
        if (binding.Async is null) throw new InvalidOperationException($"Route '{name}' is synchronous.");
        return binding.Async(arguments ?? new(), cancellationToken);
    }

    // Completes at the next publication. Drivers wait on it instead of polling.
    internal Task NextPublication
    {
        get { lock (_gate) return _published.Task; }
    }

    // Whether a bound route is asynchronous, such as an awaited command.
    internal bool IsAsync(string name) => Find(name).Async is not null;

    /// <summary>
    /// Returns and removes the frames published since the previous call, in order, across all routes. Drivers and
    /// trackers then do not see them.
    /// </summary>
    /// <exception cref="InvalidOperationException">A delta frame names a collection change kind the client does not know.</exception>
    public IReadOnlyList<RunicViewPublication> TakePublications() =>
        TakeFrames().Select(frame => new RunicViewPublication(frame.Route, frame.Json)).ToArray();

    // Unparsed frames, so the drivers' log parses each route's frames only when that route takes them.
    internal (string Route, string Json)[] TakeFrames()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var result = _publications.ToArray();
            _publications.Clear();
            return result;
        }
    }

    /// <summary>Removes all routes and recorded publications.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _bindings.Clear();
            _publications.Clear();
        }
    }

    private Binding Add(string name, Binding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_bindings.TryAdd(name, binding))
                throw new InvalidOperationException($"Route '{name}' is already bound.");
            return binding;
        }
    }

    private Binding Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _bindings.TryGetValue(name, out var binding)
                ? binding : throw new KeyNotFoundException($"Route '{name}' is not bound.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Binding(
        InMemoryBridgeTransport owner,
        string name,
        Func<IBridgeArguments, string>? sync,
        Func<IBridgeArguments, CancellationToken, ValueTask<string>>? async) : IDisposable
    {
        public Func<IBridgeArguments, string>? Sync { get; } = sync;
        public Func<IBridgeArguments, CancellationToken, ValueTask<string>>? Async { get; } = async;

        public void Dispose()
        {
            lock (owner._gate)
                if (owner._bindings.TryGetValue(name, out var current) && ReferenceEquals(current, this))
                    owner._bindings.Remove(name);
        }
    }
}
