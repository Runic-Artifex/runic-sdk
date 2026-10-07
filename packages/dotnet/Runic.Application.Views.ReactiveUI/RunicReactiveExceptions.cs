// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive. Both flavors' commands and reactive
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
    /// (<see cref="RunicFailureException"/>) and a cancellation (<see cref="OperationCanceledException"/>),
    /// which the Bridge already reported to the client, are ignored; any other exception goes to
    /// <paramref name="onUnexpected"/>. Dispose the result to stop observing.
    /// </summary>
    /// <remarks>
    /// While subscribed, no exception of <paramref name="source"/> reaches ReactiveUI's default handler.
    /// An exception thrown by <paramref name="onUnexpected"/> surfaces from <c>ThrownExceptions.OnNext</c>.
    /// </remarks>
    public static IDisposable ObserveBridgeExceptions(this IHandleObservableErrors source, Action<Exception> onUnexpected)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onUnexpected);
        return source.ThrownExceptions.Subscribe(new UnexpectedExceptions(onUnexpected));
    }

    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>ThrownExceptions</c>. A declared failure
    /// (<see cref="RunicFailureException"/>) and a cancellation are ignored; any other exception is
    /// logged at Error as <c>ReactiveCommandFailed</c> (event 1042) with <paramref name="sourceName"/>
    /// as <c>Source</c>. Dispose the result to stop observing.
    /// </summary>
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
    private static bool IsReported(Exception error)
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
}
