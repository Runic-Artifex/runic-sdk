using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

/// <summary>Represents one ownership claim over a model graph's execution context.</summary>
public interface IRunicModelContextLease : IDisposable, IAsyncDisposable
{
    /// <summary>The context that owns the claimed model graph.</summary>
    IRunicModelContext Context { get; }
}

/// <summary>
/// Associates each model identity with the context that owns its mutations.
/// </summary>
/// <remarks>
/// Bind every object that can be independently presented or mutated. The registry cannot
/// infer an arbitrary CLR object graph, so composition roots should bind their root and
/// children together. A second context for the same object is rejected rather than moving
/// a live ViewModel between execution owners.
/// </remarks>
public sealed class RunicModelContextRegistry
{
    private readonly object _gate = new();
    // The process-wide default registry must not keep a detached ViewModel graph alive.
    // Registration values may name graph members while a lease is active; the weak-key
    // table releases that association once the final lease removes it or its key dies.
    private readonly ConditionalWeakTable<object, Registration> _registrations = new();

    // Generated bridges and content sessions resolve contexts only through
    // Shared. A second public registry would silently split a graph between
    // the context an application bound and the one its bridges use.
    internal RunicModelContextRegistry() { }

    /// <summary>
    /// Gets the process-wide registry used by generated bridges and content sessions.
    /// </summary>
    public static RunicModelContextRegistry Shared { get; } = new();

    /// <summary>
    /// Binds the supplied model identities to an application-owned context. Releasing this
    /// lease only removes registrations; it never disposes the supplied context.
    /// </summary>
    public IRunicModelContextLease Bind(IRunicModelContext context, params object[] models)
    {
        ArgumentNullException.ThrowIfNull(context);
        return BindCore(context, ownsContext: false, Normalize(models));
    }

    /// <summary>
    /// Acquires a context for the supplied model graph. The first lease creates and owns it;
    /// later leases for the same graph reuse that context. It is disposed after the final
    /// owner lease is released.
    /// </summary>
    public IRunicModelContextLease Acquire(Func<IRunicModelContext> createContext, params object[] models)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        var normalized = Normalize(models);
        lock (_gate)
        {
            var existing = FindContextOrThrow(normalized);
            if (existing is not null) return AddLease(existing, normalized);
        }

        var created = createContext() ?? throw new InvalidOperationException("The model-context factory returned null.");
        IRunicModelContextLease? sharedLease = null;
        lock (_gate)
        {
            var winner = FindContextOrThrow(normalized);
            if (winner is null)
            {
                var registration = new Registration(created, ownsContext: true);
                foreach (var model in normalized)
                {
                    _registrations.Add(model, registration);
                    registration.Models.Add(model);
                }
                return AddLease(registration, normalized);
            }

            // Keep the winning registration alive before disposing the losing
            // candidate. Without this lease, the winner's final caller could
            // release it between the two locks and leave us re-registering an
            // already disposed context.
            sharedLease = AddLease(winner, normalized);
        }

        // A racing acquire claimed this graph first. Its context remains the owner and
        // the unused candidate can be shut down before returning that shared owner.
        try { RunicModelContextDisposal.DisposeSynchronously(created); }
        catch
        {
            sharedLease!.Dispose();
            throw;
        }
        return sharedLease!;
    }

    /// <summary>Gets the registered context for a model identity.</summary>
    public bool TryGet(object model, out IRunicModelContext? context)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (_registrations.TryGetValue(model, out var registration))
            {
                context = registration.Context;
                return true;
            }
        }

        context = null;
        return false;
    }

    /// <summary>Gets the registered context for a model identity.</summary>
    public IRunicModelContext GetRequired(object model) => TryGet(model, out var context)
        ? context!
        : throw new InvalidOperationException("The model has no registered Runic model context.");

    private Lease BindCore(IRunicModelContext context, bool ownsContext, object[] models)
    {
        lock (_gate)
        {
            var existing = FindContextOrThrow(models);
            if (existing is not null)
            {
                if (!ReferenceEquals(existing.Context, context))
                    throw new InvalidOperationException("The model graph is already owned by a different Runic model context.");
                return AddLease(existing, models);
            }

            var registration = new Registration(context, ownsContext);
            foreach (var model in models)
            {
                _registrations.Add(model, registration);
                registration.Models.Add(model);
            }
            return AddLease(registration, models);
        }
    }

    private Registration? FindContextOrThrow(IEnumerable<object> models)
    {
        Registration? registration = null;
        foreach (var model in models)
        {
            if (!_registrations.TryGetValue(model, out var candidate)) continue;
            if (registration is not null && !ReferenceEquals(registration, candidate))
                throw new InvalidOperationException("The supplied models belong to different Runic model contexts.");
            registration = candidate;
        }
        return registration;
    }

    private Lease AddLease(Registration registration, object[] models)
    {
        foreach (var model in models)
        {
            if (!_registrations.TryGetValue(model, out var existing))
            {
                _registrations.Add(model, registration);
                registration.Models.Add(model);
            }
            else if (!ReferenceEquals(existing, registration))
                throw new InvalidOperationException("The model graph is already owned by a different Runic model context.");
        }
        registration.LeaseCount++;
        return new Lease(this, registration, models);
    }

    private async ValueTask ReleaseAsync(Registration registration, object[] models)
    {
        IRunicModelContext? dispose = null;
        lock (_gate)
        {
            if (registration.LeaseCount == 0) return;
            registration.LeaseCount--;
            if (registration.LeaseCount != 0) return;

            foreach (var model in registration.Models)
            {
                if (_registrations.TryGetValue(model, out var existing) && ReferenceEquals(existing, registration))
                    _registrations.Remove(model);
            }
            dispose = registration.OwnsContext ? registration.Context : null;
        }

        if (dispose is not null) await dispose.DisposeAsync().ConfigureAwait(false);
    }

    private static object[] Normalize(object[] models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Length == 0) throw new ArgumentException("At least one model identity is required.", nameof(models));
        var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (model.GetType().IsValueType)
                throw new ArgumentException("A model identity must be a reference type.", nameof(models));
            unique.Add(model);
        }
        return unique.ToArray();
    }

    private sealed class Registration(IRunicModelContext context, bool ownsContext)
    {
        public IRunicModelContext Context { get; } = context;
        public bool OwnsContext { get; } = ownsContext;
        public int LeaseCount { get; set; }
        public HashSet<object> Models { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class Lease(RunicModelContextRegistry owner, Registration registration, object[] models)
        : IRunicModelContextLease
    {
        private int _disposed;

        public IRunicModelContext Context => registration.Context;

        public void Dispose()
        {
            var release = DisposeAsync();
            if (release.IsCompletedSuccessfully) return;
            // ReleaseAsync removes this registry claim before it waits for the
            // owned context to drain. A synchronous session close may originate
            // in that context's current view callback, so it must not wait for
            // itself to leave the turn.
            if (registration.Context.IsExecuting)
            {
                _ = ObserveReleaseAsync(release);
                return;
            }
            release.AsTask().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            return owner.ReleaseAsync(registration, models);
        }

        private static async Task ObserveReleaseAsync(ValueTask release)
        {
            try { await release.ConfigureAwait(false); }
            catch (Exception error)
        { ViewsLog.ModelContextReleaseFailed(TraceFallbackLogger.Instance, BridgeTelemetry.LoggedException(error), BridgeTelemetry.ErrorType(error)); }
        }
    }
}
