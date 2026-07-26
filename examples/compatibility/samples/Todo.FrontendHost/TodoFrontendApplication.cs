using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.CsWebUi.Mvvm;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.Samples.AdvancedTodo;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Infrastructure;
using AdvancedTodoViewModel = WebUIToolkit.Samples.AdvancedTodo.UI.TodoViewModel;
using SimpleTodoViewModel = WebUIToolkit.Samples.SimpleTodo.TodoViewModel;
using SimpleTodoJsonContext = WebUIToolkit.Samples.SimpleTodo.TodoJsonContext;

namespace WebUIToolkit.Samples.Todo.FrontendHost;

/// <summary>Runs either shared Todo ViewModel through one selected browser framework.</summary>
public static class TodoFrontendApplication
{
    /// <summary>Starts the native CsWebUi application for a frontend sample.</summary>
    public static async Task<int> RunAsync(string frontend, string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frontend);
        ArgumentNullException.ThrowIfNull(args);
        TodoDemo demo = args.Contains("--advanced", StringComparer.Ordinal)
            ? TodoDemo.Advanced
            : TodoDemo.Simple;
        string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
        if (!Directory.Exists(webRoot))
        {
            throw new DirectoryNotFoundException(
                $"Build the {frontend} frontend before running the sample. Missing '{webRoot}'.");
        }

        await using var root = new TodoFrontendRoot(frontend, demo);
        string entryPoint = $"{demo.ToString().ToLowerInvariant()}/index.html";
        if (args.Contains("--browser-smoke-test", StringComparer.Ordinal))
        {
            return await TodoFrontendBrowserSmoke
                .RunAsync(root, webRoot, entryPoint, frontend, demo)
                .ConfigureAwait(false);
        }

        if (args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            return await root.RunSmokeTestAsync().ConfigureAwait(false);
        }

        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
            .BuildFromDirectory(webRoot, entryPoint);
        var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
        var stop = new ApplicationStopControllerBinding();
        var builder = new GenericHostWebUIToolkitApplicationBuilder(args);
        string id = $"todo-{frontend.ToLowerInvariant()}-{demo.ToString().ToLowerInvariant()}";

        builder.Application.AddValidator(LaunchKind.UserInterface, new FrontendAssetValidator(assets));
        builder.Application.AddModeRunner(new WebUiModeRunner(
            new CsWebUiBrowserHostFactory(new CsWebUiAdapterOptions(
                webRoot,
                configureWindow: root.ConfigureWindow)),
            root,
            new FrontendAssetEndpoint(assets, new Uri($"app://{id}/")),
            stop,
            new WebUiModeOptions(
                new BrowserHostOptions(id),
                new BrowserWindowOptions(
                    "main",
                    $"{demo} ToDo · {frontend}",
                    demo == TodoDemo.Advanced ? 1180 : 760,
                    demo == TodoDemo.Advanced ? 820 : 720),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5))));

        await using WebUIToolkitApplication application = builder.Build();
        stop.Bind(application.StopController);
        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        return result.ExitCode ?? 1;
    }
}

internal enum TodoDemo
{
    Simple,
    Advanced,
}

internal sealed class TodoFrontendRoot : IRootSessionFactory, IAsyncDisposable
{
    private static readonly MvvmContract SimpleContract = new(TodoContracts.SimpleTodo.Name);
    private static readonly MvvmContract AdvancedContract = new(TodoContracts.AdvancedTodo.Name);
    private readonly TodoDemo _demo;
    private readonly IMvvmSessionFactory _sessions;
    private WebUiWindow? _window;

