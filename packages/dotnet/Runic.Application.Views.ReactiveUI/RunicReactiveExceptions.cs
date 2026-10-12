// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive. Both flavors' commands and reactive
// objects implement ReactiveUI.IHandleObservableErrors from ReactiveUI.Core.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// Observes <see cref="IHandleObservableErrors.ThrownExceptions"/> of ReactiveUI commands and objects yourself,
/// instead of the logging that a generated Bridge gives each bridged command.
/// </summary>
/// <remarks>
/// A ReactiveCommand reports every exception on <c>ThrownExceptions</c>, including a declared
/// <see cref="RunicFailureException"/> that the Bridge already delivered to the client as
/// <c>domain-failed</c>. While a generated Bridge presents a ViewModel, it observes each bridged command's
/// <c>ThrownExceptions</c> and logs an unexpected exception that no Bridge call reported, as
/// <c>ReactiveCommandFailed</c> (event 1042), through the Window's logger factory. Use this helper to handle
/// a command's exceptions yourself: while it observes a command, the Bridge does not log that command's
/// exceptions. Without any subscriber, ReactiveUI sends them to <c>RxState.DefaultExceptionHandler</c>.
/// </remarks>
public static class RunicReactiveExceptions
{
    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>ThrownExceptions</c>. A declared failure
    /// (<see cref="RunicFailureException"/>) and a cancellation (<see cref="OperationCanceledException"/>),
    /// which the Bridge already reported to the client, are ignored; any other exception goes to
    /// <paramref name="onUnexpected"/>. Dispose the result to stop observing.
    /// </summary>
    /// <remarks>
    /// While subscribed, no exception of <paramref name="source"/> reaches ReactiveUI's default handler, and a
    /// generated Bridge that presents <paramref name="source"/> does not log its exceptions: this handler replaces
    /// that logging. An exception thrown by <paramref name="onUnexpected"/> surfaces from <c>ThrownExceptions.OnNext</c>.
    /// </remarks>
    public static IDisposable ObserveBridgeExceptions(this IHandleObservableErrors source, Action<Exception> onUnexpected)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onUnexpected);
        var claim = BridgeCommandFailures.For(source).Claim();
        try { return new Observation(source.ThrownExceptions.Subscribe(new UnexpectedExceptions(onUnexpected)), claim); }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>ThrownExceptions</c>. A declared failure
    /// (<see cref="RunicFailureException"/>) and a cancellation are ignored; any other exception is
    /// logged at Error as <c>ReactiveCommandFailed</c> (event 1042) with <paramref name="sourceName"/>
    /// as <c>Source</c>. Dispose the result to stop observing.
    /// </summary>
    /// <remarks>
    /// While subscribed, a generated Bridge that presents <paramref name="source"/> does not log its exceptions.
    /// </remarks>
    /// <param name="source">The command or reactive object.</param>
    /// <param name="logger">The logger that receives unexpected exceptions.</param>
    /// <param name="sourceName">The name logged as <c>Source</c>; defaults to the <paramref name="source"/> expression, such as <c>SaveCommand</c>.</param>
    public static IDisposable ObserveBridgeExceptions(this IHandleObservableErrors source, ILogger logger,
        [CallerArgumentExpression(nameof(source))] string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        var name = string.IsNullOrWhiteSpace(sourceName) ? source.GetType().Name : sourceName;
        return source.ObserveBridgeExceptions(error =>
            ReactiveLog.CommandFailed(logger, error, name, error.GetType().FullName ?? error.GetType().Name));
    }

    // A declared failure or a cancellation, also inside a single-inner
    // AggregateException as the Bridge unwraps it. ReactiveUI reports a
    // cancelled operation on ThrownExceptions; the client already saw it.
    internal static bool IsReported(Exception error)
    {
        while (error is AggregateException { InnerExceptions.Count: 1 } aggregate)
            error = aggregate.InnerExceptions[0];
        return error is RunicFailureException or OperationCanceledException;
    }

    private sealed class UnexpectedExceptions(Action<Exception> onUnexpected) : IObserver<Exception>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(Exception value)
        {
            if (!IsReported(value)) onUnexpected(value);
        }
    }

    private sealed class Observation(IDisposable subscription, IDisposable claim) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            subscription.Dispose();
            claim.Dispose();
        }
    }
}

