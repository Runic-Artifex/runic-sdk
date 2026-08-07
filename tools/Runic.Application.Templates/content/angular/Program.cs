using System;
using System.IO;
using System.Threading.Tasks;
using RunicToolkitStarter;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkitStarter.Contract;

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await CounterSmokeTest.RunAsync();

string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
var builder = WebUiApp.CreateBuilder(args);
var options = new ApplicationBridgeFrontendApplicationOptions(
    assets,
    new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("runic-toolkitstarter-angular"),
    new BrowserWindowOptions("main", "RunicToolkit Counter · Angular", 760, 680),
    static () => new ApplicationBridgeSession(
        new CounterBridgeDispatcher(new CounterBridgeHandler())));
await using ApplicationBridgeFrontendApplication frontend =
    builder.UseApplicationBridge("Angular", options);
return await builder.RunAsync();
