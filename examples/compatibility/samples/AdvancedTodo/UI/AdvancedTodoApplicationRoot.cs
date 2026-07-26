using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Owns the compiled view, closed routes, native transport, and private web root.</summary>
internal sealed class AdvancedTodoApplicationRoot : IRootSessionFactory, IAsyncDisposable
{
    internal const string AllowedOrigin = "https://advanced-todo.native";
    internal static readonly MvvmContract Contract = new("samples.advanced-todo");
    private static readonly string[] RequiredAssetPaths =
    [
        "vendor/bootstrap/bootstrap.min.css",
        "vendor/fontawesome/css/fontawesome.min.css",
        "_content/WebUIToolkit.MVVM.Html.Htmx.CsWebUi/webuitoolkit-htmx-cswebui-1.0.0.js",
        "_content/WebUIToolkit.MVVM.Html.Htmx.Js/htmx-2.0.10.min.js",
        "_content/WebUIToolkit.MVVM.Html.Htmx.Js/htmx-csp-2.0.10.js",
        "_content/WebUIToolkit.MVVM.Html.Htmx.Js/webuitoolkit-htmx-1.0.0.mjs",
    ];

    private readonly TodoService service;
    private readonly AdvancedTodoRouteTable routes = new();
    private HtmxEndpointRuntime? runtime;
    private HtmxOpenedView? opened;
    private CsWebUiHtmxTransport? transport;
    private AdvancedTodoRuntimeAssets? assets;
    private TodoViewModel? model;
    private CommunityToolkitMvvmBindingAdapter<TodoViewModel>? adapter;
    private string? initialDocument;
    private int windowConfigured;
    private int rootOpened;
    private int disposed;

