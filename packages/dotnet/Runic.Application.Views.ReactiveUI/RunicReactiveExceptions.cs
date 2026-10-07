// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive. Both flavors' commands and reactive
// objects implement ReactiveUI.IHandleObservableErrors from ReactiveUI.Core.
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// Observes <see cref="IHandleObservableErrors.ThrownExceptions"/> of bridged ReactiveUI commands.
/// </summary>
/// <remarks>
/// A ReactiveCommand reports every exception on <c>ThrownExceptions</c>, including a declared
/// <see cref="RunicFailureException"/> that the Bridge already delivered to the client as
/// <c>domain-failed</c>. Without a subscriber ReactiveUI sends it to <c>RxState.DefaultExceptionHandler</c>,
/// which breaks into the debugger and throws <c>UnhandledErrorException</c>. Give every bridged
/// command a subscriber: this helper or the application's own.
/// </remarks>
public static class RunicReactiveExceptions
{
    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>ThrownExceptions</c>. A declared failure
    /// (<see cref="RunicFailureException"/>) is ignored; any other exception goes to
    /// <paramref name="onUnexpected"/>. Dispose the result to stop observing.
    /// </summary>
    public static IDisposable ObserveBridgeExceptions(this IHandleObservableErrors source, Action<Exception> onUnexpected)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onUnexpected);
        return source.ThrownExceptions.Subscribe(new UnexpectedExceptions(onUnexpected));
    }

    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>ThrownExceptions</c>. A declared failure
    /// (<see cref="RunicFailureException"/>) is ignored; any other exception is logged at Error
    /// as <c>ReactiveCommandFailed</c> (event 1042). Dispose the result to stop observing.
    /// </summary>
    public static IDisposable ObserveBridgeExceptions(this IHandleObservableErrors source, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        var name = source.GetType().Name;
        return source.ObserveBridgeExceptions(error =>
            ReactiveLog.CommandFailed(logger, error, name, error.GetType().FullName ?? error.GetType().Name));
    }

    // A declared failure, also inside a single-inner AggregateException as the
    // Bridge unwraps it.
    private static bool IsDeclaredFailure(Exception error)
    {
        while (error is AggregateException { InnerExceptions.Count: 1 } aggregate)
            error = aggregate.InnerExceptions[0];
        return error is RunicFailureException;
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
            if (!IsDeclaredFailure(value)) onUnexpected(value);
        }
    }
}