    internal TodoFrontendRoot(string frontend, TodoDemo demo)
    {
        _demo = demo;
        var registry = new MvvmSessionRegistry();
        if (demo == TodoDemo.Simple)
        {
            registry.Map(SimpleContract, static _ =>
            {
                var model = new SimpleTodoViewModel();
                CommunityToolkitMvvmBindingAdapter<SimpleTodoViewModel> adapter =
                    new CommunityToolkitMvvmAdapterBuilder<SimpleTodoViewModel>(model)
                        .BindProperty(
                            TodoContracts.SimpleTodo.Members.NewTitle,
                            nameof(SimpleTodoViewModel.NewTitle),
                            static state => state.NewTitle,
                            static (state, value) => state.NewTitle = value,
                            SimpleTodoJsonContext.Default.String)
                        .BindCollection(
                            TodoContracts.SimpleTodo.Members.Items,
                            nameof(SimpleTodoViewModel.Items),
                            static state => state.Items,
                            SimpleTodoJsonContext.Default.TodoItem)
                        .BindCommand(
                            TodoContracts.SimpleTodo.Members.Add,
                            nameof(SimpleTodoViewModel.AddCommand),
                            static state => state.AddCommand)
                        .BindCommand(
                            TodoContracts.SimpleTodo.Members.Toggle,
                            nameof(SimpleTodoViewModel.ToggleByIdCommand),
                            static state => state.ToggleByIdCommand,
                            SimpleTodoJsonContext.Default.String)
                        .BindCommand(
                            TodoContracts.SimpleTodo.Members.Remove,
                            nameof(SimpleTodoViewModel.RemoveByIdCommand),
                            static state => state.RemoveByIdCommand,
                            SimpleTodoJsonContext.Default.String)
                        .Build();
                return ValueTask.FromResult(new MvvmSessionActivation(adapter));
            });
        }
        else
        {
            registry.Map(AdvancedContract, async cancellationToken =>
            {
                string dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebUIToolkit",
                    "Samples");
                var repository = new JsonTodoRepository(Path.Combine(
                    dataDirectory,
                    $"advanced-todo-{frontend.ToLowerInvariant()}.json"));
                var model = new AdvancedTodoViewModel(new TodoService(repository));
                try
                {
                    await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    CommunityToolkitMvvmBindingAdapter<AdvancedTodoViewModel> adapter =
                        BuildAdvancedAdapter(model);
                    return new MvvmSessionActivation(adapter, model);
                }
                catch
                {
                    await model.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            });
        }

        _sessions = registry.Build();
    }

    internal void ConfigureWindow(WebUiWindow nativeWindow)
    {
        ArgumentNullException.ThrowIfNull(nativeWindow);
        if (Interlocked.CompareExchange(ref _window, nativeWindow, null) is not null)
        {
            throw new InvalidOperationException("A Todo frontend sample supports one native window.");
        }
    }

    public async ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        WebUiWindow selectedWindow = _window ??
            throw new InvalidOperationException("CsWebUi must create the window before the root session opens.");
        IMvvmSession session = await _sessions
            .OpenAsync(Contract, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new RootSession(CsWebUiMvvmBridge.Attach(selectedWindow, session));
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        await using IMvvmSession session = await _sessions.OpenAsync(Contract).ConfigureAwait(false);
        MvvmResponse initial = await session.DispatchAsync(new MvvmSnapshotRequest(NewRequest()))
            .ConfigureAwait(false);
        using JsonDocument argument = JsonDocument.Parse("\"Framework sample task\"");
        MvvmResponse changed = await session.DispatchAsync(new MvvmMutationRequest(
            NewRequest(),
            MvvmMutationKind.SetProperty,
            session.Revision,
            _demo == TodoDemo.Simple ? TodoContracts.SimpleTodo.Members.NewTitle : TodoContracts.AdvancedTodo.Members.NewTitle,
            argument.RootElement)).ConfigureAwait(false);
        bool passed = initial.Succeeded && changed.Succeeded && changed.Revision == 1;
        Console.WriteLine(passed
            ? $"{_demo} Todo shared ViewModel smoke test passed."
            : $"{_demo} Todo shared ViewModel smoke test failed.");
        return passed ? 0 : 1;
    }

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();

    private MvvmContract Contract =>
        _demo == TodoDemo.Simple ? SimpleContract : AdvancedContract;

    private static MvvmRequestId NewRequest() => new(Guid.NewGuid());

