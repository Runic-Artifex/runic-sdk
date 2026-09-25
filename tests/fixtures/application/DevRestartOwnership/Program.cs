using System.Reflection;
using System.Security.Cryptography;

using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Contract.cs")
    ?? throw new InvalidOperationException("Compiled contract resource is missing.");
string fingerprint = Convert.ToHexString(SHA256.HashData(source));
string startsPath = Environment.GetEnvironmentVariable("RUNIC_PROBE_STARTS_PATH")
    ?? throw new InvalidOperationException("RUNIC_PROBE_STARTS_PATH is required.");
string hostReadyPath = Environment.GetEnvironmentVariable("RUNIC_VIEW_BRIDGE_HOST_READY")
    ?? throw new InvalidOperationException("RUNIC_VIEW_BRIDGE_HOST_READY is required.");

Directory.CreateDirectory(Path.GetDirectoryName(startsPath)!);
File.AppendAllText(startsPath, $"{Environment.ProcessId} {fingerprint}{Environment.NewLine}");
string? readyGate = Environment.GetEnvironmentVariable("RUNIC_PROBE_HOST_READY_GATE_PATH");
if (readyGate is not null && File.ReadAllLines(startsPath).Length > 1)
{
    Console.WriteLine($"PROBE_HOST_WAITING_FOR_GATE {Environment.ProcessId}");
    while (!File.Exists(readyGate))
        await Task.Delay(25);
}
Directory.CreateDirectory(Path.GetDirectoryName(hostReadyPath)!);
string temporary = hostReadyPath + "." + Environment.ProcessId + ".tmp";
File.WriteAllText(temporary, fingerprint + Environment.NewLine);
File.Move(temporary, hostReadyPath, overwrite: true);
Console.WriteLine($"PROBE_HOST_STARTED {Environment.ProcessId} {fingerprint}");
await Task.Delay(Timeout.InfiniteTimeSpan);
