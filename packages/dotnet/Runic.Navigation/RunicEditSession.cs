using System.ComponentModel;

namespace Runic.Navigation;

/// <summary>
/// The editing state of a form in a ViewModel of any MVVM framework: a baseline snapshot of the edited values, whether
/// the current values differ from it (<see cref="IsDirty"/>, with change notification), <see cref="Accept()"/> after a
/// save and <see cref="Revert"/> to discard. It pairs with <see cref="LeaveConfirmation"/>:
/// <c>new LeaveConfirmation(() =&gt; session.IsDirty, confirm, session.Revert)</c>.
/// </summary>
/// <typeparam name="T">
/// A snapshot of the edited values with value equality, such as a record or a tuple:
/// <c>(Title, Body)</c> or <c>NoteDraft(Title, Body)</c>.
/// </typeparam>
/// <example>
/// <code>
/// Session = new RunicEditSession&lt;(string Title, string Body)&gt;(this,
///     capture: () =&gt; (Title, Body),
///     restore: saved =&gt; (Title, Body) = saved);
/// // Bind or forward Session.IsDirty; after a save: Session.Accept().
/// </code>
/// </example>
/// <remarks>
/// With an owner, the session compares again whenever the owner raises <see cref="INotifyPropertyChanged.PropertyChanged"/>,
/// so a ReactiveUI <c>WhenAnyValue(vm =&gt; vm.Session.IsDirty)</c>, a CommunityToolkit binding and a WPF binding follow it.
/// Without one, call <see cref="Refresh"/> after a change. Use it on the ViewModel's thread (its model turn), as the
/// ViewModel's own properties. Dispose it to stop following the owner.
/// </remarks>
public sealed class RunicEditSession<T> : INotifyPropertyChanged, IDisposable
{
    private static readonly PropertyChangedEventArgs IsDirtyChanged = new(nameof(IsDirty));
    private static readonly PropertyChangedEventArgs BaselineChanged = new(nameof(Baseline));

    private readonly Func<T> _capture;
    private readonly Action<T> _restore;
    private readonly IEqualityComparer<T> _comparer;
    private readonly INotifyPropertyChanged? _owner;
    private bool _reverting;
    private bool _disposed;

    /// <summary>Creates a session whose baseline is the current values.</summary>
    /// <param name="owner">
    /// The ViewModel whose property changes may change the values, or <see langword="null"/> to call <see cref="Refresh"/>.
    /// </param>
    /// <param name="capture">Returns a snapshot of the edited values.</param>
    /// <param name="restore">Sets the edited values from a snapshot (<see cref="Revert"/>).</param>
    /// <param name="comparer">Compares snapshots; <see cref="EqualityComparer{T}.Default"/> by default.</param>
    public RunicEditSession(INotifyPropertyChanged? owner, Func<T> capture, Action<T> restore, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(restore);
        _capture = capture;
        _restore = restore;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        Baseline = capture();
        _owner = owner;
        if (owner is not null) owner.PropertyChanged += OnOwnerChanged;
    }

    /// <summary>The values as last accepted: when the session was created, or by <see cref="Accept()"/>.</summary>
    public T Baseline { get; private set; }

    /// <summary>The current values.</summary>
    public T Current => _capture();

    /// <summary>Whether the current values differ from <see cref="Baseline"/>.</summary>
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Compares the current values with the baseline again, and raises a change of <see cref="IsDirty"/> if it changed.</summary>
    public void Refresh()
    {
        if (_disposed || _reverting) return;
        SetDirty(!_comparer.Equals(_capture(), Baseline));
    }

    /// <summary>Makes the current values the baseline, such as after a save: the session is clean.</summary>
    public void Accept() => Accept(_capture());

    /// <summary>
    /// Makes <paramref name="baseline"/> the baseline, such as the values a save wrote or a load returned, and compares
    /// the current values with it.
    /// </summary>
    public void Accept(T baseline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Baseline = baseline;
        PropertyChanged?.Invoke(this, BaselineChanged);
        SetDirty(!_comparer.Equals(_capture(), baseline));
    }

    /// <summary>Restores the baseline, discarding the changes: the session is clean.</summary>
    public void Revert()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The owner's notifications while restoring describe intermediate values.
        _reverting = true;
        try { _restore(Baseline); }
        finally { _reverting = false; }
        SetDirty(!_comparer.Equals(_capture(), Baseline));
    }

    /// <summary>Stops following the owner.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_owner is not null) _owner.PropertyChanged -= OnOwnerChanged;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void SetDirty(bool dirty)
    {
        if (IsDirty == dirty) return;
        IsDirty = dirty;
        PropertyChanged?.Invoke(this, IsDirtyChanged);
    }
}
