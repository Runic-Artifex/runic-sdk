using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>
/// Owns the compiled view, closed endpoint routes, native transport, and generated web root.
/// </summary>
internal sealed class TodoApplicationRoot : IRootSessionFactory, IAsyncDisposable
{
    internal const string AllowedOrigin = "https://simple-todo.native";
    internal static readonly MvvmContract Contract = new("samples.simple-todo");
    private static readonly string[] RequiredAssetPaths =
    [
        "cwhtml.css",
        "cwhtml.js",
        "webuitoolkit.assets.json",
    ];

    private readonly TodoRouteTable routes = new();
    private CsWebUiHtmxApplication? htmxApplication;
    private TodoRuntimeAssets? assets;
    private TodoViewModel? model;
    private CommunityToolkitMvvmBindingAdapter<TodoViewModel>? adapter;
    private string? initialDocument;
    private int windowConfigured;
    private int rootOpened;
    private int disposed;

    /// <summary>Gets the private generated root after preparation.</summary>
    internal string WebRoot =>
        assets?.Root ??
        throw new InvalidOperationException("Prepare the todo application before creating its host.");

    /// <summary>
    /// Opens the MVVM/HTMX view, assigns opaque routes, and compiles the initial document
    /// into an owned static root before CsWebUi starts serving files.
    /// </summary>
    internal async ValueTask PrepareAsync(
        string staticWebRoot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (htmxApplication is not null)
        {
            throw new InvalidOperationException("The todo application is already prepared.");
        }

        try
        {
            HtmxViewDescriptor descriptor = CreateDescriptor();
            CsWebUiHtmxApplication createdApplication =
                await new CsWebUiHtmxApplicationBuilder(Contract, AllowedOrigin)
                    .Activate(_ =>
                    {
                        model = new TodoViewModel();
                        CommunityToolkitMvvmBindingAdapter<TodoViewModel> adapter =
                            new CommunityToolkitMvvmAdapterBuilder<TodoViewModel>(model)
                                .BindProperty(
                                    1,
                                    nameof(TodoViewModel.NewTitle),
                                    static state => state.NewTitle,
                                    static (state, value) => state.NewTitle = value,
                                    TodoJsonContext.Default.String)
                                .BindProperty(
                                    2,
                                    nameof(TodoViewModel.SelectedId),
                                    static state => state.SelectedId,
                                    static (state, value) => state.SelectedId = value,
                                    TodoJsonContext.Default.String)
                                .BindCommand(
                                    3,
                                    nameof(TodoViewModel.AddCommand),
                                    static state => state.AddCommand)
                                .BindCommand(
                                    4,
                                    nameof(TodoViewModel.ToggleCommand),
                                    static state => state.ToggleCommand)
                                .BindCommand(
                                    5,
                                    nameof(TodoViewModel.RemoveCommand),
                                    static state => state.RemoveCommand)
                                .BindCollection(
                                    6,
                                    nameof(TodoViewModel.Items),
                                    static state => state.Items,
                                    TodoJsonContext.Default.TodoItem)
                                .Build();
                        this.adapter = adapter;
                        return ValueTask.FromResult(new MvvmSessionActivation(adapter));
                    })
                    .UseView(descriptor)
                    .ConfigureEndpoint(new HtmxEndpointOptions(
                        AllowedOrigin,
                        idleTimeout: TimeSpan.FromMinutes(15),
                        maximumBodyBytes: 32 * 1024,
                        maximumFields: 8,
                        maximumFieldBytes: 1024,
                        maximumResponseBytes: 256 * 1024))
                    .ConfigureTransport(new CsWebUiHtmxTransportOptions(
                        AllowedOrigin,
                        maximumRequestBytes: 32 * 1024,
                        maximumResponseBytes: 256 * 1024,
                        maximumFields: 8,
                        maximumFieldBytes: 1024))
                    .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            htmxApplication = createdApplication;
            routes.Initialize(createdApplication.OpenedView);

            TodoViewModel activeModel = model ??
                throw new InvalidOperationException("Opening the view did not activate its model.");
            var application = new TodoAppView(
                TodoRenderModel.Initial(
                    activeModel,
                    routes,
                    createdApplication.OpenedView.Revision));
            initialDocument = "<!doctype html>" + Render(
                new TodoDocumentView(new TodoDocumentModel(application)));
            assets = await TodoRuntimeAssets
                .CreateAsync(staticWebRoot, initialDocument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Attaches the sole browser-to-.NET binding when CsWebUi creates the window.</summary>
    internal void ConfigureWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref windowConfigured, 1) != 0)
        {
            throw new InvalidOperationException("SimpleTodo supports exactly one native window.");
        }

        CsWebUiHtmxApplication application = htmxApplication ??
            throw new InvalidOperationException(
                "Prepare the todo application before creating a window.");
        try
        {
            application.AttachWindow(window);
        }
        catch
        {
            Volatile.Write(ref windowConfigured, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref windowConfigured) == 0)
        {
            throw new InvalidOperationException(
                "CsWebUi must attach the native HTMX transport before the root session opens.");
        }

        if (Interlocked.Exchange(ref rootOpened, 1) != 0)
        {
            throw new InvalidOperationException("SimpleTodo supports exactly one root session.");
        }

        return ValueTask.FromResult<IRootSession>(new RootSession(this));
    }

