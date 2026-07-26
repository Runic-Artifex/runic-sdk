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

string staticWebRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(staticWebRoot))
{
    throw new DirectoryNotFoundException(
        $"The sample's local assets were not copied to '{staticWebRoot}'.");
}

await using var root = new TodoApplicationRoot();
await root.PrepareAsync(staticWebRoot);
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await root.RunSmokeTestAsync();
}

if (args.Contains("--browser-smoke-test", StringComparer.Ordinal))
{
    return await TodoBrowserSmoke.RunAsync(root);
}

FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(root.WebRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(root.WebRoot, manifest);
var endpoint = new FrontendAssetEndpoint(assets, new Uri("app://simple-todo/"));
var stop = new ApplicationStopControllerBinding();
var host = new GenericHostWebUIToolkitApplicationBuilder(args);
var browserFactory = new CsWebUiBrowserHostFactory(
    new CsWebUiAdapterOptions(
        root.WebRoot,
        configureWindow: root.ConfigureWindow));

host.Application.AddValidator(LaunchKind.UserInterface, new FrontendAssetValidator(assets));
host.Application.AddModeRunner(new WebUiModeRunner(
    browserFactory,
    root,
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
