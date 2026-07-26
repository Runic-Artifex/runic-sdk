using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.Samples.SimpleTodo;

string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(webRoot))
{
    throw new DirectoryNotFoundException($"The sample UI was not copied to '{webRoot}'.");
}

await using var backend = new TodoBackend();
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await backend.RunSmokeTestAsync();
}

FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
var endpoint = new FrontendAssetEndpoint(assets, new Uri("app://simple-todo/"));
var stop = new ApplicationStopControllerBinding();
var host = new GenericHostWebUIToolkitApplicationBuilder(args);
var browserFactory = new CsWebUiBrowserHostFactory(
    new CsWebUiAdapterOptions(
        webRoot,
        configureWindow: window =>
        {
            window.BindAsync("todoSnapshot", async (_, cancellationToken) =>
                WebUiResult.FromString(await backend.SnapshotAsync(cancellationToken)));
            window.BindAsync("todoAdd", async (webUiEvent, cancellationToken) =>
                WebUiResult.FromString(
                    await backend.AddAsync(webUiEvent.GetString(), cancellationToken)));
            window.BindAsync("todoToggle", async (webUiEvent, cancellationToken) =>
                WebUiResult.FromString(
                    await backend.ToggleAsync(webUiEvent.GetString(), cancellationToken)));
            window.BindAsync("todoRemove", async (webUiEvent, cancellationToken) =>
                WebUiResult.FromString(
                    await backend.RemoveAsync(webUiEvent.GetString(), cancellationToken)));
        }));

host.Application.AddValidator(LaunchKind.UserInterface, new FrontendAssetValidator(assets));
host.Application.AddModeRunner(new WebUiModeRunner(
    browserFactory,
    new TodoRootSessionFactory(backend),
    endpoint,
    stop,
    new WebUiModeOptions(
        new BrowserHostOptions("simple-todo"),
        new BrowserWindowOptions("main", "Simple Todo", 760, 720),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5))));

await using WebUIToolkitApplication application = host.Build();
stop.Bind(application.StopController);
ApplicationRunResult result = await application.RunAsync();
return result.ExitCode ?? 1;
