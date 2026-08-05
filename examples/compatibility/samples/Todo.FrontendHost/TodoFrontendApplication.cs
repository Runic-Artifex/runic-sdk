using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.CsWebUi.Mvvm;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.MVVM;
using RunicToolkit.Samples.AdvancedTodo;
using RunicToolkit.Samples.AdvancedTodo.Application;
using RunicToolkit.Samples.AdvancedTodo.Infrastructure;
using AdvancedTodoViewModel = RunicToolkit.Samples.AdvancedTodo.UI.TodoViewModel;
using SimpleTodoViewModel = RunicToolkit.Samples.SimpleTodo.TodoViewModel;

namespace RunicToolkit.Samples.Todo.FrontendHost;

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

        string entryPoint = $"{demo.ToString().ToLowerInvariant()}/index.html";
        if (args.Contains("--browser-smoke-test", StringComparer.Ordinal))
        {
            await using var root = new TodoFrontendRoot(frontend, demo);
            return await TodoFrontendBrowserSmoke
                .RunAsync(
                    root,
                    webRoot,
                    entryPoint,
                    frontend,
                    demo,
                    args.Contains("--hmr-smoke-test", StringComparer.Ordinal))
                .ConfigureAwait(false);
        }

        if (args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            await using var root = new TodoFrontendRoot(frontend, demo);
            return await root.RunSmokeTestAsync().ConfigureAwait(false);
        }

        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
            .BuildFromDirectory(webRoot, entryPoint);
        var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
        string id = $"todo-{frontend.ToLowerInvariant()}-{demo.ToString().ToLowerInvariant()}";
        var builder = WebUiApp.CreateBuilder(args);
        var options = new MvvmFrontendApplicationOptions<object>(
            assets,
            new CsWebUiAdapterOptions(webRoot),
            new BrowserHostOptions(id),
            new BrowserWindowOptions(
                "main",
                $"{demo} ToDo · {frontend}",
                demo == TodoDemo.Advanced ? 1180 : 760,
                demo == TodoDemo.Advanced ? 820 : 720),
            demo == TodoDemo.Simple
                ? new MvvmContract(TodoContracts.SimpleTodo.Name)
                : new MvvmContract(TodoContracts.AdvancedTodo.Name),
            cancellationToken => ActivateModelAsync(frontend, demo, cancellationToken),
            CreateAdapter);
        await using MvvmFrontendApplication application = frontend switch
        {
            "React" => builder.React.CreateApplication(options),
            "Vue" => builder.Vue.CreateApplication(options),
            "Svelte" => builder.Svelte.CreateApplication(options),
            "Angular" => builder.Angular.CreateApplication(options),
            _ => throw new ArgumentException(
                $"Unsupported Todo frontend '{frontend}'.",
                nameof(frontend)),
        };

        return await builder.RunAsync().ConfigureAwait(false);
    }

    private static ValueTask<object> ActivateModelAsync(
        string frontend,
        TodoDemo demo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (demo == TodoDemo.Simple)
        {
            return ValueTask.FromResult<object>(new SimpleTodoViewModel());
        }

        return ActivateAdvancedModelAsync(frontend, cancellationToken);
    }

    private static async ValueTask<object> ActivateAdvancedModelAsync(
        string frontend,
        CancellationToken cancellationToken)
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RunicToolkit",
            "Samples");
        var repository = new JsonTodoRepository(Path.Combine(
            dataDirectory,
            $"advanced-todo-{frontend.ToLowerInvariant()}.json"));
        var model = new AdvancedTodoViewModel(new TodoService(repository));
        try
        {
            await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return model;
        }
        catch
        {
            await model.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IMvvmBindingAdapter CreateAdapter(object model) =>
        model switch
        {
            SimpleTodoViewModel simple => TodoContracts.SimpleTodo.CreateAdapter(simple),
            AdvancedTodoViewModel advanced => TodoContracts.AdvancedTodo.CreateAdapter(advanced),
            _ => throw new ArgumentException(
                $"Unsupported Todo ViewModel type '{model.GetType().FullName}'.",
                nameof(model)),
        };
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
                return ValueTask.FromResult(
                    new MvvmSessionActivation(TodoContracts.SimpleTodo.CreateAdapter(model)));
            });
        }
        else
        {
            registry.Map(AdvancedContract, async cancellationToken =>
            {
                string dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RunicToolkit",
                    "Samples");
                var repository = new JsonTodoRepository(Path.Combine(
                    dataDirectory,
                    $"advanced-todo-{frontend.ToLowerInvariant()}.json"));
                var model = new AdvancedTodoViewModel(new TodoService(repository));
                try
                {
                    await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    return new MvvmSessionActivation(
                        TodoContracts.AdvancedTodo.CreateAdapter(model),
                        model);
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
