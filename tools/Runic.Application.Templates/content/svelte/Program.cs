using System;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Bridge;
using System.Reflection;
using Runic.Assets;
using RunicDesktopApp;

[assembly: RunicApplicationManifest("RunicDesktopApp", Version = "1.0.0", Provenance = "template")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: ApplicationBridgeContract("runic.artifex.counter", 1, ContractName = "Counter")]

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await CounterSmokeTest.RunAsync();

var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly()).WithDevelopmentDocument();
await using ApplicationHost application = RunicApplication.CreateBuilder(args)
    .UseHost(HostComposition.Create(assets))
    .Build();
await application.RunAsync();
return 0;
