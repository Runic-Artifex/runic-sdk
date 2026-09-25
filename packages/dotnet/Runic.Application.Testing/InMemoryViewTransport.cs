using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>Arguments delivered to a generated View route by a headless test.</summary>
public sealed record ViewTestArguments(
    long? Int64Value = null,
    bool? BooleanValue = null,
    string? StringValue = null,
    string? ClientKey = null,
    string? ConnectionKey = null) : IBridgeArguments
{
    public long GetInt64() => Int64Value ?? throw new InvalidOperationException("No integer argument was supplied.");
    public bool GetBoolean() => BooleanValue ?? throw new InvalidOperationException("No Boolean argument was supplied.");
    public string GetString() => StringValue ?? throw new InvalidOperationException("No string argument was supplied.");
}

/// <summary>One state publication in the order observed by the test transport.</summary>
public sealed record ViewTestPublication(string Route, string StateJson);

/// <summary>
/// In-memory implementation of the same host-neutral route boundary used by
/// browser adapters. Calls invoke real generated handlers; no native UI runs.
/// </summary>
public sealed class InMemoryViewTransport : IBridgeTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly List<ViewTestPublication> _publications = [];
    private bool _disposed;

    /// <summary>Names of currently bound routes.</summary>
    public IReadOnlyList<string> Routes
    {
        get { lock (_gate) return _bindings.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(); }
    }

    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(name, new Binding(this, name, handler, null));
    }

    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(name, new Binding(this, name, null, handler));
    }

    public void Publish(string name, string stateJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stateJson);
        lock (_gate)
        {
            ThrowIfDisposed();
            _publications.Add(new(name, stateJson));
        }
    }

    /// <summary>Calls a synchronous generated route.</summary>
    public string Call(string name, ViewTestArguments? arguments = null)
    {
        var binding = Find(name);
        if (binding.Sync is null) throw new InvalidOperationException($"Route '{name}' is asynchronous.");
        return binding.Sync(arguments ?? new());
    }

    /// <summary>Calls an asynchronous generated route and observes its result.</summary>
    public ValueTask<string> CallAsync(string name, ViewTestArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var binding = Find(name);
        if (binding.Async is null) throw new InvalidOperationException($"Route '{name}' is synchronous.");
        return binding.Async(arguments ?? new(), cancellationToken);
    }

    /// <summary>Returns and clears publications since the previous drain.</summary>
    public IReadOnlyList<ViewTestPublication> DrainPublications()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var result = _publications.ToArray();
            _publications.Clear();
            return result;
        }
    }

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

    private IDisposable Add(string name, Binding binding)
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
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryViewTransport));
    }

    private sealed class Binding(
        InMemoryViewTransport owner,
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