    /// <summary>Exercises compiled rendering and the real endpoint without opening a window.</summary>
    internal async Task<int> RunSmokeTestAsync()
    {
        CsWebUiHtmxApplication selectedApplication = htmxApplication ??
            throw new InvalidOperationException("Prepare the todo application before smoke testing.");
        HtmxOpenedView selectedView = selectedApplication.OpenedView;
        TodoViewModel activeModel = model ??
            throw new InvalidOperationException("Prepare the todo application before smoke testing.");

        CaptureTransport invalid = await SendAsync(
            selectedApplication,
            Request(
                selectedView,
                routes.Add,
                selectedView.Revision,
                [new HtmxFormValue("title", "x")]));
        CaptureTransport added = await SendAsync(
            selectedApplication,
            Request(
                selectedView,
                routes.Add,
                selectedView.Revision,
                [new HtmxFormValue("title", "Run the compiled desktop sample")]));
        TodoItem? addedItem = activeModel.Items.FirstOrDefault(
            static item => item.Title == "Run the compiled desktop sample");
        long addedRevision = Revision(added);
        CaptureTransport toggled = await SendAsync(
            selectedApplication,
            Request(
                selectedView,
                routes.Toggle,
                addedRevision,
                [new HtmxFormValue("selectedId", addedItem?.Id.ToString("D") ?? string.Empty)]));
        CaptureTransport removed = await SendAsync(
            selectedApplication,
            Request(
                selectedView,
                routes.Remove,
                Revision(toggled),
                [new HtmxFormValue("selectedId", addedItem?.Id.ToString("D") ?? string.Empty)]));

        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
            .BuildFromDirectory(WebRoot, "index.html");
        var provider = new DirectoryFrontendAssetProvider(WebRoot, manifest);
        await provider.ValidateAsync(CancellationToken.None).ConfigureAwait(false);

        string[] forbiddenBindings =
        [
            string.Concat("todo", "Snapshot"),
            string.Concat("todo", "Add"),
            string.Concat("todo", "Toggle"),
            string.Concat("todo", "Remove"),
        ];
        bool generatedAssetsAreClean = Directory
            .EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Where(static path =>
                Path.GetExtension(path) is ".html" or ".js" or ".mjs")
            .Select(File.ReadAllText)
            .All(content => forbiddenBindings.All(
                binding => !content.Contains(binding, StringComparison.Ordinal)));
        string assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "WebUIToolkit.Samples.SimpleTodo.dll");
        bool applicationAssemblyIsClean = !File.Exists(assemblyPath);
        if (!applicationAssemblyIsClean)
        {
            byte[] applicationAssembly = await File.ReadAllBytesAsync(assemblyPath)
                .ConfigureAwait(false);
            applicationAssemblyIsClean = forbiddenBindings.All(binding =>
                applicationAssembly.AsSpan().IndexOf(Encoding.UTF8.GetBytes(binding)) < 0);
        }
        bool localAssetsPresent = RequiredAssetPaths.All(
            relativePath => File.Exists(Path.Combine(WebRoot, relativePath)));
        bool collectionBindingPresent = adapter?.Metadata.Any(static metadata =>
            metadata.MemberId == 6 &&
            metadata.Kind == MvvmBindingMemberKind.Collection) == true;
        string generatedRoot = WebRoot;
        await DisposeAsync().ConfigureAwait(false);
        bool generatedRootRemoved = !Directory.Exists(generatedRoot);
        bool passed =
            initialDocument?.StartsWith("<!doctype html><html", StringComparison.Ordinal) == true &&
            initialDocument.Contains("cwhtml.js", StringComparison.Ordinal) &&
            initialDocument.Contains(routes.Add, StringComparison.Ordinal) &&
            initialDocument.Contains(routes.Toggle, StringComparison.Ordinal) &&
            initialDocument.Contains(routes.Remove, StringComparison.Ordinal) &&
            invalid.StatusCode == 200 &&
            invalid.Body.Contains("between 2 and 80 characters", StringComparison.Ordinal) &&
            added.StatusCode == 200 &&
            added.Body.Contains("Run the compiled desktop sample", StringComparison.Ordinal) &&
            toggled.StatusCode == 200 &&
            toggled.Body.Contains("task completed", StringComparison.Ordinal) &&
            removed.StatusCode == 200 &&
            !removed.Body.Contains("Run the compiled desktop sample", StringComparison.Ordinal) &&
            generatedAssetsAreClean &&
            applicationAssemblyIsClean &&
            localAssetsPresent &&
            collectionBindingPresent &&
            generatedRootRemoved &&
            StringComparer.Ordinal.Equals(
                CsWebUiHtmxTransport.BindingName,
                "webuitoolkitHtmx");
        Console.WriteLine(passed
            ? "SimpleTodo compiled native HTMX smoke test passed."
            : "SimpleTodo compiled native HTMX smoke test failed.");
        if (!passed)
        {
            Console.Error.WriteLine(
                $"invalid={invalid.StatusCode}:{invalid.Body}{Environment.NewLine}" +
                $"added={added.StatusCode}:{added.Body}{Environment.NewLine}" +
                $"toggled={toggled.StatusCode}:{toggled.Body}{Environment.NewLine}" +
                $"removed={removed.StatusCode}:{removed.Body}");
        }

