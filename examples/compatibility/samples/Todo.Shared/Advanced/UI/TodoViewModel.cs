using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebUIToolkit.MVVM.Flow;
using WebUIToolkit.MVVM.Workflows;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

internal sealed partial class TodoViewModel :
    ObservableObject,
    INotifyDataErrorInfo,
    IAsyncDisposable
{
    private readonly TodoService _service;
    private readonly List<DiagnosticEntry> _diagnostics = [];
    private IReadOnlyList<TodoItem> _items = [];
    private TodoCreationFlow? _creationFlow;
    private CancellationTokenSource? _importCancellation;
    private Task? _importTask;
    private string[] _newTitleErrors = [];

    [ObservableProperty]
    private string newTitle = string.Empty;

    [ObservableProperty]
    private string newNotes = string.Empty;

    [ObservableProperty]
    private string newPriority = nameof(TodoPriority.Normal);

    [ObservableProperty]
    private string query = string.Empty;

    [ObservableProperty]
    private string filter = nameof(TodoFilter.All);

    [ObservableProperty]
    private string selectedId = string.Empty;

    internal TodoViewModel(TodoService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public bool HasErrors => _newTitleErrors.Length != 0;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) ||
        string.Equals(propertyName, nameof(NewTitle), StringComparison.Ordinal)
            ? _newTitleErrors
            : Array.Empty<string>();

    partial void OnNewTitleChanged(string value) => ValidateNewTitle(value);

    internal IReadOnlyList<TodoItem> VisibleItems
    {
        get
        {
            TodoFilter selectedFilter = ParseFilter(Filter);
            IEnumerable<TodoItem> filtered = _items.Where(item =>
                selectedFilter switch
                {
                    TodoFilter.Active => !item.IsCompleted,
                    TodoFilter.Completed => item.IsCompleted,
                    _ => true,
                });
            if (!string.IsNullOrWhiteSpace(Query))
            {
                filtered = filtered.Where(item =>
                    item.Title.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    item.Notes.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return filtered
                .OrderBy(static item => item.IsCompleted)
                .ThenByDescending(static item => item.Priority)
                .ThenByDescending(static item => item.CreatedAt)
                .ToArray();
        }
    }

    internal int TotalCount => _items.Count;

    internal int RemainingCount => _items.Count(static item => !item.IsCompleted);

    internal int CompletedCount => _items.Count(static item => item.IsCompleted);

    internal IReadOnlyList<DiagnosticEntry> Diagnostics => _diagnostics;

    internal bool IsImporting { get; private set; }

    internal StepKey? WizardStep => _creationFlow?.Snapshot.CurrentStep;

    internal IReadOnlyList<WorkflowValidationIssue> WizardIssues =>
        _creationFlow?.Snapshot.ValidationIssues ?? [];

    internal AdvancedTodoState State =>
        new(
            TotalCount,
            RemainingCount,
            CompletedCount,
            IsImporting,
            WizardStep?.Value,
            WizardIssues.Select(static issue => issue.Message).ToArray());

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        Observe("session", $"Loaded {_items.Count} persisted task(s).");
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task AddAsync(CancellationToken cancellationToken)
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            Observe("validation", "The quick-add form was rejected.");
            return;
        }

        await _service.AddAsync(
            NewTitle,
            NewNotes,
            ParsePriority(NewPriority),
            cancellationToken).ConfigureAwait(false);
        Observe("save", $"Added “{NewTitle.Trim()}”.");
        ClearDraft();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        Filter = ParseFilter(Filter).ToString();
        Observe("query", $"Showing {Filter.ToLowerInvariant()} tasks.");
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        if (Guid.TryParse(SelectedId, out Guid id))
        {
            await _service.ToggleAsync(id, cancellationToken).ConfigureAwait(false);
            Observe("save", "Changed task completion.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (Guid.TryParse(SelectedId, out Guid id))
        {
            await _service.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            Observe("save", "Deleted a task.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ToggleByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out Guid parsed))
        {
            await _service.ToggleAsync(parsed, cancellationToken).ConfigureAwait(false);
            Observe("save", "Changed task completion.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DeleteByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out Guid parsed))
        {
            await _service.DeleteAsync(parsed, cancellationToken).ConfigureAwait(false);
            Observe("save", "Deleted a task.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ClearCompletedAsync(CancellationToken cancellationToken)
    {
        await _service.ClearCompletedAsync(cancellationToken).ConfigureAwait(false);
        Observe("save", "Cleared completed tasks.");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private void Import()
    {
        if (_importTask is { IsCompleted: false })
        {
            return;
        }

        _importCancellation?.Dispose();
        _importCancellation = new CancellationTokenSource();
        IsImporting = true;
        Observe("async", "Starter-task import began.");
        OnPropertyChanged(nameof(IsImporting));
        _importTask = RunImportAsync(_importCancellation.Token);
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CancelImportAsync()
    {
        CancellationTokenSource? cancellation = _importCancellation;
        Task? import = _importTask;
        if (cancellation is null || import is null || import.IsCompleted)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await import.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // RunImportAsync records the safe cancellation outcome.
        }
    }

    [RelayCommand]
    private void RefreshImport()
    {
        // The delayed HTMX status action re-renders authoritative state after
        // the background import has either persisted or been cancelled.
        _ = IsImporting;
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task StartWizardAsync(CancellationToken cancellationToken)
    {
        await CloseFlowAsync().ConfigureAwait(false);
        _creationFlow = new TodoCreationFlow(_ => OnPropertyChanged(nameof(WizardStep)));
        await _creationFlow.StartAsync(cancellationToken).ConfigureAwait(false);
        Observe("flow", "Started the guided task workflow.");
        OnPropertyChanged(nameof(WizardStep));
        OnPropertyChanged(nameof(WizardIssues));
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task WizardNextAsync(CancellationToken cancellationToken)
    {
        if (_creationFlow is null)
        {
            return;
        }

        WorkflowTransition<TodoDraft> transition = await _creationFlow
            .NextAsync(NewTitle, NewNotes, ParsePriority(NewPriority), cancellationToken)
            .ConfigureAwait(false);
        Observe("flow", transition.Kind == WorkflowTransitionKind.Moved
            ? "Moved from Details to Review."
            : "Stayed on Details because validation failed.");
        OnPropertyChanged(nameof(WizardStep));
        OnPropertyChanged(nameof(WizardIssues));
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task WizardBackAsync(CancellationToken cancellationToken)
    {
        if (_creationFlow is not null)
        {
            await _creationFlow.BackAsync(cancellationToken).ConfigureAwait(false);
            Observe("flow", "Returned to the retained Details step.");
            OnPropertyChanged(nameof(WizardStep));
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task WizardFinishAsync(CancellationToken cancellationToken)
    {
        if (_creationFlow is null)
        {
            return;
        }

        WorkflowTransition<TodoDraft> transition = await _creationFlow
            .FinishAsync(cancellationToken)
            .ConfigureAwait(false);
        if (transition.Outcome is { Value: TodoDraft draft })
        {
            await _service.AddAsync(
                draft.Title,
                draft.Notes,
                draft.Priority,
                cancellationToken).ConfigureAwait(false);
            Observe("flow", "Finished the workflow and persisted its typed result.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            ClearDraft();
            await CloseFlowAsync().ConfigureAwait(false);
        }

        OnPropertyChanged(nameof(WizardStep));
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task WizardCancelAsync(CancellationToken cancellationToken)
    {
        if (_creationFlow is not null)
        {
            await _creationFlow.CancelAsync(cancellationToken).ConfigureAwait(false);
            Observe("flow", "Cancelled the guided workflow.");
            await CloseFlowAsync().ConfigureAwait(false);
            OnPropertyChanged(nameof(WizardStep));
        }
    }

    private async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        _items = await _service.GetAsync(cancellationToken).ConfigureAwait(false);
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(CompletedCount));
    }

    private async Task RunImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.ImportStarterTasksAsync(cancellationToken).ConfigureAwait(false);
            Observe("async", "Starter-task import completed.");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe("async", "Starter-task import was cancelled before persistence.");
            throw;
        }
        finally
        {
            IsImporting = false;
            OnPropertyChanged(nameof(IsImporting));
        }
    }

    private void ClearDraft()
    {
        NewTitle = string.Empty;
        NewNotes = string.Empty;
        NewPriority = nameof(TodoPriority.Normal);
        ClearErrors();
    }

    private void ValidateAllProperties() => ValidateNewTitle(NewTitle);

    private void ValidateNewTitle(string value)
    {
        string[] errors = string.IsNullOrWhiteSpace(value)
            ? ["A task title is required."]
            : value.Length is < 2 or > 120
                ? ["Task titles must contain between 2 and 120 characters."]
                : [];
        if (_newTitleErrors.SequenceEqual(errors, StringComparer.Ordinal))
        {
            return;
        }

        _newTitleErrors = errors;
        ErrorsChanged?.Invoke(
            this,
            new DataErrorsChangedEventArgs(nameof(NewTitle)));
        OnPropertyChanged(nameof(HasErrors));
    }

    private void ClearErrors()
    {
        if (_newTitleErrors.Length == 0)
        {
            return;
        }

        _newTitleErrors = [];
        ErrorsChanged?.Invoke(
            this,
            new DataErrorsChangedEventArgs(nameof(NewTitle)));
        OnPropertyChanged(nameof(HasErrors));
    }

    private void Observe(string category, string message)
    {
        _diagnostics.Insert(0, new DiagnosticEntry(DateTimeOffset.Now, category, message));
        if (_diagnostics.Count > 8)
        {
            _diagnostics.RemoveAt(_diagnostics.Count - 1);
        }

        OnPropertyChanged(nameof(Diagnostics));
    }

    private async ValueTask CloseFlowAsync()
    {
        TodoCreationFlow? flow = _creationFlow;
        _creationFlow = null;
        if (flow is not null)
        {
            await flow.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TodoPriority ParsePriority(string value) =>
        Enum.TryParse(value, ignoreCase: true, out TodoPriority parsed)
            ? parsed
            : TodoPriority.Normal;

    private static TodoFilter ParseFilter(string value) =>
        Enum.TryParse(value, ignoreCase: true, out TodoFilter parsed)
            ? parsed
            : TodoFilter.All;

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? importCancellation = _importCancellation;
        Task? importTask = _importTask;
        if (importCancellation is not null)
        {
            importCancellation.Cancel();
        }

        if (importTask is not null)
        {
            try
            {
                await importTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        importCancellation?.Dispose();
        await CloseFlowAsync().ConfigureAwait(false);
        _service.Dispose();
    }
}

internal sealed record DiagnosticEntry(DateTimeOffset At, string Category, string Message);

internal sealed record AdvancedTodoState(
    int TotalCount,
    int RemainingCount,
    int CompletedCount,
    bool IsImporting,
    string? WizardStep,
    IReadOnlyList<string> WizardIssues);
