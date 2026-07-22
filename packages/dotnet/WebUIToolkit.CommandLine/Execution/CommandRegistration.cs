using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.CommandLine;

internal sealed class FrozenCommandResultCodec<TResult> : ICommandResultCodec<TResult>
{
    private readonly ICommandResultCodec<TResult> _inner;

    internal FrozenCommandResultCodec(
        ICommandResultCodec<TResult> inner,
        string payloadType,
        JsonTypeInfo<TResult> typeInfo)
    {
        _inner = inner;
        PayloadType = payloadType;
        TypeInfo = typeInfo;
    }

    public string PayloadType { get; }

    public JsonTypeInfo<TResult> TypeInfo { get; }

    public ValueTask WriteHumanAsync(
        TResult value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken) =>
        _inner.WriteHumanAsync(value, console, culture, cancellationToken);
}

internal abstract class CommandRegistration
{
    internal abstract ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandDescriptor command,
        CommandExecutionRequest request,
        ICommandExecutionScopeFactory scopeFactory,
        IExitCodePolicy exitCodePolicy,
        ICommandOutcomeSink outcomeSink,
        ICommandExecutionObserver? observer,
        CancellationToken cancellationToken);
}

internal sealed class CommandRegistration<TOptions, THandler, TResult> : CommandRegistration
    where THandler : notnull, ICommandHandler<TOptions, TResult>
{
    private static readonly IReadOnlyList<CommandDiagnostic> NoDiagnostics = Array.Empty<CommandDiagnostic>();
    private readonly ICommandOptionsBinder<TOptions> _binder;
    private readonly ICommandHandlerFactory<THandler> _handlerFactory;
    private readonly ICommandResultCodec<TResult> _resultCodec;

    internal CommandRegistration(
        ICommandOptionsBinder<TOptions> binder,
        ICommandHandlerFactory<THandler> handlerFactory,
        ICommandResultCodec<TResult> resultCodec)
    {
        _binder = binder;
        _handlerFactory = handlerFactory;
        _resultCodec = resultCodec;
    }

    internal override async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandDescriptor command,
        CommandExecutionRequest request,
        ICommandExecutionScopeFactory scopeFactory,
        IExitCodePolicy exitCodePolicy,
        ICommandOutcomeSink outcomeSink,
        ICommandExecutionObserver? observer,
        CancellationToken cancellationToken)
    {
        ICommandExecutionScope? scope = null;
        CommandExecutionContext? context = null;
        CommandOutcome<TResult>? outcome = null;
        Exception? fatalException = null;

        try
        {
            try
            {
                scope = scopeFactory.CreateScope();
                if (scope is null)
                {
                    throw new InvalidOperationException("The command scope factory returned null.");
                }

                context = new CommandExecutionContext(
                    scope.Services,
                    request.Console,
                    request.Invocation.Path,
                    request.Invocation.OutputClassification.Mode!.Value,
                    request.Culture,
                    request.CorrelationId);
                Observe(observer, new CommandExecutionEvent(
                    CommandExecutionEventKind.Started,
                    context.Path,
                    context.CorrelationId));

                cancellationToken.ThrowIfCancellationRequested();
                CommandOutcome<TOptions> binding = await _binder
                    .BindAsync(request.Invocation, cancellationToken)
                    .ConfigureAwait(false);
                if (binding is null)
                {
                    throw new InvalidOperationException("The command options binder returned null.");
                }

                Observe(observer, new CommandExecutionEvent(
                    CommandExecutionEventKind.Bound,
                    context.Path,
                    context.CorrelationId,
                    binding.ExitCategory));

                if (!binding.IsSuccess)
                {
                    outcome = CommandOutcome.Failure<TResult>(binding.ExitCategory, binding.Fault!);
                }
                else
                {
                    THandler handler = _handlerFactory.Create(scope.Services);
                    if (handler is null)
                    {
                        throw new InvalidOperationException("The command handler factory returned null.");
                    }

                    Observe(observer, new CommandExecutionEvent(
                        CommandExecutionEventKind.HandlerStarted,
                        context.Path,
                        context.CorrelationId));
                    outcome = await handler.ExecuteAsync(binding.Value!, context, cancellationToken).ConfigureAwait(false);
                    if (outcome is null)
                    {
                        throw new InvalidOperationException("The command handler returned null.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = CommandOutcome.Failure<TResult>(
                    CommandExitCategory.Cancelled,
                    new CommandFault("WUTCLI4000", "The command was cancelled."));
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                outcome = HostFailure();
            }
            catch (Exception exception)
            {
                fatalException = exception;
            }
        }
        finally
        {
            if (scope is not null)
            {
                try
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (fatalException is not null)
                {
                    // Preserve the original fatal failure while still attempting disposal.
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    outcome = HostFailure();
                }
            }
        }

        if (fatalException is not null)
        {
            ExceptionDispatchInfo.Capture(fatalException).Throw();
        }

        outcome ??= HostFailure();
        context ??= new CommandExecutionContext(
            EmptyServiceProvider.Instance,
            request.Console,
            request.Invocation.Path,
            request.Invocation.OutputClassification.Mode!.Value,
            request.Culture,
            request.CorrelationId);

        int exitCode = exitCodePolicy.GetExitCode(outcome.ExitCategory);
        CancellationToken presentationToken = outcome.ExitCategory == CommandExitCategory.Cancelled
            ? CancellationToken.None
            : cancellationToken;
        try
        {
            await outcomeSink.WriteAsync(
                command,
                context,
                outcome,
                _resultCodec,
                exitCode,
                NoDiagnostics,
                presentationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CommandOutcome.Failure<TResult>(
                CommandExitCategory.Cancelled,
                new CommandFault("WUTCLI4000", "The command was cancelled."));
            exitCode = exitCodePolicy.GetExitCode(outcome.ExitCategory);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            outcome = HostFailure();
            exitCode = exitCodePolicy.GetExitCode(outcome.ExitCategory);
        }

        Observe(observer, new CommandExecutionEvent(
            CommandExecutionEventKind.Completed,
            context.Path,
            context.CorrelationId,
            outcome.ExitCategory,
            exitCode,
            outcome.Fault?.Code));
        return new CommandExecutionResult(outcome.ExitCategory, exitCode, outcome.Fault);
    }

    private static CommandOutcome<TResult> HostFailure() => CommandOutcome.Failure<TResult>(
        CommandExitCategory.HostFailure,
        new CommandFault("WUTCLI5000", "The command could not be completed."));

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException or AppDomainUnloadedException or BadImageFormatException;

    private static void Observe(ICommandExecutionObserver? observer, CommandExecutionEvent executionEvent)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.Observe(executionEvent);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Diagnostics observers are deliberately isolated from command semantics.
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