    private static CommunityToolkitMvvmBindingAdapter<AdvancedTodoViewModel> BuildAdvancedAdapter(
        AdvancedTodoViewModel model) =>
        new CommunityToolkitMvvmAdapterBuilder<AdvancedTodoViewModel>(model)
            .BindProperty(
                TodoContracts.AdvancedTodo.Members.NewTitle,
                nameof(AdvancedTodoViewModel.NewTitle),
                static state => state.NewTitle,
                static (state, value) => state.NewTitle = value,
                AdvancedTodoJsonContext.Default.String,
                includeValidation: true)
            .BindProperty(
                TodoContracts.AdvancedTodo.Members.NewNotes,
                nameof(AdvancedTodoViewModel.NewNotes),
                static state => state.NewNotes,
                static (state, value) => state.NewNotes = value,
                AdvancedTodoJsonContext.Default.String)
            .BindProperty(
                TodoContracts.AdvancedTodo.Members.NewPriority,
                nameof(AdvancedTodoViewModel.NewPriority),
                static state => state.NewPriority,
                static (state, value) => state.NewPriority = value,
                AdvancedTodoJsonContext.Default.String)
            .BindProperty(
                TodoContracts.AdvancedTodo.Members.Query,
                nameof(AdvancedTodoViewModel.Query),
                static state => state.Query,
                static (state, value) => state.Query = value,
                AdvancedTodoJsonContext.Default.String)
            .BindProperty(
                TodoContracts.AdvancedTodo.Members.Filter,
                nameof(AdvancedTodoViewModel.Filter),
                static state => state.Filter,
                static (state, value) => state.Filter = value,
                AdvancedTodoJsonContext.Default.String)
            .BindCollection(
                TodoContracts.AdvancedTodo.Members.Items,
                nameof(AdvancedTodoViewModel.VisibleItems),
                static state => state.VisibleItems,
                AdvancedTodoJsonContext.Default.TodoItem)
            .BindCollection(
                TodoContracts.AdvancedTodo.Members.Diagnostics,
                nameof(AdvancedTodoViewModel.Diagnostics),
                static state => state.Diagnostics,
                AdvancedTodoJsonContext.Default.DiagnosticEntry)
            .BindReadOnlyProperty(
                TodoContracts.AdvancedTodo.Members.State,
                nameof(AdvancedTodoViewModel.State),
                static state => state.State,
                AdvancedTodoJsonContext.Default.AdvancedTodoState)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.Add, nameof(AdvancedTodoViewModel.AddCommand), static state => state.AddCommand)
            .BindCommand(TodoContracts.AdvancedTodo.Members.ApplyFilter, nameof(AdvancedTodoViewModel.ApplyFilterCommand), static state => state.ApplyFilterCommand)
            .BindAsyncCommand(
                TodoContracts.AdvancedTodo.Members.Toggle,
                nameof(AdvancedTodoViewModel.ToggleByIdCommand),
                static state => state.ToggleByIdCommand,
                AdvancedTodoJsonContext.Default.String)
            .BindAsyncCommand(
                TodoContracts.AdvancedTodo.Members.Delete,
                nameof(AdvancedTodoViewModel.DeleteByIdCommand),
                static state => state.DeleteByIdCommand,
                AdvancedTodoJsonContext.Default.String)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.ClearCompleted, nameof(AdvancedTodoViewModel.ClearCompletedCommand), static state => state.ClearCompletedCommand)
            .BindCommand(TodoContracts.AdvancedTodo.Members.Import, nameof(AdvancedTodoViewModel.ImportCommand), static state => state.ImportCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.CancelImport, nameof(AdvancedTodoViewModel.CancelImportCommand), static state => state.CancelImportCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.WizardStart, nameof(AdvancedTodoViewModel.StartWizardCommand), static state => state.StartWizardCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.WizardNext, nameof(AdvancedTodoViewModel.WizardNextCommand), static state => state.WizardNextCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.WizardBack, nameof(AdvancedTodoViewModel.WizardBackCommand), static state => state.WizardBackCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.WizardFinish, nameof(AdvancedTodoViewModel.WizardFinishCommand), static state => state.WizardFinishCommand)
            .BindAsyncCommand(TodoContracts.AdvancedTodo.Members.WizardCancel, nameof(AdvancedTodoViewModel.WizardCancelCommand), static state => state.WizardCancelCommand)
            .Build();

    private sealed class RootSession(CsWebUiMvvmBridge bridge) : IRootSession
    {
        private CsWebUiMvvmBridge? _bridge = bridge;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            CsWebUiMvvmBridge? owned = Interlocked.Exchange(ref _bridge, null);
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
