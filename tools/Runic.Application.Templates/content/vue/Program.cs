using System;
using System.Threading.Tasks;
using Runic.Application;
using System.Reflection;
using Runic.Application.Desktop;
using Runic.Desktop;
using Runic.Assets;
using Runic.Assets.Desktop;
using RunicDesktopApp;

[assembly: RunicApplicationManifest("RunicDesktopApp", Version = "1.0.0", Provenance = "template")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: RunicApplicationArtifact("bridge-contract", "runic.artifex.counter/1", "4e873f5967e86eeded5e26d8faf27c305464f1272b90935cc8a1b09365471508")]
[assembly: RunicApplicationBridgeComposition(typeof(CounterBridgeHandler), typeof(Runic.Application.Template.Contract.CounterBridgeDispatcher))]

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await CounterSmokeTest.RunAsync();

AssetArchiveSource assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
await using ApplicationHost application = RunicApplication.CreateBuilder(args)
    .UseDesktop(new DesktopApplicationHostOptions
    {
        Title = "Runic Application Counter · Vue",
        Surface = new DesktopSurfaceOptions { ContentHandler = assets.ToDesktopContentHandler() },
    })
    .Build();
await application.RunAsync();
return 0;
