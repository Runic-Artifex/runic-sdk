using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Application.Views;

/// <summary>Whether a Window blocks its owner while it is open (<see cref="RunicWindowOptions.Modality"/>).</summary>
/// <remarks>The set may grow in a minor release.</remarks>
public enum RunicWindowModality
{
    /// <summary>The owner stays usable.</summary>
    None = 0,

    /// <summary>
    /// The owner takes no input until the Window closes; the application's other Windows stay usable. It needs
    /// <see cref="RunicWindowOptions.Owner"/>.
    /// </summary>
    Window = 1,
}

/// <summary>
/// The result of a Window opened with <see cref="IRunicWindowService.OpenForResultAsync{TWindow, TParameter, TResult}"/>: the
/// value its ViewModel completed it with (<see cref="IRunicWindowResult{TResult}.CompleteAsync"/>), or none when the Window
/// closed without one. The default value is none.
/// </summary>
/// <typeparam name="TResult">The result's type.</typeparam>
public readonly struct RunicWindowResult<TResult> : IEquatable<RunicWindowResult<TResult>>
{
    private readonly TResult _value;

    /// <summary>A completed result.</summary>
    /// <param name="value">The value.</param>
    public RunicWindowResult(TResult value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>
    /// Whether the Window completed with a value. <see langword="false"/> when it closed without one: the user closed it,
    /// or the application did.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>The value the Window completed with.</summary>
    /// <exception cref="InvalidOperationException">The Window closed without a result (<see cref="HasValue"/> is <see langword="false"/>).</exception>
    public TResult Value => HasValue ? _value : throw new InvalidOperationException("The Window closed without a result.");

    /// <summary>Gets the value, if the Window completed with one.</summary>
    /// <param name="value">The value, or the default of <typeparamref name="TResult"/>.</param>
    /// <returns><see langword="true"/> when the Window completed with a value.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out TResult value)
    {
        value = _value;
        return HasValue;
    }

    /// <summary>The value, or <paramref name="fallback"/> when the Window closed without one.</summary>
    /// <param name="fallback">The value without a result.</param>
    public TResult GetValueOrDefault(TResult fallback) => HasValue ? _value : fallback;

    /// <inheritdoc />
    public bool Equals(RunicWindowResult<TResult> other) =>
        HasValue == other.HasValue && (!HasValue || EqualityComparer<TResult>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RunicWindowResult<TResult> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HasValue ? HashCode.Combine(true, _value) : 0;

    /// <summary>Whether two results are equal.</summary>
    public static bool operator ==(RunicWindowResult<TResult> left, RunicWindowResult<TResult> right) => left.Equals(right);

    /// <summary>Whether two results differ.</summary>
    public static bool operator !=(RunicWindowResult<TResult> left, RunicWindowResult<TResult> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => HasValue ? $"Result({_value})" : "None";
}

/// <summary>
/// Completes the Window that the scope belongs to with a result, for a Window opened with
/// <see cref="IRunicWindowService.OpenForResultAsync{TWindow, TParameter, TResult}"/>. Take it in the constructor of the
/// Window's ViewModel:
/// <code>
/// public sealed partial class ChooseAccountViewModel(IRunicWindowResult&lt;Account&gt; result) : ObservableObject
/// {
///     [RelayCommand]
///     private async Task ChooseAsync(Account account) =&gt; await result.CompleteAsync(account);
/// }
/// </code>
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope. A test that builds
/// such a ViewModel by hand passes its own implementation, or uses <c>RunicWindowTestHost.CreateWindow</c>.
/// </remarks>
/// <typeparam name="TResult">The result's type, as the open named it.</typeparam>
public interface IRunicWindowResult<in TResult>
{
    /// <summary>
    /// Completes the Window with <paramref name="result"/> and closes it without asking its close guard, as
    /// <see cref="IRunicWindow.CloseAsync"/> does: the application has decided.
    /// </summary>
    /// <param name="result">The result the opener receives.</param>
    /// <param name="cancellationToken">Stops waiting for the close; the result is set either way.</param>
    /// <returns>
    /// <see langword="true"/> when this call set the result; <see langword="false"/> when the Window already completed or
    /// closed, and the result is dropped.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The Window was not opened for a result of this type, or the service was resolved outside a Window's scope.
    /// </exception>
    ValueTask<bool> CompleteAsync(TResult result, CancellationToken cancellationToken = default);
}

/// <summary>A Window that opened or closed, for <see cref="IRunicWindowService.WindowsChanged"/>.</summary>
/// <param name="window">The Window.</param>
/// <param name="opened"><see langword="true"/> when it opened; <see langword="false"/> when it closed.</param>
public sealed class RunicWindowsChangedEventArgs(IRunicWindow window, bool opened) : EventArgs
{
    /// <summary>The Window that opened or closed.</summary>
    public IRunicWindow Window { get; } = window ?? throw new ArgumentNullException(nameof(window));

    /// <summary><see langword="true"/> when <see cref="Window"/> opened; <see langword="false"/> when it closed.</summary>
    public bool Opened { get; } = opened;
}

/// <summary>Observes <see cref="IRunicWindowService.Windows"/> without System.Reactive.</summary>
public static class RunicWindowServiceExtensions
{
    /// <summary>
    /// The open Windows now and after every change: a plain <see cref="IObservable{T}"/> over
    /// <see cref="IRunicWindowService.WindowsChanged"/> that ReactiveUI composes and CommunityToolkit can subscribe to.
    /// </summary>
    /// <remarks>
    /// A subscription receives <see cref="IRunicWindowService.Windows"/> at once, then the list after each change, on the
    /// thread that changed it. Dispose the subscription to stop.
    /// </remarks>
    public static IObservable<IReadOnlyList<IRunicWindow>> ObserveWindows(this IRunicWindowService windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return new WindowsObservable(windows);
    }

    private sealed class WindowsObservable(IRunicWindowService windows) : IObservable<IReadOnlyList<IRunicWindow>>
    {
        public IDisposable Subscribe(IObserver<IReadOnlyList<IRunicWindow>> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            void OnChanged(object? sender, RunicWindowsChangedEventArgs args) => observer.OnNext(windows.Windows);
            windows.WindowsChanged += OnChanged;
            observer.OnNext(windows.Windows);
            return new Subscription(() => windows.WindowsChanged -= OnChanged);
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}

// What one open hands to the Window's scope besides the options: its parameter, its key and its result.
internal sealed record RunicWindowOpenRequest(object? Parameter, object? Key, RunicWindowResultSlot? Result)
{
    internal static RunicWindowOpenRequest Plain { get; } = new(null, null, null);
}

// The result of an OpenForResultAsync: one per open, bound to the Window's scope.
internal sealed class RunicWindowResultSlot(Type resultType)
{
    private readonly TaskCompletionSource<object?> _value = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRunicWindow? _window;

    internal Type ResultType { get; } = resultType;

    internal void Bind(IRunicWindow window) => Volatile.Write(ref _window, window);

    internal bool TryComplete(object? value) => _value.TrySetResult(value);

    // The Window closed: a result not set by now is none.
    internal void Closed() => _value.TrySetCanceled();

    internal RunicWindowResult<TResult> Read<TResult>() =>
        _value.Task.IsCompletedSuccessfully ? new RunicWindowResult<TResult>((TResult)_value.Task.Result!) : default;

    internal IRunicWindow? Window => Volatile.Read(ref _window);
}

// IRunicWindowResult<T> of a scope: the slot of the open that created it, if any.
internal sealed class RunicWindowResultHolder
{
    private RunicWindowResultSlot? _slot;

    internal void Set(RunicWindowResultSlot slot)
    {
        if (Interlocked.CompareExchange(ref _slot, slot, null) is not null)
            throw new InvalidOperationException("The Window's scope already has a result.");
    }

    internal RunicWindowResultSlot? Slot => Volatile.Read(ref _slot);
}

internal sealed class ScopedWindowResult<TResult>(RunicWindowResultHolder holder) : IRunicWindowResult<TResult>
{
    public async ValueTask<bool> CompleteAsync(TResult result, CancellationToken cancellationToken = default)
    {
        var slot = holder.Slot ?? throw new InvalidOperationException(
            $"IRunicWindowResult<{typeof(TResult).Name}> was resolved in a scope whose Window was not opened for a result. Open it " +
            $"with IRunicWindowService.OpenForResultAsync<TWindow, {typeof(TResult).Name}>(), and resolve it in that Window's scope.");
        if (!slot.ResultType.IsAssignableFrom(typeof(TResult)) && !(result is not null && slot.ResultType.IsInstanceOfType(result)))
            throw new InvalidOperationException(
                $"The Window was opened for a {slot.ResultType.Name} result, not a {typeof(TResult).Name}.");
        if (!slot.TryComplete(result)) return false;
        if (slot.Window is { } window)
        {
            // A Window that is closing or released already (ObjectDisposedException is an InvalidOperationException).
            try { _ = await window.CloseAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }
        return true;
    }
}

internal static class RunicWindowResultRegistration
{
    internal static void Add(IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped(
            services, static _ => new RunicWindowResultHolder());
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped(
            services, typeof(IRunicWindowResult<>), typeof(ScopedWindowResult<>));
    }
}
