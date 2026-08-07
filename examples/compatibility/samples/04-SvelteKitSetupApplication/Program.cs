using System;
using System.IO;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Examples.Setup;
using RunicToolkit.Examples.Setup.Contract;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;
using RunicToolkit.Hosting.WebUi;

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await SetupSmokeTest.RunAsync();
if (Array.Exists(args, static argument => argument == "--native-e2e"))
    return await SvelteKitNativeE2E.RunAsync();

string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
var builder = WebUiApp.CreateBuilder(args);
var options = new ApplicationBridgeFrontendApplicationOptions(
    assets,
    new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("runic-toolkit-sveltekit-setup"),
    new BrowserWindowOptions("main", "Runic Toolkit · SvelteKit Setup", 980, 720),
    static () => new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupBridgeHandler())));
await using ApplicationBridgeFrontendApplication frontend =
    builder.UseApplicationBridge("SvelteKitSetup", options);
return await builder.RunAsync();
