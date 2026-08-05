using System;
using System.Threading;
using System.Threading.Tasks;
using RunicFlow;
using RunicFlow.Workflows;
using RunicToolkit.Samples.AdvancedTodo.Domain;

namespace RunicToolkit.Samples.AdvancedTodo.Application;

internal sealed record TodoDraft(string Title, string Notes, TodoPriority Priority);

internal sealed class TodoCreationContext
{
    internal string Title { get; set; } = string.Empty;

    internal string Notes { get; set; } = string.Empty;

    internal TodoPriority Priority { get; set; } = TodoPriority.Normal;
}

internal sealed class TodoCreationFlow : IAsyncDisposable
{
    internal static readonly StepKey DetailsStep = new("todo.create.details");
    internal static readonly StepKey ReviewStep = new("todo.create.review");

    private readonly TodoCreationContext _context = new();
    private readonly WorkflowSession<TodoCreationContext, TodoDraft> _session;

    internal TodoCreationFlow(Action<StepKey> presentStep)
    {
        ArgumentNullException.ThrowIfNull(presentStep);
        WorkflowDefinition<TodoCreationContext, TodoDraft> definition =
            new WorkflowDefinitionBuilder<TodoCreationContext, TodoDraft>(
                new WorkflowKey("todo.create"),
                schemaVersion: 1)
                .AddStep<DetailsViewModel>(
                    DetailsStep,
                    new ViewContract("todo.create.details"),
                    static (_, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(
                            new WorkflowStepActivation(new DetailsViewModel(), new StepScope()));
                    },
                    retention: WorkflowStepRetention.RetainVisited)
                .AddStep<ReviewViewModel>(
                    ReviewStep,
                    new ViewContract("todo.create.review"),
                    static (_, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(
                            new WorkflowStepActivation(new ReviewViewModel(), new StepScope()));
                    })
                .AddTransition(DetailsStep, ReviewStep)
                .StartWith(DetailsStep)
                .FinishWith(static context =>
                    new TodoDraft(context.Title, context.Notes, context.Priority))
                .Build();

        _session = new WorkflowSession<TodoCreationContext, TodoDraft>(
            definition,
            _context,
            new BrowserWorkflowPresenter(presentStep));
    }

    internal WorkflowSnapshot Snapshot => _session.Snapshot;

    internal ValueTask<WorkflowTransition<TodoDraft>> StartAsync(
        CancellationToken cancellationToken) =>
        _session.StartAsync(cancellationToken);

    internal ValueTask<WorkflowTransition<TodoDraft>> NextAsync(
        string title,
        string notes,
        TodoPriority priority,
        CancellationToken cancellationToken)
    {
        _context.Title = title.Trim();
        _context.Notes = notes.Trim();
        _context.Priority = priority;
        return _session.NextAsync(cancellationToken);
    }

    internal ValueTask<WorkflowTransition<TodoDraft>> BackAsync(
        CancellationToken cancellationToken) =>
        _session.BackAsync(cancellationToken);

    internal ValueTask<WorkflowTransition<TodoDraft>> FinishAsync(
        CancellationToken cancellationToken) =>
        _session.FinishAsync(cancellationToken);

    internal ValueTask<WorkflowTransition<TodoDraft>> CancelAsync(
        CancellationToken cancellationToken) =>
        _session.CancelAsync(cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private sealed class DetailsViewModel : IWorkflowStepValidator<TodoCreationContext>
    {
        public ValueTask<WorkflowValidationResult> ValidateAsync(
            TodoCreationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowValidationResult result = string.IsNullOrWhiteSpace(context.Title)
                ? WorkflowValidationResult.FromIssues(
                [
                    new WorkflowValidationIssue(
                        "todo.title.required",
                        "Give the task a title before continuing."),
                ])
                : WorkflowValidationResult.Valid;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReviewViewModel;

    private sealed class StepScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class BrowserWorkflowPresenter(Action<StepKey> presentStep)
        : IWorkflowPresenter
    {
        public ValueTask<IFlowPresentationLease> PresentAsync(
            FlowContentDescriptor content,
            WorkflowPresentationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            presentStep(context.Step);
            return ValueTask.FromResult<IFlowPresentationLease>(new BrowserPresentationLease());
        }
    }

    private sealed class BrowserPresentationLease : IFlowPresentationLease
    {
        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
