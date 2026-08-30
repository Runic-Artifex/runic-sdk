using System;
using System.Reflection;
using System.Threading;
using Runic.Application;
using Runic.Application.Desktop;
using Runic.Desktop;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Examples.Setup;
using Runic.Examples.Setup.Contract;

[assembly: RunicApplicationManifest("runic-toolkit-sveltekit-setup", Version = "1.0.0", Provenance = "example")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: RunicApplicationArtifact("bridge-contract", SetupBridgeContract.ProtocolArtifactIdentity, SetupBridgeContract.Fingerprint)]
[assembly: RunicApplicationBridgeComposition(typeof(SetupBridgeHandler), typeof(SetupBridgeDispatcher))]

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await SetupSmokeTest.RunAsync();

bool nativeE2E = Array.Exists(args, static argument => argument == "--native-e2e");
AssetArchiveSource assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
DesktopApplicationHostOptions desktopOptions = new()
{
    Title = "Runic Toolkit · SvelteKit Setup",
    Surface = new DesktopSurfaceOptions
    {
        ContentHandler = assets.ToDesktopContentHandler(new DesktopAssetOptions
        {
            EnableSinglePageApplicationFallback = true,
        }),
    },
    Window = new DesktopWindowOptions
    {
        Browser = nativeE2E ? BrowserKind.Embedded : BrowserKind.Any,
        Width = 980,
        Height = 720,
        MinimumWidth = 760,
        MinimumHeight = 560,
        Centered = true,
        Hidden = nativeE2E,
    },
};

if (nativeE2E)
{
    await using DesktopApplicationHost desktop = new(desktopOptions);
    await using ApplicationHost nativeApplication = RunicApplication.CreateBuilder(args)
        .UseHost(desktop)
        .Build();
    using var roundtripTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await desktop.StartAsync(nativeApplication.Manifest, nativeApplication.Arguments, roundtripTimeout.Token);
    if (desktop.Surface is null || desktop.Window is null)
        throw new InvalidOperationException("The native Setup roundtrip did not open its authenticated Desktop presentation.");
    await desktop.StopAsync(CancellationToken.None);
    return 0;
}

await using ApplicationHost application = RunicApplication.CreateBuilder(args)
    .UseDesktop(desktopOptions)
    .Build();
await application.RunAsync();
return 0;
