using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("package.consumer", Version = "1.0.0", Provenance = "package")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationCapability("headless")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1", "feed")]

var normalHost = new DeterministicApplicationTestHost(capabilities: [
    ApplicationCapabilityStatus.Unavailable("desktop", "headless-package-consumer"),
    ApplicationCapabilityStatus.Available("headless"),
]);
await using ApplicationHost application = RunicApplication.CreateBuilder([]).UseHost(normalHost).Build();
await application.RunAsync();
if (!normalHost.Lifecycle.SequenceEqual(["start", "wait", "stop"]) ||
    application.Capabilities.GetRequired("headless").Availability != ApplicationCapabilityAvailability.Available ||
    application.Capabilities.GetRequired("desktop").UnavailableReason != "headless-package-consumer") return 2;
var faultHost = new DeterministicApplicationTestHost { WaitFailure = new InvalidOperationException("primary"), StopFailure = new InvalidOperationException("cleanup") };
await using (var faultApplication = new ApplicationHost(application.Manifest, [], faultHost))
{
    try
    {
        await faultApplication.RunAsync();
        return 3;
    }
    catch (InvalidOperationException exception) when (exception.Message == "primary")
    {
        if (!faultHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 4;
    }
}
var cancelledHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
await using (var cancelledApplication = new ApplicationHost(application.Manifest, [], cancelledHost))
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await cancelledApplication.RunAsync(cancellation.Token);
        return 5;
    }
    catch (OperationCanceledException)
    {
        if (!cancelledHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 6;
    }
}
var controlledStopHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
controlledStopHost.CompleteShutdown();
await using (var controlledStopApplication = new ApplicationHost(application.Manifest, [], controlledStopHost))
{
    await controlledStopApplication.RunAsync();
}
if (!controlledStopHost.Lifecycle.SequenceEqual(["start", "wait", "stop", "dispose"])) return 7;
Console.WriteLine(application.Manifest.ToJson());
return application.Manifest.EntryPoint == "package.consumer" ? 0 : 1;