// The logging a generated Bridge gives a bridged command (W250-042), shared per command so that two Bridges
// presenting one ViewModel subscribe once and log once. An exception is logged only when nothing else reports it:
// - a declared failure or a cancellation was reported to the client;
// - an exception that a Bridge call received is the Bridge's to report (its reply, and events 1000/1004 for an
//   unexpected one), as for a command of any MVVM library;
// - while ObserveBridgeExceptions observes the command, that handler replaces this logging.
// ReactiveUI delivers ThrownExceptions on the command's output scheduler: with the model-context scheduler after
// the failed execution's own observer, with the immediate scheduler before it. An exception that arrives while a
// Bridge call runs is therefore held until the calls end, and logged then unless one of them received it.
internal sealed class BridgeCommandFailures
{
    private static readonly ConditionalWeakTable<object, BridgeCommandFailures> States = [];
    private static readonly ConditionalWeakTable<Exception, object> ReceivedByCall = [];
    private static readonly object Received = new();

    private readonly Lock _gate = new();
    private readonly List<Logging> _loggings = [];
    private readonly List<Exception> _held = [];
    private IDisposable? _subscription;
    private int _claims;
    private int _calls;

    internal static BridgeCommandFailures For(object source) =>
        States.GetValue(source, static _ => new BridgeCommandFailures());

    // Logs source's unexpected exceptions with logger until the result is disposed. One subscription serves
    // every Bridge; the earliest Bridge still attached logs.
    internal IDisposable Log(IHandleObservableErrors source, ILogger logger, string name)
    {
        var logging = new Logging(this, logger, name);
        lock (_gate)
        {
            _loggings.Add(logging);
            if (_subscription is null)
            {
                try { _subscription = source.ThrownExceptions.Subscribe(new Observer(this)); }
                catch
                {
                    _loggings.Remove(logging);
                    throw;
                }
            }
        }
        return logging;
    }

    internal IDisposable Claim()
    {
        lock (_gate) _claims++;
        return new Release(this, static owner => { lock (owner._gate) owner._claims--; });
    }

    // Brackets one Bridge execution of the command; exceptions that arrive meanwhile are held until it ends.
    internal Call BeginCall()
    {
        lock (_gate) _calls++;
        return new Call(this);
    }

    private void OnNext(Exception error)
    {
        if (RunicReactiveExceptions.IsReported(error) || ReceivedByCall.TryGetValue(error, out _)) return;
        lock (_gate)
        {
            if (_calls > 0)
            {
                _held.Add(error);
                return;
            }
        }
        Report(error);
    }

    private void EndCall()
    {
        Exception[] held;
        lock (_gate)
        {
            if (--_calls > 0 || _held.Count == 0) return;
            held = [.. _held];
            _held.Clear();
        }
        foreach (var error in held)
            if (!ReceivedByCall.TryGetValue(error, out _)) Report(error);
    }

    private void Report(Exception error)
    {
        Logging? logging;
        lock (_gate) logging = _claims > 0 || _loggings.Count == 0 ? null : _loggings[0];
        if (logging is not null)
            ReactiveLog.CommandFailed(logging.Logger, error, logging.Name, error.GetType().FullName ?? error.GetType().Name);
    }

    private void Remove(Logging logging)
    {
        IDisposable? subscription = null;
        lock (_gate)
        {
            if (!_loggings.Remove(logging) || _loggings.Count != 0) return;
            subscription = _subscription;
            _subscription = null;
        }
        subscription?.Dispose();
    }

    // Marks the exception that a Bridge execution's observer received; the Bridge reports it.
    internal static void MarkReceived(Exception error) => ReceivedByCall.AddOrUpdate(error, Received);

    internal readonly struct Call(BridgeCommandFailures owner) : IDisposable
    {
        public void Dispose() => owner.EndCall();
    }

    private sealed class Logging(BridgeCommandFailures owner, ILogger logger, string name) : IDisposable
    {
        private int _disposed;

        public ILogger Logger { get; } = logger;
        public string Name { get; } = name;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Remove(this);
        }
    }

    private sealed class Release(BridgeCommandFailures owner, Action<BridgeCommandFailures> release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release(owner);
        }
    }

    private sealed class Observer(BridgeCommandFailures owner) : IObserver<Exception>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(Exception value) => owner.OnNext(value);
    }
}
