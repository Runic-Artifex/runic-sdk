using System.ComponentModel;

namespace Runic.Application.Views;

/// <summary>
/// An object whose validation can be in progress, such as an asynchronous rule that asks a server whether a name is
/// taken. The Bridge reports its pending member paths in the <c>validation</c> state member (<c>validation.pending</c>,
/// the <c>validation-pending/1</c> feature), where a page shows "checking…" (<c>field.validating</c>). Implement it on a
/// ViewModel or a DTO of its state, usually by delegating to a <see cref="RunicPendingValidation"/>.
/// </summary>
public interface IRunicPendingValidation
{
    /// <summary>
    /// The member paths whose validation is in progress: relative CLR property paths as in
    /// <see cref="RunicValidationMessage.MemberPaths"/> (<c>UserName</c>, <c>Address.Postcode</c>, <c>Items[0].Name</c>),
    /// and <c>""</c> for the object itself.
    /// </summary>
    IReadOnlyCollection<string> PendingMemberPaths { get; }

    /// <summary>Raised when <see cref="PendingMemberPaths"/> changed.</summary>
    event EventHandler? PendingChanged;
}

/// <summary>
/// Tracks validation in progress per member path, for ViewModels of any MVVM framework. Mark a path pending for the
/// duration of a check with <see cref="Begin"/> or <see cref="Track{T}(string, Func{Task{T}})"/>, or follow an
/// observable of validation states with <see cref="RunicPendingValidationExtensions.TrackPending"/>. WPF can bind
/// <c>Pending[UserName]</c> or <see cref="HasPending"/>.
/// </summary>
/// <example>
/// <code>
/// public RunicPendingValidation Pending { get; } = new();
/// IReadOnlyCollection&lt;string&gt; IRunicPendingValidation.PendingMemberPaths => Pending.PendingMemberPaths;
/// event EventHandler? IRunicPendingValidation.PendingChanged { add => Pending.PendingChanged += value; remove => Pending.PendingChanged -= value; }
///
/// // CommunityToolkit: check the name when it changes.
/// partial void OnUserNameChanged(string value) =&gt; _ = Pending.Track(nameof(UserName), async () =&gt;
/// {
///     NameTaken = await _users.ExistsAsync(value);
///     ValidateProperty(UserName, nameof(UserName));
/// });
/// </code>
/// </example>
/// <remarks>
/// Paths are counted: a path stays pending until every <see cref="Begin"/> for it ended, so overlapping checks of one
/// member do not end each other. It is thread-safe. <see cref="PendingChanged"/> and <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// are raised on the thread that changed the set, outside its lock.
/// </remarks>
public sealed class RunicPendingValidation : IRunicPendingValidation, INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs HasPendingChanged = new(nameof(HasPending));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly PropertyChangedEventArgs PathsChanged = new(nameof(PendingMemberPaths));

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private string[] _paths = [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> PendingMemberPaths => Volatile.Read(ref _paths);

    /// <summary>Whether the validation of any path is in progress.</summary>
    public bool HasPending => Volatile.Read(ref _paths).Length != 0;

    /// <summary>Whether the validation of <paramref name="memberPath"/> is in progress (<c>""</c>: the object).</summary>
    public bool this[string memberPath] => IsPending(memberPath);

    /// <inheritdoc />
    public event EventHandler? PendingChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the validation of <paramref name="memberPath"/> is in progress (<c>""</c>: the object).</summary>
    public bool IsPending(string memberPath = "")
    {
        ArgumentNullException.ThrowIfNull(memberPath);
        return Array.IndexOf(Volatile.Read(ref _paths), memberPath) >= 0;
    }

    /// <summary>
    /// Marks <paramref name="memberPath"/> pending until the result is disposed. Disposing it again does nothing.
    /// </summary>
    /// <param name="memberPath">A relative CLR property path, or <c>""</c> for the object.</param>
    public IDisposable Begin(string memberPath = "")
    {
        ArgumentNullException.ThrowIfNull(memberPath);
        bool changed;
        lock (_gate)
        {
            _counts.TryGetValue(memberPath, out var count);
            _counts[memberPath] = count + 1;
            changed = count == 0;
            if (changed) _paths = [.. _counts.Keys];
        }
        if (changed) Raise();
        return new Lease(this, memberPath);
    }

    /// <summary>Marks <paramref name="memberPath"/> pending while <paramref name="validate"/> runs.</summary>
    public async Task Track(string memberPath, Func<Task> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        using var lease = Begin(memberPath);
        // The caller's context continues the check, so a ViewModel's rule completes on its own thread.
#pragma warning disable CA2007
        await validate();
#pragma warning restore CA2007
    }

    /// <summary>Marks <paramref name="memberPath"/> pending while <paramref name="validate"/> runs, and returns its result.</summary>
    public async Task<T> Track<T>(string memberPath, Func<Task<T>> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        using var lease = Begin(memberPath);
#pragma warning disable CA2007
        return await validate();
#pragma warning restore CA2007
    }

    private void End(string memberPath)
    {
        bool changed;
        lock (_gate)
        {
            if (!_counts.TryGetValue(memberPath, out var count)) return;
            changed = count == 1;
            if (changed)
            {
                _counts.Remove(memberPath);
                _paths = [.. _counts.Keys];
            }
            else _counts[memberPath] = count - 1;
        }
        if (changed) Raise();
    }

    private void Raise()
    {
        PendingChanged?.Invoke(this, EventArgs.Empty);
        var propertyChanged = PropertyChanged;
        if (propertyChanged is null) return;
        propertyChanged(this, PathsChanged);
        propertyChanged(this, IndexerChanged);
        propertyChanged(this, HasPendingChanged);
    }

    private sealed class Lease(RunicPendingValidation owner, string memberPath) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(memberPath);
        }
    }
}

