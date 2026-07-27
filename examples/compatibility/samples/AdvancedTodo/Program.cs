using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Infrastructure;
using WebUIToolkit.Samples.AdvancedTodo.UI;

string staticWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(staticWebRoot))
{
    throw new DirectoryNotFoundException(
        $"The sample's local assets were not copied to '{staticWebRoot}'.");
}

bool selfTest = args.Contains("--self-test", StringComparer.Ordinal);
bool browserSmokeTest = args.Contains("--browser-smoke-test", StringComparer.Ordinal);
string? testDirectory = null;
string dataPath;
if (selfTest || browserSmokeTest)
{
    testDirectory = Path.Combine(
        Path.GetTempPath(),
        "webuitoolkit-advanced-todo-test-" + Guid.NewGuid().ToString("N"));
    dataPath = Path.Combine(testDirectory, "todos.json");
}
else
{
    dataPath = Environment.GetEnvironmentVariable("ADVANCED_TODO_DATA")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebUIToolkit",
            "AdvancedTodo",
            "todos.json");
}

await using var root = new AdvancedTodoApplicationRoot(
    new TodoService(new JsonTodoRepository(dataPath)));
try
{
    await root.PrepareAsync(staticWebRoot);
    if (selfTest)
    {
        return await root.RunSelfTestAsync();
    }

    if (browserSmokeTest)
    {
        return await AdvancedTodoBrowserSmoke.RunAsync(root);
    }

    FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
        .BuildFromDirectory(root.WebRoot, "index.html");
    var assets = new DirectoryFrontendAssetProvider(root.WebRoot, manifest);
    var endpoint = new FrontendAssetEndpoint(assets, new Uri("app://advanced-todo/"));
    var stop = new ApplicationStopControllerBinding();
    var builder = new GenericHostWebUIToolkitApplicationBuilder(args);
    var browserFactory = new CsWebUiBrowserHostFactory(
        new CsWebUiAdapterOptions(
            root.WebRoot,
            configureWindow: root.ConfigureWindow));

    builder.Application.AddValidator(
        LaunchKind.UserInterface,
        new FrontendAssetValidator(assets));
    builder.Application.AddModeRunner(
        new WebUiModeRunner(
            browserFactory,
            root,
            endpoint,
            stop,
            new WebUiModeOptions(
                new BrowserHostOptions("advanced-todo"),
                new BrowserWindowOptions(
                    "main",
                    "Advanced ToDo · WebUIToolkit",
                    width: 1180,
                    height: 820),
                sessionCloseTimeout: TimeSpan.FromSeconds(5),
                windowCloseTimeout: TimeSpan.FromSeconds(5))));

    await using WebUIToolkitApplication application = builder.Build();
    stop.Bind(application.StopController);
    ApplicationRunResult result = await application.RunAsync();
    return result.ExitCode ?? 1;
}
finally
{
    if (testDirectory is not null && Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}
