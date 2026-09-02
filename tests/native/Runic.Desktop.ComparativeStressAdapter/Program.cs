using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Runic.Desktop;

var payloadBytes = ReadPayloadSize();
var payload = Enumerable.Repeat((byte)'R', payloadBytes).ToArray();
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
{
    WaitForConnection = false,
});
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Path = "stress",
    ContentHandler = (request, _) => ValueTask.FromResult<ContentResponse?>(
        request.Path == "/payload"
            ? new ContentResponse(payload, "application/octet-stream")
            : null),
});

Write(new
{
    schema = "runic.comparative-stress-adapter-ready/1",
    implementation = "runic-desktop",
    revision = Environment.GetEnvironmentVariable("RUNIC_STRESS_IMPLEMENTATION_REVISION"),
    url = new Uri(surface.Url, "payload").AbsoluteUri,
    runtime = RuntimeInformation.FrameworkDescription,
    adapterAssemblySha256 = Hash(Assembly.GetExecutingAssembly().Location),
    implementationAssemblySha256 = Hash(typeof(DesktopHost).Assembly.Location),
    shutdownBoundary = "graceful-dispose",
});

if (!string.Equals(await Console.In.ReadLineAsync(), "stop", StringComparison.Ordinal))
{
    return 2;
}

using var process = Process.GetCurrentProcess();
Write(new
{
    schema = "runic.comparative-stress-adapter-stopped/1",
    implementation = "runic-desktop",
    managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
    peakWorkingSetBytes = process.PeakWorkingSet64,
});
return 0;

static int ReadPayloadSize()
{
    var value = Environment.GetEnvironmentVariable("RUNIC_STRESS_PAYLOAD_BYTES");
    return int.TryParse(value, out var result) && result is > 0 and <= 1_048_576
        ? result
        : throw new InvalidOperationException("RUNIC_STRESS_PAYLOAD_BYTES must be between 1 and 1048576.");
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static void Write<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
