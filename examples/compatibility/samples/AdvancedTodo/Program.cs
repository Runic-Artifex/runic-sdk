using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
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
var builder = WebUiApp.CreateBuilder(args);
CwhtmlHtmxAppBuilder frontend = builder.CwhtmlHtmx
    .ConfigureEndpoint(new HtmxEndpointOptions(
        AdvancedTodoApplicationRoot.AllowedOrigin,
        idleTimeout: TimeSpan.FromMinutes(15),
        maximumBodyBytes: 16 * 1024,
        maximumFields: 8,
        maximumFieldBytes: 4 * 1024,
        maximumResponseBytes: 512 * 1024))
    .ConfigureTransport(new CsWebUiHtmxTransportOptions(
        AdvancedTodoApplicationRoot.AllowedOrigin,
        maximumRequestBytes: 16 * 1024,
        maximumResponseBytes: 512 * 1024,
        maximumFields: 8,
        maximumFieldBytes: 4 * 1024));
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
    await root.PrepareAsync(staticWebRoot, frontend);
    if (selfTest)
    {
        return await AdvancedTodoSmoke.RunAsync(root);
    }

    if (browserSmokeTest)
    {
        return await AdvancedTodoBrowserSmoke.RunAsync(root);
    }

    frontend.UseNativeWindow(
        root.PreparedAssets,
        root,
        new BrowserHostOptions("advanced-todo"),
        new BrowserWindowOptions(
            "main",
            "Advanced ToDo · WebUIToolkit",
            width: 1180,
            height: 820),
        root.ConfigureWindow);

    return await builder.RunAsync();
}
finally
{
    if (testDirectory is not null && Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}
