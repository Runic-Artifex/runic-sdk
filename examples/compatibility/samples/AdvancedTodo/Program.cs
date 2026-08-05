using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.CsWebUi;
using RunicMarkup.RunicToolkit.Htmx;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;
using RunicToolkit.Samples.AdvancedTodo.Application;
using RunicToolkit.Samples.AdvancedTodo.Infrastructure;
using RunicToolkit.Samples.AdvancedTodo.UI;

string staticWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(staticWebRoot))
{
    throw new DirectoryNotFoundException(
        $"The sample's local assets were not copied to '{staticWebRoot}'.");
}

bool selfTest = args.Contains("--self-test", StringComparer.Ordinal);
bool browserSmokeTest = args.Contains("--browser-smoke-test", StringComparer.Ordinal);
bool csharpMarkupSelfTest = args.Contains(
    "--csharp-markup-self-test",
    StringComparer.Ordinal);
bool csharpMarkupBrowserSmokeTest = args.Contains(
    "--csharp-markup-browser-smoke-test",
    StringComparer.Ordinal);
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
if (selfTest || browserSmokeTest || csharpMarkupSelfTest || csharpMarkupBrowserSmokeTest)
{
    testDirectory = Path.Combine(
        Path.GetTempPath(),
        "runic-toolkit-advanced-todo-test-" + Guid.NewGuid().ToString("N"));
    dataPath = Path.Combine(testDirectory, "todos.json");
}
else
{
    dataPath = Environment.GetEnvironmentVariable("ADVANCED_TODO_DATA")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RunicToolkit",
            "AdvancedTodo",
            "todos.json");
}

var service = new TodoService(new JsonTodoRepository(dataPath));
if (csharpMarkupSelfTest || csharpMarkupBrowserSmokeTest)
{
    await using AdvancedTodoCsharpMarkupApplication csharpMarkupRoot =
        await AdvancedTodoCsharpMarkupApplicationRoot.CreateAsync(
            service,
            staticWebRoot,
            frontend);
    try
    {
        return csharpMarkupBrowserSmokeTest
            ? await AdvancedTodoBrowserSmoke.RunAsync(csharpMarkupRoot)
            : await AdvancedTodoSmoke.RunAsync(csharpMarkupRoot, service);
    }
    finally
    {
        if (testDirectory is not null && Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}

await using AdvancedTodoApplication root =
    await AdvancedTodoApplicationRoot.CreateAsync(
        service,
        staticWebRoot,
        frontend);
try
{
    if (selfTest)
    {
        return await AdvancedTodoSmoke.RunAsync(root, service);
    }

    if (browserSmokeTest)
    {
        return await AdvancedTodoBrowserSmoke.RunAsync(root);
    }

    frontend.UseNativeWindow(
        root,
        new BrowserHostOptions("advanced-todo"),
        new BrowserWindowOptions(
            "main",
            "Advanced ToDo · Runic Toolkit",
            width: 1180,
            height: 820));

    return await builder.RunAsync();
}
finally
{
    if (testDirectory is not null && Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}
