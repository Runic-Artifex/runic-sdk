using System.Reflection;
using Runic.Application.Views.Desktop;

namespace HostedShell;

public static class Guests
{
    // The counter's executable, with the parameter that selects its hosted mode (../Counter/Program.cs). The shell starts
    // it directly, never through a shell, so the arguments reach it as they are.
    public static RunicGuestStartInfo Counter { get; } = new(CounterPath(), ["hosted"]);

    // HostedShell.csproj records where the counter is built.
    private static string CounterPath()
    {
        var path = typeof(Guests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CounterTool").Value!;
        return OperatingSystem.IsWindows() ? path + ".exe" : path;
    }
}
