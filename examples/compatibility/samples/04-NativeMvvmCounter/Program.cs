using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RunicAssets;
using RunicAssets.RunicToolkit;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.Samples.NativeMvvmCounter;

string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(webRoot))
{
    throw new DirectoryNotFoundException($"The sample UI was not copied to '{webRoot}'.");
}

await using var root = new NativeCounterRoot();
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await root.RunSmokeTestAsync();
}

var source = new DevelopmentDirectoryAssetSource(webRoot, "index.html");
var assets = new RunicToolkitAssetBoundary(
    source,
    new Uri("app://native-counter/application/"));
var stop = new ApplicationStopControllerBinding();
var builder = new GenericHostRunicToolkitApplicationBuilder(args);

builder.Application.AddValidator(LaunchKind.UserInterface, new FrontendAssetValidator(assets));
builder.Application.AddModeRunner(new WebUiModeRunner(
    new CsWebUiBrowserHostFactory(new CsWebUiAdapterOptions(
        webRoot,
        configureWindow: root.ConfigureWindow)),
    root,
    new FrontendAssetEndpoint(assets, new Uri("app://native-mvvm-counter/")),
    stop,
    new WebUiModeOptions(
        new BrowserHostOptions("native-mvvm-counter"),
        new BrowserWindowOptions("main", "Native MVVM Counter", 640, 560),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5))));

await using RunicToolkitApplication application = builder.Build();
stop.Bind(application.StopController);
ApplicationRunResult result = await application.RunAsync();
return result.ExitCode ?? 1;