    internal AdvancedTodoApplicationRoot(TodoService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    internal string WebRoot =>
        assets?.Root ??
        throw new InvalidOperationException("Prepare AdvancedTodo before creating its host.");

    internal async ValueTask PrepareAsync(
        string staticWebRoot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (runtime is not null)
        {
            throw new InvalidOperationException("AdvancedTodo is already prepared.");
        }

        model = new TodoViewModel(service);
        await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var registry = new MvvmSessionRegistry();
        registry.Map(Contract, _ =>
        {
            TodoViewModel activeModel = model ??
                throw new InvalidOperationException("The advanced model is unavailable.");
            CommunityToolkitMvvmBindingAdapter<TodoViewModel> createdAdapter =
                new CommunityToolkitMvvmAdapterBuilder<TodoViewModel>(activeModel)
                    .BindProperty(
                        1,
                        nameof(TodoViewModel.NewTitle),
                        static state => state.NewTitle,
                        static (state, value) => state.NewTitle = value,
                        AdvancedTodoJsonContext.Default.String,
                        includeValidation: true)
                    .BindProperty(
                        2,
                        nameof(TodoViewModel.NewNotes),
                        static state => state.NewNotes,
                        static (state, value) => state.NewNotes = value,
                        AdvancedTodoJsonContext.Default.String)
                    .BindProperty(
                        3,
                        nameof(TodoViewModel.NewPriority),
                        static state => state.NewPriority,
                        static (state, value) => state.NewPriority = value,
                        AdvancedTodoJsonContext.Default.String)
                    .BindProperty(
                        4,
                        nameof(TodoViewModel.Query),
                        static state => state.Query,
                        static (state, value) => state.Query = value,
                        AdvancedTodoJsonContext.Default.String)
                    .BindProperty(
                        5,
                        nameof(TodoViewModel.Filter),
                        static state => state.Filter,
                        static (state, value) => state.Filter = value,
                        AdvancedTodoJsonContext.Default.String)
                    .BindProperty(
                        6,
                        nameof(TodoViewModel.SelectedId),
                        static state => state.SelectedId,
                        static (state, value) => state.SelectedId = value,
                        AdvancedTodoJsonContext.Default.String)
                    .BindAsyncCommand(101, nameof(TodoViewModel.AddCommand), static state => state.AddCommand)
                    .BindCommand(102, nameof(TodoViewModel.ApplyFilterCommand), static state => state.ApplyFilterCommand)
                    .BindAsyncCommand(103, nameof(TodoViewModel.ToggleCommand), static state => state.ToggleCommand)
                    .BindAsyncCommand(104, nameof(TodoViewModel.DeleteCommand), static state => state.DeleteCommand)
                    .BindAsyncCommand(105, nameof(TodoViewModel.ClearCompletedCommand), static state => state.ClearCompletedCommand)
                    .BindCommand(106, nameof(TodoViewModel.ImportCommand), static state => state.ImportCommand)
                    .BindAsyncCommand(107, nameof(TodoViewModel.StartWizardCommand), static state => state.StartWizardCommand)
                    .BindAsyncCommand(108, nameof(TodoViewModel.WizardNextCommand), static state => state.WizardNextCommand)
                    .BindAsyncCommand(109, nameof(TodoViewModel.WizardBackCommand), static state => state.WizardBackCommand)
                    .BindAsyncCommand(110, nameof(TodoViewModel.WizardFinishCommand), static state => state.WizardFinishCommand)
                    .BindAsyncCommand(111, nameof(TodoViewModel.WizardCancelCommand), static state => state.WizardCancelCommand)
                    .BindAsyncCommand(112, nameof(TodoViewModel.CancelImportCommand), static state => state.CancelImportCommand)
                    .BindCommand(113, nameof(TodoViewModel.RefreshImportCommand), static state => state.RefreshImportCommand)
                    .Build();
            adapter = createdAdapter;
            return ValueTask.FromResult(new MvvmSessionActivation(createdAdapter));
        });

        HtmxEndpointRuntime createdRuntime = new(
            registry.Build(),
            new HtmxEndpointOptions(
                AllowedOrigin,
                idleTimeout: TimeSpan.FromMinutes(15),
                maximumBodyBytes: 16 * 1024,
                maximumFields: 8,
                maximumFieldBytes: 4 * 1024,
                maximumResponseBytes: 512 * 1024));
        runtime = createdRuntime;
        try
        {
            opened = await createdRuntime
                .OpenAsync(CreateDescriptor(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            routes.Initialize(opened);
            AdvancedTodoAppView application = new(
                AdvancedTodoRenderModel.Initial(model, routes, opened.Revision));
            initialDocument = "<!doctype html>" + Render(
                new AdvancedTodoDocumentView(new AdvancedTodoDocumentModel(application)));
            assets = await AdvancedTodoRuntimeAssets
                .CreateAsync(staticWebRoot, initialDocument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void ConfigureWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref windowConfigured, 1) != 0)
        {
            throw new InvalidOperationException("AdvancedTodo supports exactly one native window.");
        }

        try
        {
            transport = new CsWebUiHtmxTransport(
                window,
                runtime ?? throw new InvalidOperationException("Prepare AdvancedTodo first."),
                opened ?? throw new InvalidOperationException("Prepare AdvancedTodo first."),
                new CsWebUiHtmxTransportOptions(
                    AllowedOrigin,
                    maximumRequestBytes: 16 * 1024,
                    maximumResponseBytes: 512 * 1024,
                    maximumFields: 8,
                    maximumFieldBytes: 4 * 1024));
        }
        catch
        {
            Volatile.Write(ref windowConfigured, 0);
            throw;
        }
    }

    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (transport is null)
        {
            throw new InvalidOperationException(
                "CsWebUi must attach the native HTMX transport before the root opens.");
        }

        if (Interlocked.Exchange(ref rootOpened, 1) != 0)
        {
            throw new InvalidOperationException("AdvancedTodo supports exactly one root session.");
        }

        return ValueTask.FromResult<IRootSession>(new RootSession(this));
    }

    internal async Task<int> RunSelfTestAsync()
    {
        HtmxEndpointRuntime selectedRuntime = runtime ??
            throw new InvalidOperationException("Prepare AdvancedTodo before self-testing.");
        HtmxOpenedView selectedView = opened ??
            throw new InvalidOperationException("Prepare AdvancedTodo before self-testing.");
        TodoViewModel activeModel = model ??
            throw new InvalidOperationException("Prepare AdvancedTodo before self-testing.");

        long revision = selectedView.Revision;
        CaptureTransport invalid = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Add,
            revision,
            [new("title", "x"), new("notes", ""), new("priority", "Normal")]);
        revision = Revision(invalid);
        CaptureTransport added = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Add,
            revision,
            [
                new("title", "Persisted through an opaque route"),
                new("notes", "Rendered by compiled cwhtml"),
                new("priority", "High"),
            ]);
        revision = Revision(added);
        TodoItem? addedItem = activeModel.VisibleItems.FirstOrDefault(
            static item => item.Title == "Persisted through an opaque route");
        CaptureTransport filtered = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Filter,
            revision,
            [new("query", "opaque"), new("filter", "Active")]);
        revision = Revision(filtered);
        CaptureTransport toggled = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Toggle,
            revision,
            [new("selectedId", addedItem?.Id.ToString("D") ?? "")]);
        revision = Revision(toggled);
        CaptureTransport clearFilter = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Filter,
            revision,
            [new("query", ""), new("filter", "All")]);
        revision = Revision(clearFilter);
        CaptureTransport wizardStart = await PostAsync(
            selectedRuntime, selectedView, routes.WizardStart, revision, []);
        revision = Revision(wizardStart);
        CaptureTransport wizardInvalid = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.WizardNext,
            revision,
            [new("title", ""), new("notes", ""), new("priority", "Normal")]);
        revision = Revision(wizardInvalid);
        CaptureTransport wizardReview = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.WizardNext,
            revision,
            [
                new("title", "Planned through Flow"),
                new("notes", "Review and retain this draft"),
                new("priority", "Low"),
            ]);
        revision = Revision(wizardReview);
        CaptureTransport wizardBack = await PostAsync(
            selectedRuntime, selectedView, routes.WizardBack, revision, []);
        revision = Revision(wizardBack);
        CaptureTransport wizardReviewAgain = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.WizardNext,
            revision,
            [
                new("title", "Planned through Flow"),
                new("notes", "Review and retain this draft"),
                new("priority", "Low"),
            ]);
        revision = Revision(wizardReviewAgain);
        CaptureTransport wizardFinish = await PostAsync(
            selectedRuntime, selectedView, routes.WizardFinish, revision, []);
        revision = Revision(wizardFinish);

        CaptureTransport importStarted = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.Import,
            revision,
            []);
        revision = Revision(importStarted);
        await Task.Delay(150).ConfigureAwait(false);
        CaptureTransport cancelled = await PostAsync(
            selectedRuntime,
            selectedView,
            routes.CancelImport,
            revision,
            []);

        IReadOnlyList<TodoItem> persisted = await service
            .GetAsync(CancellationToken.None)
            .ConfigureAwait(false);
        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
            .BuildFromDirectory(WebRoot, "index.html");
        var provider = new DirectoryFrontendAssetProvider(WebRoot, manifest);
        await provider.ValidateAsync(CancellationToken.None).ConfigureAwait(false);

        string[] forbidden =
        [
            string.Concat("webui", ".call"),
            string.Concat("todo", "Snapshot"),
            string.Concat("todo", "Add"),
            string.Concat("todo", "Filter"),
            string.Concat("todo", "Toggle"),
            string.Concat("todo", "Delete"),
            string.Concat("todo", "Import"),
        ];
        bool generatedAssetsAreClean = Directory
            .EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path) is ".html" or ".js" or ".mjs")
            .Select(File.ReadAllText)
            .All(content => forbidden.All(value =>
                !content.Contains(value, StringComparison.Ordinal)));
        string renderedRouteSurface = string.Concat(
            initialDocument,
            added.Body,
            filtered.Body,
            wizardStart.Body,
            wizardInvalid.Body,
            wizardReview.Body,
            wizardBack.Body,
            wizardFinish.Body,
            importStarted.Body,
            cancelled.Body);
        bool routesAreOpaque = AdvancedTodoRouteTable.Handles.All(handle =>
            renderedRouteSurface.Contains(
                opened.ActionRoutes[new HtmxActionHandle(handle)],
                StringComparison.Ordinal));
        bool localAssetsPresent = RequiredAssetPaths.All(
            path => File.Exists(Path.Combine(WebRoot, path)));
        bool commandBindingsPresent = adapter?.Metadata.Count(static metadata =>
            metadata.Kind == MvvmBindingMemberKind.Command) == 13;
        string generatedRoot = WebRoot;
        await DisposeAsync().ConfigureAwait(false);
        bool generatedRootRemoved = !Directory.Exists(generatedRoot);
        bool passed =
            initialDocument?.StartsWith("<!doctype html><html", StringComparison.Ordinal) == true &&
            initialDocument.Contains(CsWebUiHtmxTransport.BrowserBridgePath, StringComparison.Ordinal) &&
            invalid.Body.Contains("between 2 and 120 characters", StringComparison.Ordinal) &&
            added.Body.Contains("Persisted through an opaque route", StringComparison.Ordinal) &&
            filtered.Body.Contains("Persisted through an opaque route", StringComparison.Ordinal) &&
            wizardInvalid.Body.Contains("Give the task a title before continuing.", StringComparison.Ordinal) &&
            wizardReview.Body.Contains("2 Review", StringComparison.Ordinal) &&
            wizardBack.Body.Contains("Continue to review", StringComparison.Ordinal) &&
            wizardReviewAgain.Body.Contains("Review and retain this draft", StringComparison.Ordinal) &&
            wizardFinish.Body.Contains("Planned through Flow", StringComparison.Ordinal) &&
            importStarted.Body.Contains("Cancel running import", StringComparison.Ordinal) &&
            cancelled.Body.Contains("cancelled before persistence", StringComparison.Ordinal) &&
            persisted.Any(static item =>
                item.Title == "Persisted through an opaque route" &&
                item.IsCompleted) &&
            persisted.Any(static item => item.Title == "Planned through Flow") &&
            !persisted.Any(static item => item.Title == "Explore the guided creation flow") &&
            generatedAssetsAreClean &&
            routesAreOpaque &&
            localAssetsPresent &&
            commandBindingsPresent &&
            generatedRootRemoved &&
            StringComparer.Ordinal.Equals(CsWebUiHtmxTransport.BindingName, "webuitoolkitHtmx");
        Console.WriteLine(passed
            ? "Advanced ToDo compiled native HTMX self-test passed."
            : "Advanced ToDo compiled native HTMX self-test failed.");
        if (!passed)
        {
            Console.Error.WriteLine(
                $"clean={generatedAssetsAreClean}, routes={routesAreOpaque}, assets={localAssetsPresent}, bindings={commandBindingsPresent}, removed={generatedRootRemoved}, persisted={persisted.Count}{Environment.NewLine}" +
                $"invalid={invalid.StatusCode}:{invalid.Body}{Environment.NewLine}" +
                $"wizardInvalid={wizardInvalid.StatusCode}:{wizardInvalid.Body}{Environment.NewLine}" +
                $"importStarted={importStarted.StatusCode}:{importStarted.Body}{Environment.NewLine}" +
                $"cancel={cancelled.StatusCode}:{cancelled.Body}");
        }

        return passed ? 0 : 1;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CsWebUiHtmxTransport? ownedTransport = Interlocked.Exchange(ref transport, null);
        HtmxOpenedView? ownedView = Interlocked.Exchange(ref opened, null);
        HtmxEndpointRuntime? ownedRuntime = Interlocked.Exchange(ref runtime, null);
        AdvancedTodoRuntimeAssets? ownedAssets = Interlocked.Exchange(ref assets, null);
        try
        {
            if (ownedTransport is not null)
            {
                await ownedTransport.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (ownedView is not null)
                {
                    await ownedView.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (ownedRuntime is not null)
                    {
                        await ownedRuntime.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (ownedAssets is not null)
                    {
                        await ownedAssets.DisposeAsync().ConfigureAwait(false);
                    }

                    TodoViewModel? ownedModel = Interlocked.Exchange(ref model, null);
                    if (ownedModel is not null)
                    {
                        await ownedModel.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private HtmxViewDescriptor CreateDescriptor()
    {
        TodoViewModel ActiveModel() =>
            model ?? throw new InvalidOperationException("The advanced model is not active.");
        HtmxFragmentDescriptor application = HtmxCompiledTemplateProjection.CreateFragment(
            new HtmxFragmentHandle("advanced_fragment"),
            context => new AdvancedTodoAppView(
                AdvancedTodoRenderModel.Response(ActiveModel(), routes, context)));
        var complete = new HtmxRenderPlan(application, events: ["todo:changed"]);
        var initial = new HtmxRenderPlan(application);
        var addInvalid = new HtmxRenderPlan(
            application,
            focus: new HtmxDomHandle("new-title"));
        var wizardInvalid = new HtmxRenderPlan(
            application,
            focus: new HtmxDomHandle("wizard-title"));

        HtmxFieldDescriptor StringField(string handle, int memberId) =>
            new(
                new HtmxFieldHandle(handle),
                memberId,
                static (value, _) => HtmxConversionResult.Success(
                    JsonSerializer.SerializeToElement(value, AdvancedTodoJsonContext.Default.String)));
        HtmxFieldDescriptor title = new(
            new HtmxFieldHandle("title"),
            1,
            static (value, _) => HtmxConversionResult.Success(
                JsonSerializer.SerializeToElement(value, AdvancedTodoJsonContext.Default.String)),
            [
                static (value, _) =>
                {
                    int length = value.GetString()?.Trim().Length ?? 0;
                    IReadOnlyList<string> messages = length is >= 2 and <= 120
                        ? []
                        : ["Give the task a title between 2 and 120 characters."];
                    return ValueTask.FromResult(messages);
                },
            ]);
        HtmxFieldDescriptor wizardTitle = StringField("title", 1);
        HtmxFieldDescriptor notes = StringField("notes", 2);
        HtmxFieldDescriptor priority = new(
            new HtmxFieldHandle("priority"),
            3,
            static (value, _) => HtmxConversionResult.Success(
                JsonSerializer.SerializeToElement(value, AdvancedTodoJsonContext.Default.String)),
            [
                static (value, _) =>
                {
                    bool valid = Enum.TryParse(
                        value.GetString(),
                        ignoreCase: true,
                        out TodoPriority _);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        valid ? [] : ["Choose Low, Normal, or High priority."]);
                },
            ]);
        HtmxFieldDescriptor query = StringField("query", 4);
        HtmxFieldDescriptor filter = new(
            new HtmxFieldHandle("filter"),
            5,
            static (value, _) => HtmxConversionResult.Success(
                JsonSerializer.SerializeToElement(value, AdvancedTodoJsonContext.Default.String)),
            [
                static (value, _) =>
                {
                    bool valid = Enum.TryParse(
                        value.GetString(),
                        ignoreCase: true,
                        out TodoFilter _);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        valid ? [] : ["Choose All, Active, or Completed."]);
                },
            ]);
        HtmxFieldDescriptor selected = new(
            new HtmxFieldHandle("selectedId"),
            6,
            static (value, _) => HtmxConversionResult.Success(
                JsonSerializer.SerializeToElement(value, AdvancedTodoJsonContext.Default.String)),
            [
                (value, _) =>
                {
                    bool exists = Guid.TryParse(value.GetString(), out Guid id) &&
                        ActiveModel().VisibleItems.Any(item => item.Id == id);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        exists ? [] : ["That task is no longer available."]);
                },
            ]);
        static ValueTask<bool> Enabled(
            IReadOnlyDictionary<HtmxFieldHandle, JsonElement> _,
            CancellationToken __) => ValueTask.FromResult(true);

        HtmxActionDescriptor Action(
            string handle,
            int command,
            IReadOnlyList<HtmxFieldDescriptor> fields,
            HtmxRenderPlan success,
            HtmxRenderPlan invalid) =>
            new(new HtmxActionHandle(handle), command, fields, Enabled, success, invalid, initial);
        return new HtmxViewDescriptor(
            new HtmxViewHandle("advanced-todo"),
            Contract,
            [
                Action("add", 101, [title, notes, priority], complete, addInvalid),
                Action("filter", 102, [query, filter], initial, initial),
                Action("toggle", 103, [selected], complete, initial),
                Action("delete", 104, [selected], complete, initial),
                Action("clear-completed", 105, [], complete, initial),
                Action("import", 106, [], complete, initial),
                Action("wizard-start", 107, [], initial, initial),
                Action("wizard-next", 108, [wizardTitle, notes, priority], initial, wizardInvalid),
                Action("wizard-back", 109, [], initial, initial),
                Action("wizard-finish", 110, [], complete, initial),
                Action("wizard-cancel", 111, [], initial, initial),
                Action("cancel-import", 112, [], initial, initial),
                Action("import-status", 113, [], initial, initial),
            ],
            initial);
    }

    private static HtmxEndpointRequest Request(
        HtmxOpenedView view,
        string method,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form,
        Guid? requestId = null,
        Guid? targetRequestId = null) =>
        new(
            method,
            route,
            isHtmx: true,
            AllowedOrigin,
            view.SessionCookie,
            view.AntiForgeryToken,
            view.AntiForgeryToken,
            view.Capability,
            revision,
            form,
            bodyByteCount: form.Sum(static value =>
                Encoding.UTF8.GetByteCount(value.Name) +
                Encoding.UTF8.GetByteCount(value.Value)),
            CultureInfo.InvariantCulture,
            requestId,
            targetRequestId);

    private static Task<CaptureTransport> PostAsync(
        HtmxEndpointRuntime runtime,
        HtmxOpenedView view,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form,
        Guid? requestId = null) =>
        SendAsync(runtime, Request(view, "POST", route, revision, form, requestId));

    private static async Task<CaptureTransport> SendAsync(
        HtmxEndpointRuntime runtime,
        HtmxEndpointRequest request)
    {
        var capture = new CaptureTransport();
        await runtime.HandleAsync(request, capture).ConfigureAwait(false);
        return capture;
    }

    private static long Revision(CaptureTransport response) =>
        long.Parse(
            response.Headers["X-WebUI-Revision"],
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static string Render(AdvancedTodoDocumentView view)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new Utf8HtmlWriter(output);
        view.Render(ref writer, new TemplateContext(CultureInfo.InvariantCulture));
        writer.Complete();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private sealed class RootSession(AdvancedTodoApplicationRoot owner) : IRootSession
    {
        private AdvancedTodoApplicationRoot? owner = owner;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            AdvancedTodoApplicationRoot? selected = Interlocked.Exchange(ref owner, null);
            if (selected is not null)
            {
                await selected.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CaptureTransport : IHtmxEndpointTransport
    {
        private HtmxEndpointResponse? response;

        internal int StatusCode => response?.StatusCode ?? 0;
        internal string Body => response is null
            ? string.Empty
            : Encoding.UTF8.GetString(response.Body.Span);
        internal IReadOnlyDictionary<string, string> Headers =>
            response?.Headers ?? new Dictionary<string, string>();

        public ValueTask WriteAsync(
            HtmxEndpointResponse value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = value;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class AdvancedTodoRuntimeAssets : IAsyncDisposable
{
    private int disposed;

    private AdvancedTodoRuntimeAssets(string root)
    {
        Root = root;
    }

    internal string Root { get; }

    internal static async ValueTask<AdvancedTodoRuntimeAssets> CreateAsync(
        string staticRoot,
        string initialDocument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staticRoot);
        ArgumentNullException.ThrowIfNull(initialDocument);
        if (!Directory.Exists(staticRoot))
        {
            throw new DirectoryNotFoundException(
                $"The AdvancedTodo assets were not copied to '{staticRoot}'.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-advanced-todo-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = new AdvancedTodoRuntimeAssets(root);
        try
        {
            CopyDirectory(staticRoot, root, cancellationToken);
            await File.WriteAllTextAsync(
                    Path.Combine(root, "index.html"),
                    initialDocument,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (string directory in Directory.EnumerateDirectories(
            sourceRoot,
            "*",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(
            sourceRoot,
            "*",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, file);
            if (StringComparer.OrdinalIgnoreCase.Equals(relative, "index.html") ||
                StringComparer.OrdinalIgnoreCase.Equals(relative, "advanced-todo.js"))
            {
                continue;
            }

            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }
}