        return passed ? 0 : 1;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CsWebUiHtmxApplication? ownedApplication =
            Interlocked.Exchange(ref htmxApplication, null);
        TodoRuntimeAssets? ownedAssets = Interlocked.Exchange(ref assets, null);
        try
        {
            if (ownedApplication is not null)
            {
                await ownedApplication.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownedAssets is not null)
            {
                await ownedAssets.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private HtmxViewDescriptor CreateDescriptor()
    {
        TodoViewModel ActiveModel() =>
            model ?? throw new InvalidOperationException("The todo model is not active.");

        return TodoAppView
            .ConfigureHtmx(
                Contract,
                context => new TodoAppView(
                    TodoRenderModel.Response(ActiveModel(), routes, context)))
            .ConfigureStringField(
                "title",
                TodoJsonContext.Default.String,
            [
                static (value, _) =>
                {
                    string title = value.GetString()?.Trim() ?? string.Empty;
                    IReadOnlyList<string> messages = title.Length is >= 2 and <= 80
                        ? []
                        : ["Give the task a name between 2 and 80 characters."];
                    return ValueTask.FromResult(messages);
                },
            ])
            .ConfigureStringField(
                "selectedId",
                TodoJsonContext.Default.String,
            [
                (value, _) =>
                {
                    string? selectedId = value.GetString();
                    bool exists = Guid.TryParse(selectedId, out Guid id) &&
                        ActiveModel().Items.Any(item => item.Id == id);
                    IReadOnlyList<string> messages = exists
                        ? []
                        : ["That task is no longer available."];
                    return ValueTask.FromResult(messages);
                },
            ])
            .Build();
    }

    private static HtmxEndpointRequest Request(
        HtmxOpenedView view,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form) =>
        new(
            "POST",
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
            Guid.NewGuid());

    private static async Task<CaptureTransport> SendAsync(
        CsWebUiHtmxApplication application,
        HtmxEndpointRequest request)
    {
        var capture = new CaptureTransport();
        await application.HandleAsync(request, capture).ConfigureAwait(false);
        return capture;
    }

    private static long Revision(CaptureTransport response) =>
        long.Parse(
            response.Headers["X-WebUI-Revision"],
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static string Render(TodoDocumentView view)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new Utf8HtmlWriter(output);
        view.Render(ref writer, new TemplateContext(CultureInfo.InvariantCulture));
        writer.Complete();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private sealed class RootSession(TodoApplicationRoot owner) : IRootSession
    {
        private TodoApplicationRoot? owner = owner;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            TodoApplicationRoot? selected = Interlocked.Exchange(ref owner, null);
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

internal sealed class TodoRuntimeAssets : IAsyncDisposable
{
    private int disposed;

    private TodoRuntimeAssets(string root)
    {
        Root = root;
    }

    internal string Root { get; }

    internal static async ValueTask<TodoRuntimeAssets> CreateAsync(
        string staticRoot,
        string initialDocument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staticRoot);
        ArgumentNullException.ThrowIfNull(initialDocument);
        if (!Directory.Exists(staticRoot))
        {
            throw new DirectoryNotFoundException(
                $"The sample UI was not copied to '{staticRoot}'.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-simple-todo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = new TodoRuntimeAssets(root);
        try
        {
            CopyDirectory(staticRoot, root, cancellationToken);
            await File.WriteAllTextAsync(
                    Path.Combine(root, "index.html"),
                    initialDocument,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
            string relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (string file in Directory.EnumerateFiles(
            sourceRoot,
            "*",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, file);
            if (StringComparer.OrdinalIgnoreCase.Equals(relative, "index.html") ||
                StringComparer.OrdinalIgnoreCase.Equals(relative, "app.js"))
            {
                continue;
            }

            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }
}
