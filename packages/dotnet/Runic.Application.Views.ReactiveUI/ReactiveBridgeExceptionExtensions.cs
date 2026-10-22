// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive. Both flavors' commands and reactive
// objects implement ReactiveUI.IHandleObservableErrors from ReactiveUI.Core.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Navigation;

#if SYSTEM_REACTIVE
using Runic.Application.Views.ReactiveUI.Reactive.Generated;

namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
using Runic.Application.Views.ReactiveUI.Generated;

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
public static class ReactiveBridgeExceptionExtensions
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
// the failed execution's own observer, with the immediate scheduler before it. An exception is therefore held, and
// logged when the hold ends unless a Bridge call received it:
// - while a Bridge call of the same command runs;
// - while the Bridge call runs in whose flow the exception was raised (#473), so an exception that hand-written code
//   received from a plain Execute and let propagate, or wrapped, reaches the call first. ReactiveUI delivers
//   ThrownExceptions in the failing execution's execution context, which descends from the call: Runic's schedulers
//   and a UI dispatcher restore it per item, and the immediate scheduler runs inline. A call whose flow raised it
//   hands it to the enclosing call when it ends; the outermost one logs it.
// The subscription also lasts until every execution that Runic started (a Bridge call, RunAsync) has ended (I-188):
// ReactiveUI reports a cancelled execution's end on ThrownExceptions when its work stops, which can be after the call
// returned and the Bridge detached, as when a retiring navigation entry cancels its operation. Without a subscriber,
// ReactiveUI's default handler would end the process. An execution has ended when its observer received its end and,
// for a failure, when ThrownExceptions delivered that failure. ReactiveUI calls the observer after it scheduled the
// delivery on the output scheduler, so the delivery can come before or after. A failure reported after every Bridge
// detached is logged with the logger of the Bridge that detached last, or written to Trace when no Bridge presented the
// command.
// An execution that ReactiveUI ends by unsubscribing, without telling its observer (a cancelled CreateFromObservable
// execution), never counts as ended: the command then stays observed, and its failures logged, for its lifetime.
internal sealed class BridgeCommandFailures
{
    private static readonly ConditionalWeakTable<object, BridgeCommandFailures> States = [];
    private static readonly ConditionalWeakTable<Exception, object> ReceivedByCall = [];
    private static readonly object Received = new();

