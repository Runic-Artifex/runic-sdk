using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Bridge;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("tests.application", Version = "1.0.0", Provenance = "test")]
[assembly: RunicApplicationBridgeComposition(typeof(Runic.Application.Tests.BridgeCompositionHandler), typeof(Runic.Application.Tests.BridgeCompositionDispatcher))]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationCapability("headless")]
[assembly: RunicApplicationArtifact("bridge", "runic.bridge/1", "abc123")]

object? bridgeComposition = RunicApplicationBridgeCompositionRegistry.CreateSession();
if (bridgeComposition is not ApplicationBridgeSession bridgeSession)
{
    return 18;
}
await bridgeSession.DisposeAsync();

DeterministicApplicationTestHost host = new(
    DateTimeOffset.UnixEpoch,
    4,
    [new("MODE", "test")],
    capabilities: [
        ApplicationCapabilityStatus.Available("bridge"),
        ApplicationCapabilityStatus.Unavailable("desktop", "headless-test-host"),
        ApplicationCapabilityStatus.Available("headless"),
    ]);
ApplicationHost application = RunicApplication.CreateBuilder([]).UseHost(host).Build();
await application.RunAsync();
if (host.Manifest is null || host.Manifest.Schema != "runic.application/1" || host.Manifest.Capabilities.Length != 2)
{
    return 1;
}
if (application.Capabilities.GetRequired("headless").Availability != ApplicationCapabilityAvailability.Available ||
    application.Capabilities.GetRequired("desktop").UnavailableReason != "headless-test-host" ||
    application.Capabilities.Statuses.Length != 2)
{
    return 16;
}
try
{
    _ = application.Capabilities.GetRequired("bridge");
    return 17;
}
catch (ArgumentException)
{
    // Only the generated manifest can declare a capability.
}
if (host.Ids.Next("window") != "window-00000005" || host.Environment.Get("MODE") != "test")
{
    return 2;
}
int timerCallbacks = 0;
using (host.Clock.CreateTimer(_ => timerCallbacks++, null, TimeSpan.Zero, TimeSpan.Zero))
{
    host.Timers.Advance(TimeSpan.Zero);
    host.Timers.Advance(TimeSpan.Zero);
}
if (timerCallbacks != 1)
{
    return 4;
}
var boundedClock = new DeterministicClock(DateTimeOffset.UnixEpoch);
var boundedTimers = new DeterministicTimerScheduler(boundedClock, maximumCallbacksPerAdvance: 2);
Action? selfRequeue = null;
selfRequeue = () => boundedTimers.Schedule(TimeSpan.Zero, selfRequeue!);
boundedTimers.Schedule(TimeSpan.Zero, selfRequeue);
try
{
    boundedTimers.Advance(TimeSpan.Zero);
    return 5;
}
catch (InvalidOperationException)
{
    // The due callback remains queued, so repeated tests observe the same bounded state.
}
host.Bridge.Send("ping", new byte[] { 1, 2 });
if (!host.Bridge.TryReceive(out ApplicationBridgeMessage? message) || message?.Operation != "ping")
{
    return 3;
}
try
{
    host.Bridge.Send(new string('x', 257), Array.Empty<byte>());
    return 8;
}
catch (InvalidOperationException)
{
    // Semantic names remain bounded just like payloads and queues.
}
try
{
    host.Ids.Next(new string('x', 257));
    return 9;
}
catch (ArgumentOutOfRangeException)
{
    // Deterministic IDs cannot retain unbounded prefixes.
}
host.Assets.Set("app.js", new byte[] { 1, 2, 3 });
await host.Assets.ValidateAsync();
await using (var asset = await host.Assets.OpenReadAsync("app.js"))
{
    if (asset.Length != 3 || host.Assets.Manifest.EntryPoint.RelativePath != "index.html") return 6;
}
var boundedAssets = new InMemoryApplicationAssets(entryPoint: "ui\\index.html");
boundedAssets.Set("styles\\site.css", new byte[] { 1 }, mediaType: "text/css");
if (boundedAssets.Manifest.EntryPoint.RelativePath != "ui/index.html") return 10;
bool retainedMetadata = false;
foreach (var descriptor in boundedAssets.Manifest.Assets)
{
    if (descriptor.RelativePath == "styles/site.css" && descriptor.MediaType == "text/css" && !descriptor.IsEntryPoint)
    {
        retainedMetadata = true;
    }
}
if (!retainedMetadata) return 11;
try
{
    _ = new InMemoryApplicationAssets(entryPoint: " ");
    return 12;
}
catch (ArgumentException)
{
    // The deterministic asset source retains only normalized entry-point paths.
}
try
{
    boundedAssets.Set(new string('p', 4097), Array.Empty<byte>());
    return 13;
}
catch (ArgumentOutOfRangeException)
{
    // Stored paths are bounded before they enter either deterministic dictionary.
}
try
{
    boundedAssets.Set("app.css", Array.Empty<byte>(), mediaType: " text/css");
    return 14;
}
catch (ArgumentException)
{
    // Media types have the same normalized single-value contract as Runic Assets.
}
try
{
    boundedAssets.Set("app.css", Array.Empty<byte>(), mediaType: new string('m', 257));
    return 15;
}
catch (ArgumentOutOfRangeException)
{
    // Retained media type metadata is independently bounded.
}
var faultHost = new DeterministicApplicationTestHost { WaitFailure = new InvalidOperationException("wait"), StopFailure = new InvalidOperationException("stop") };
var faultApplication = new ApplicationHost(application.Manifest, [], faultHost);
try
{
    await faultApplication.RunAsync();
    return 7;
}
catch (InvalidOperationException exception) when (exception.Message == "wait")
{
    // Wait is the primary lifecycle failure even when cleanup also faults.
}
var cancelledHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
var cancelledApplication = new ApplicationHost(application.Manifest, [], cancelledHost);
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await cancelledApplication.RunAsync(cancellation.Token);
        return 18;
    }
    catch (OperationCanceledException)
    {
        if (!cancelledHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 19;
    }
}
await cancelledApplication.DisposeAsync();
var controlledStopHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
controlledStopHost.CompleteShutdown();
await using (var controlledStopApplication = new ApplicationHost(application.Manifest, [], controlledStopHost))
{
    await controlledStopApplication.RunAsync();
}
if (!controlledStopHost.Lifecycle.SequenceEqual(["start", "wait", "stop", "dispose"])) return 20;
await application.DisposeAsync();
Console.WriteLine(host.Manifest.ToJson());
return 0;
