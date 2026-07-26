using System;
using System.IO;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Infrastructure;
using WebUIToolkit.Samples.AdvancedTodo.UI;

if (args is ["--self-test"])
{
    return await SampleSmoke.RunAsync();
}

string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);

string dataPath = Environment.GetEnvironmentVariable("ADVANCED_TODO_DATA")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WebUIToolkit",
        "AdvancedTodo",
        "todos.json");
var controller = new NativeTodoController(
    new TodoService(new JsonTodoRepository(dataPath)));
var stop = new ApplicationStopControllerBinding();
var builder = new GenericHostWebUIToolkitApplicationBuilder();

builder.Application.AddValidator(
    LaunchKind.UserInterface,
    new FrontendAssetValidator(assets));
builder.Application.AddModeRunner(
    new WebUiModeRunner(
        new CsWebUiBrowserHostFactory(
            new CsWebUiAdapterOptions(
                webRoot,
                configureWindow: controller.ConfigureWindow)),
        controller,
        new FrontendAssetEndpoint(assets, new Uri("app://advanced-todo/")),
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