    private readonly Lock _gate = new();
    private readonly List<Logging> _loggings = [];
    private readonly List<Exception> _held = [];
    // The failures that ended an execution's observer and that ThrownExceptions has not delivered yet, and those that
    // ThrownExceptions delivered while executions ran and that no execution's observer has received yet.
    private readonly List<Exception> _awaited = [];
    private readonly List<Exception> _delivered = [];
    private IDisposable? _subscription;
    private Logging? _detached;
    private string? _sourceName;
    private int _claims;
    private int _calls;
    private int _executions;

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
            try { Subscribe(source); }
            catch
            {
                _loggings.Remove(logging);
                throw;
            }
        }
        return logging;
    }

    // Observes source's ThrownExceptions from one execution that Runic starts until that execution ended, also after
    // the call that started it returned and after every Bridge detached. Call it before subscribing to the execution.
    internal Execution BeginExecution(IHandleObservableErrors source)
    {
        lock (_gate)
        {
            Subscribe(source);
            _executions++;
        }
        return new Execution(this);
    }

    private void Subscribe(IHandleObservableErrors source)
    {
        if (_subscription is not null) return;
        _subscription = source.ThrownExceptions.Subscribe(new Observer(this));
        _sourceName ??= source.GetType().Name;
    }

    internal IDisposable Claim()
    {
        lock (_gate) _claims++;
        return new Release(this, static owner => { lock (owner._gate) owner._claims--; });
    }

    // Brackets one Bridge execution of the command; exceptions of the command that arrive meanwhile are held until
    // it ends. Unless holdsFlow is false, exceptions that any bridged command raises in the call's flow are held too.
    internal Call BeginCall(bool holdsFlow = true)
    {
        lock (_gate) _calls++;
        return new Call(this, new BridgeCallScope(ModelTurnFlow.CurrentCall, RunicModelContext.Current, holdsFlow));
    }

    private void OnNext(Exception error)
    {
        bool ended;
        lock (_gate)
        {
            ended = _awaited.Remove(error);
            if (ended) Ended();
            else if (_executions > 0) _delivered.Add(error);
        }
        try { Handle(error); }
        finally
        {
            if (ended) ReleaseIfIdle();
        }
    }

    private void Handle(Exception error)
    {
        if (ReactiveBridgeExceptionExtensions.IsReported(error) || IsReceived(error)) return;
        if (ModelTurnFlow.CurrentCall is { } scope && scope.TryHold(this, error)) return;
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
            if (!IsReceived(error)) Report(error);
    }

    internal static bool IsReceived(Exception error) => ReceivedByCall.TryGetValue(error, out _);

    internal void Report(Exception error)
    {
        Logging? logging;
        string? sourceName;
        bool detached;
        lock (_gate)
        {
            if (_claims > 0) return;
            detached = _loggings.Count == 0;
            logging = detached ? _detached : _loggings[0];
            sourceName = _sourceName;
        }
        var errorType = error.GetType().FullName ?? error.GetType().Name;
        if (logging is null)
        {
            ReactiveLog.CommandFailed(TraceLogger.Instance, error, sourceName ?? "ReactiveCommand", errorType);
            return;
        }
        try { ReactiveLog.CommandFailed(logging.Logger, error, logging.Name, errorType); }
        // The logger of a detached Bridge can belong to a Window whose logger factory is disposed.
        catch (Exception) when (detached)
        {
            ReactiveLog.CommandFailed(TraceLogger.Instance, error, logging.Name, errorType);
        }
    }

    private void Remove(Logging logging)
    {
        lock (_gate)
        {
            if (!_loggings.Remove(logging)) return;
            if (_loggings.Count == 0) _detached = logging;
        }
        ReleaseIfIdle();
    }

    private void Ended()
    {
        // Deliveries that no execution claimed came from executions that Runic did not start.
        if (--_executions == 0) _delivered.Clear();
    }

    // Ends the subscription once no Bridge presents the command and every execution that Runic started has ended.
    private void ReleaseIfIdle()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_loggings.Count != 0 || _executions != 0) return;
            subscription = _subscription;
            _subscription = null;
        }
        subscription?.Dispose();
    }

    private void EndExecution(Exception? error)
    {
        lock (_gate)
        {
            if (error is not null && !_delivered.Remove(error))
            {
                _awaited.Add(error);
                return;
            }
            Ended();
        }
        ReleaseIfIdle();
    }

    // Marks the exception that a Bridge execution's observer received; the Bridge reports it. The exceptions it wraps
    // are marked too: hand-written code that wraps a command's exception lets the call report both.
    internal static void MarkReceived(Exception error)
    {
        ReceivedByCall.AddOrUpdate(error, Received);
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) MarkReceived(inner);
        else if (error.InnerException is { } wrapped) MarkReceived(wrapped);
    }

    internal readonly struct Call(BridgeCommandFailures owner, BridgeCallScope scope) : IDisposable
    {
        internal BridgeCallScope Scope => scope;

        public void Dispose()
        {
            owner.EndCall();
            scope.End();
        }
    }

    // One execution that Runic started. End is called when the execution's observer receives its end.
    internal sealed class Execution(BridgeCommandFailures owner)
    {
        private int _ended;

        internal void End(Exception? error)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.EndExecution(error);
        }
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

// One Bridge call's flow (#473). ModelTurnFlow puts it into the async flow of the call's subscription, so the
// command's execution, the hand-written code its body runs and the executions that code starts inherit it. An
// exception that a bridged command reports on ThrownExceptions in this flow is held until the call ends; an ended
// call hands what it held, and what arrives later in its flow, to the enclosing call while that one runs. A call that
// does not hold (ExecuteStream, which can stay open indefinitely) shields its flow from the enclosing call.
internal sealed class BridgeCallScope(BridgeCallScope? parent, RunicModelContext? context, bool holds)
{
    private readonly Lock _gate = new();
    private List<(BridgeCommandFailures Owner, Exception Error)>? _held;
    private bool _ended;

    // The model context of the turn that started the call, for ModelTurnFlow.
    internal RunicModelContext? Context => context;

    internal bool TryHold(BridgeCommandFailures owner, Exception error)
    {
        if (!holds) return false;
        lock (_gate)
        {
            if (!_ended)
            {
                (_held ??= []).Add((owner, error));
                return true;
            }
        }
        return parent?.TryHold(owner, error) ?? false;
    }

    internal void End()
    {
        if (!holds) return;
        List<(BridgeCommandFailures Owner, Exception Error)>? held;
        lock (_gate)
        {
            _ended = true;
            held = _held;
            _held = null;
        }
        if (held is null) return;
        foreach (var (owner, error) in held)
        {
            if (BridgeCommandFailures.IsReceived(error) || (parent?.TryHold(owner, error) ?? false)) continue;
            owner.Report(error);
        }
    }
}
