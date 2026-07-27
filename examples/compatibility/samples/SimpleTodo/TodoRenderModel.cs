using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Immutable state consumed only by the compiled todo fragment.</summary>
public sealed class TodoRenderModel
{
    private static readonly HtmxFieldHandle TitleField = new("title");

    private TodoRenderModel(
        IReadOnlyList<TodoRenderItem> items,
        string draft,
        string? validationMessage,
        string addRoute,
        string toggleRoute,
        string removeRoute,
        long revision)
    {
        Items = items;
        Draft = draft;
        ValidationMessage = validationMessage;
        AddRoute = addRoute;
        ToggleRoute = toggleRoute;
        RemoveRoute = removeRoute;
        Revision = revision.ToString(CultureInfo.InvariantCulture);
        Remaining = items.Count(static item => !item.IsCompleted);
        Completed = items.Count - Remaining;
    }

    /// <summary>Gets the encoded task rows.</summary>
    public IReadOnlyList<TodoRenderItem> Items { get; }

    /// <summary>Gets the authoritative or rejected composer value.</summary>
    public string Draft { get; }

    /// <summary>Gets a bounded validation message for the composer.</summary>
    public string? ValidationMessage { get; }

    /// <summary>Gets whether a validation message is present.</summary>
    public bool HasValidationMessage => ValidationMessage is not null;

    /// <summary>Gets whether the list is empty.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Gets the opaque add action route.</summary>
    public string AddRoute { get; }

    /// <summary>Gets the opaque toggle action route.</summary>
    public string ToggleRoute { get; }

    /// <summary>Gets the opaque remove action route.</summary>
    public string RemoveRoute { get; }

    /// <summary>Gets the authoritative revision without losing JavaScript precision.</summary>
    public string Revision { get; }

    /// <summary>Gets the number of active tasks.</summary>
    public int Remaining { get; }

    /// <summary>Gets the number of completed tasks.</summary>
    public int Completed { get; }

    /// <summary>Creates initial render state after the endpoint has assigned closed routes.</summary>
    internal static TodoRenderModel Initial(
        TodoViewModel model,
        TodoRouteTable routes,
        long revision) =>
        Create(model, routes, revision, validationErrors: [], submittedValues: null);

    /// <summary>Creates response render state from the bounded endpoint context.</summary>
    internal static TodoRenderModel Response(
        TodoViewModel model,
        TodoRouteTable routes,
        HtmxRenderContext context) =>
        Create(
            model,
            routes,
            context.Revision,
            context.ValidationErrors,
            context.SubmittedValues);

    private static TodoRenderModel Create(
        TodoViewModel model,
        TodoRouteTable routes,
        long revision,
        IReadOnlyList<HtmxValidationError> validationErrors,
        IReadOnlyDictionary<HtmxFieldHandle, string>? submittedValues)
    {
        string draft = submittedValues is not null &&
            submittedValues.TryGetValue(TitleField, out string? submitted)
                ? submitted
                : model.NewTitle;
        string? validationMessage = validationErrors
            .FirstOrDefault(static error => error.Field == TitleField)
            ?.Message;
        return new TodoRenderModel(
            model.Items
                .Select(static item => new TodoRenderItem(
                    item.Id.ToString("D"),
                    item.Title,
                    item.IsCompleted))
                .ToArray(),
            draft,
            validationMessage,
            routes.Add,
            routes.Toggle,
            routes.Remove,
            revision);
    }
}

/// <summary>Encoded task-row state used by the compiled view.</summary>
public sealed class TodoRenderItem
{
    internal TodoRenderItem(string id, string title, bool isCompleted)
    {
        Id = id;
        Title = title;
        IsCompleted = isCompleted;
    }

    /// <summary>Gets the opaque task identifier submitted to a closed action.</summary>
    public string Id { get; }

    /// <summary>Gets the task text.</summary>
    public string Title { get; }

    /// <summary>Gets whether the task is complete.</summary>
    public bool IsCompleted { get; }

    /// <summary>Gets the fixed Bootstrap row classes.</summary>
    public string CssClass => IsCompleted
        ? "task completed list-group-item"
        : "task list-group-item";

    /// <summary>Gets the toggle button's accessible label.</summary>
    public string ToggleLabel => IsCompleted ? "Mark as active" : "Mark as complete";

    /// <summary>Gets the fixed Font Awesome toggle class.</summary>
    public string ToggleIconClass => IsCompleted
        ? "fa-solid fa-check"
        : "fa-solid fa-circle";

    /// <summary>Gets the remove button's accessible label.</summary>
    public string RemoveLabel => $"Remove {Title}";
}

/// <summary>Immutable state for the compiled initial document.</summary>
public sealed class TodoDocumentModel
{
    /// <summary>Creates a document around the same compiled fragment used by responses.</summary>
    public TodoDocumentModel(
        IHtmlRenderable application,
        FrontendDevelopmentAssets assets)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    /// <summary>Gets the compiled application fragment.</summary>
    public IHtmlRenderable Application { get; }

    /// <summary>Gets production or development-only Vite asset references.</summary>
    public FrontendDevelopmentAssets Assets { get; }
}

internal sealed class TodoRouteTable
{
    private string? add;
    private string? toggle;
    private string? remove;

    internal string Add => add ?? throw NotInitialized();
    internal string Toggle => toggle ?? throw NotInitialized();
    internal string Remove => remove ?? throw NotInitialized();

    internal void Initialize(HtmxOpenedView opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        if (add is not null || toggle is not null || remove is not null)
        {
            throw new InvalidOperationException("Todo routes can be initialized only once.");
        }

        add = opened.ActionRoutes[new HtmxActionHandle("add")];
        toggle = opened.ActionRoutes[new HtmxActionHandle("toggle")];
        remove = opened.ActionRoutes[new HtmxActionHandle("remove")];
    }

    private static InvalidOperationException NotInitialized() =>
        new("The endpoint must assign closed routes before the todo view renders.");
}
