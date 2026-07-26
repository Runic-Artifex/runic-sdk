using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Immutable presentation state consumed by the compiled advanced view.</summary>
public sealed class AdvancedTodoRenderModel
{
    private static readonly HtmxFieldHandle TitleField = new("title");

    private AdvancedTodoRenderModel(
        IReadOnlyList<AdvancedTodoRenderItem> items,
        IReadOnlyList<AdvancedTodoDiagnostic> diagnostics,
        IReadOnlyList<string> wizardIssues,
        int totalCount,
        int remainingCount,
        int completedCount,
        string query,
        string filter,
        string title,
        string notes,
        string priority,
        string? validationMessage,
        string? wizardStep,
        bool isImporting,
        AdvancedTodoRouteTable routes,
        long revision)
    {
        Items = items;
        Diagnostics = diagnostics;
        WizardIssues = wizardIssues;
        TotalCount = totalCount;
        RemainingCount = remainingCount;
        CompletedCount = completedCount;
        Query = query;
        Filter = filter;
        Title = title;
        Notes = notes;
        Priority = priority;
        ValidationMessage = validationMessage;
        WizardStep = wizardStep;
        IsImporting = isImporting;
        AddRoute = routes.Add;
        FilterRoute = routes.Filter;
        ToggleRoute = routes.Toggle;
        DeleteRoute = routes.Delete;
        ClearCompletedRoute = routes.ClearCompleted;
        ImportRoute = routes.Import;
        CancelImportRoute = routes.CancelImport;
        ImportStatusRoute = routes.ImportStatus;
        WizardStartRoute = routes.WizardStart;
        WizardNextRoute = routes.WizardNext;
        WizardBackRoute = routes.WizardBack;
        WizardFinishRoute = routes.WizardFinish;
        WizardCancelRoute = routes.WizardCancel;
        Revision = revision.ToString(CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<AdvancedTodoRenderItem> Items { get; }
    public IReadOnlyList<AdvancedTodoDiagnostic> Diagnostics { get; }
    public IReadOnlyList<string> WizardIssues { get; }
    public int TotalCount { get; }
    public int RemainingCount { get; }
    public int CompletedCount { get; }
    public int ShownCount => Items.Count;
    public bool HasItems => Items.Count != 0;
    public bool HasDiagnostics => Diagnostics.Count != 0;
    public string Query { get; }
    public string Filter { get; }
    public string Title { get; }
    public string Notes { get; }
    public string Priority { get; }
    public string? ValidationMessage { get; }
    public bool HasValidationMessage => ValidationMessage is not null;
    public string? WizardStep { get; }
    public bool IsImporting { get; }
    public bool WizardIsClosed => WizardStep is null;
    public bool WizardIsDetails =>
        StringComparer.Ordinal.Equals(WizardStep, TodoCreationFlow.DetailsStep.Value);
    public bool WizardIsReview =>
        StringComparer.Ordinal.Equals(WizardStep, TodoCreationFlow.ReviewStep.Value);
    public bool FilterIsAll => StringComparer.Ordinal.Equals(Filter, nameof(TodoFilter.All));
    public bool FilterIsActive => StringComparer.Ordinal.Equals(Filter, nameof(TodoFilter.Active));
    public bool FilterIsCompleted => StringComparer.Ordinal.Equals(Filter, nameof(TodoFilter.Completed));
    public bool PriorityIsLow => StringComparer.Ordinal.Equals(Priority, nameof(TodoPriority.Low));
    public bool PriorityIsNormal => StringComparer.Ordinal.Equals(Priority, nameof(TodoPriority.Normal));
    public bool PriorityIsHigh => StringComparer.Ordinal.Equals(Priority, nameof(TodoPriority.High));
    public string AddRoute { get; }
    public string FilterRoute { get; }
    public string ToggleRoute { get; }
    public string DeleteRoute { get; }
    public string ClearCompletedRoute { get; }
    public string ImportRoute { get; }
    public string CancelImportRoute { get; }
    public string ImportStatusRoute { get; }
    public string WizardStartRoute { get; }
    public string WizardNextRoute { get; }
    public string WizardBackRoute { get; }
    public string WizardFinishRoute { get; }
    public string WizardCancelRoute { get; }
    public string Revision { get; }

    internal static AdvancedTodoRenderModel Initial(
        TodoViewModel model,
        AdvancedTodoRouteTable routes,
        long revision) =>
        Create(model, routes, revision, [], null);

    internal static AdvancedTodoRenderModel Response(
        TodoViewModel model,
        AdvancedTodoRouteTable routes,
        HtmxRenderContext context) =>
        Create(
            model,
            routes,
            context.Revision,
            context.ValidationErrors,
            context.SubmittedValues);

    private static AdvancedTodoRenderModel Create(
        TodoViewModel model,
        AdvancedTodoRouteTable routes,
        long revision,
        IReadOnlyList<HtmxValidationError> validationErrors,
        IReadOnlyDictionary<HtmxFieldHandle, string>? submittedValues)
    {
        static string Submitted(
            IReadOnlyDictionary<HtmxFieldHandle, string>? values,
            string name,
            string fallback) =>
            values is not null &&
            values.TryGetValue(new HtmxFieldHandle(name), out string? value)
                ? value
                : fallback;

        string? validationMessage = validationErrors
            .FirstOrDefault(static error => error.Field == TitleField)
            ?.Message;
        return new AdvancedTodoRenderModel(
            model.VisibleItems.Select(static item => new AdvancedTodoRenderItem(item)).ToArray(),
            model.Diagnostics.Take(5).Select(static entry => new AdvancedTodoDiagnostic(entry)).ToArray(),
            model.WizardIssues.Select(static issue => issue.Message).ToArray(),
            model.TotalCount,
            model.RemainingCount,
            model.CompletedCount,
            Submitted(submittedValues, "query", model.Query),
            Submitted(submittedValues, "filter", model.Filter),
            Submitted(submittedValues, "title", model.NewTitle),
            Submitted(submittedValues, "notes", model.NewNotes),
            Submitted(submittedValues, "priority", model.NewPriority),
            validationMessage,
            model.WizardStep?.Value,
            model.IsImporting,
            routes,
            revision);
    }
}

/// <summary>Encoded task-row state for compiled rendering.</summary>
public sealed class AdvancedTodoRenderItem
{
    internal AdvancedTodoRenderItem(TodoItem item)
    {
        Id = item.Id.ToString("D");
        Title = item.Title;
        Notes = item.Notes;
        Priority = item.Priority.ToString();
        IsCompleted = item.IsCompleted;
        Created = item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public string Id { get; }
    public string Title { get; }
    public string Notes { get; }
    public bool HasNotes => Notes.Length != 0;
    public string Priority { get; }
    public bool IsCompleted { get; }
    public string Created { get; }
    public string CssClass => IsCompleted ? "todo completed" : "todo";
    public string PriorityCssClass => $"priority {Priority.ToLowerInvariant()}";
    public string ToggleLabel => IsCompleted ? "Mark active" : "Mark complete";
    public string ToggleIconClass => IsCompleted
        ? "fa-solid fa-check"
        : "fa-regular fa-circle";
    public string DeleteLabel => $"Delete {Title}";
}

/// <summary>Bounded diagnostic state for compiled rendering.</summary>
public sealed class AdvancedTodoDiagnostic
{
    internal AdvancedTodoDiagnostic(DiagnosticEntry entry)
    {
        Time = entry.At.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        Category = entry.Category;
        Message = entry.Message;
    }

    public string Time { get; }
    public string Category { get; }
    public string Message { get; }
}

/// <summary>Initial-document state around the same compiled application fragment.</summary>
public sealed class AdvancedTodoDocumentModel
{
    public AdvancedTodoDocumentModel(IHtmlRenderable application)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public IHtmlRenderable Application { get; }
}

internal sealed class AdvancedTodoRouteTable
{
    private readonly Dictionary<string, string> routes = new(StringComparer.Ordinal);

    internal string Add => Get("add");
    internal string Filter => Get("filter");
    internal string Toggle => Get("toggle");
    internal string Delete => Get("delete");
    internal string ClearCompleted => Get("clear-completed");
    internal string Import => Get("import");
    internal string CancelImport => Get("cancel-import");
    internal string ImportStatus => Get("import-status");
    internal string WizardStart => Get("wizard-start");
    internal string WizardNext => Get("wizard-next");
    internal string WizardBack => Get("wizard-back");
    internal string WizardFinish => Get("wizard-finish");
    internal string WizardCancel => Get("wizard-cancel");

    internal void Initialize(HtmxOpenedView opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        if (routes.Count != 0)
        {
            throw new InvalidOperationException("Advanced ToDo routes can be initialized only once.");
        }

        foreach (string handle in Handles)
        {
            routes.Add(handle, opened.ActionRoutes[new HtmxActionHandle(handle)]);
        }
    }

    internal static IReadOnlyList<string> Handles { get; } =
    [
        "add",
        "filter",
        "toggle",
        "delete",
        "clear-completed",
        "import",
        "cancel-import",
        "import-status",
        "wizard-start",
        "wizard-next",
        "wizard-back",
        "wizard-finish",
        "wizard-cancel",
    ];

    private string Get(string handle) =>
        routes.TryGetValue(handle, out string? route)
            ? route
            : throw new InvalidOperationException(
                "The endpoint must assign closed routes before the advanced view renders.");
}