/// <summary>Follows validation streams with a <see cref="RunicPendingValidation"/>.</summary>
public static class RunicPendingValidationExtensions
{
    /// <summary>
    /// Marks <paramref name="memberPath"/> pending while the latest value of <paramref name="states"/> is a pending
    /// state, until the subscription ends. With ReactiveUI.Validation, pass the stream of an asynchronous rule and
    /// recognize the pending state it starts each check with:
    /// <code>
    /// var checking = new ValidationState(true, "Checking…");
    /// var states = ValidationAsync.ForLatest(this.WhenAnyValue(vm =&gt; vm.UserName), CheckAsync, checking, scheduler)
    ///     .TrackPending(Pending, nameof(UserName), state =&gt; ReferenceEquals(state, checking));
    /// this.ValidationRule(vm =&gt; vm.UserName, states);
    /// </code>
    /// </summary>
    /// <param name="states">The validation states, or any values whose latest one tells whether a check runs.</param>
    /// <param name="pending">The tracker the ViewModel reports through <see cref="IRunicPendingValidation"/>.</param>
    /// <param name="memberPath">A relative CLR property path, or <c>""</c> for the object.</param>
    /// <param name="isPending">Whether a value means that a check is in progress.</param>
    public static IObservable<T> TrackPending<T>(this IObservable<T> states, RunicPendingValidation pending, string memberPath,
        Func<T, bool> isPending)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(memberPath);
        ArgumentNullException.ThrowIfNull(isPending);
        return new Tracked<T>(states, pending, memberPath, isPending);
    }

    private sealed class Tracked<T>(IObservable<T> source, RunicPendingValidation pending, string memberPath, Func<T, bool> isPending)
        : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var tracker = new Tracker<T>(observer, pending, memberPath, isPending);
            tracker.Subscription = source.Subscribe(tracker);
            return tracker;
        }
    }

    private sealed class Tracker<T>(IObserver<T> observer, RunicPendingValidation pending, string memberPath, Func<T, bool> isPending)
        : IObserver<T>, IDisposable
    {
        private readonly Lock _gate = new();
        private IDisposable? _lease;
        private bool _ended;

        internal IDisposable? Subscription { get; set; }

        public void OnNext(T value)
        {
            bool checking;
            try { checking = isPending(value); }
            catch (Exception error)
            {
                OnError(error);
                return;
            }
            // The tracker raises its events outside this lock.
            IDisposable? ended = null;
            var begin = false;
            lock (_gate)
            {
                if (_ended) return;
                if (!checking) (ended, _lease) = (_lease, null);
                else begin = _lease is null;
            }
            ended?.Dispose();
            if (begin)
            {
                var lease = pending.Begin(memberPath);
                lock (_gate)
                {
                    if (_ended || _lease is not null) ended = lease;
                    else (_lease, ended) = (lease, null);
                }
                ended?.Dispose();
            }
            observer.OnNext(value);
        }

        public void OnError(Exception error)
        {
            if (End()) observer.OnError(error);
        }

        public void OnCompleted()
        {
            if (End()) observer.OnCompleted();
        }

        public void Dispose()
        {
            End();
            Subscription?.Dispose();
        }

        // Ends the pending mark; false when it already ended.
        private bool End()
        {
            IDisposable? lease;
            lock (_gate)
            {
                if (_ended) return false;
                _ended = true;
                (lease, _lease) = (_lease, null);
            }
            lease?.Dispose();
            return true;
        }
    }
}
