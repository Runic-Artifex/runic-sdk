using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal static class BridgeInspectionClient
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 1) throw new ArgumentException("Bridge inspection requires one .NET project path.");
        string inspector = Path.Combine(AppContext.BaseDirectory, "bridge-inspector", "Runic.Application.Bridge.Inspector.dll");
        if (!File.Exists(inspector)) throw new FileNotFoundException("The managed bridge inspector is missing from the Runic tool installation.", inspector);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(inspector);
        start.ArgumentList.Add(Path.GetFullPath(arguments[0]));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the managed bridge inspector.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
