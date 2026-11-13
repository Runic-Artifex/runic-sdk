using System.Reflection;
using Runic.Application.Views.Desktop;

namespace HostedDashboard;

// How the dashboard runs: --elevated makes it the elevated host of elevated widgets (examples/hosted-region explains
// the elevated pair).
public sealed record DashboardOptions(bool Elevated);

public static class Guests
{
    // The dashboard's two widgets: two processes of one widget application, each for another service.
    public static readonly IReadOnlyList<(string Region, string Service)> Widgets = [("indexer", "Indexer"), ("sync", "Sync")];

    public static RunicGuestStartInfo Widget(string service, bool elevated) => new(WidgetPath(), ["hosted", service])
    {
        AllowElevatedGuest = elevated,
    };

    // HostedDashboard.csproj records where the widget is built, as a fully qualified path.
    private static string WidgetPath()
    {
        var path = typeof(Guests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "WidgetTool").Value!;
        return OperatingSystem.IsWindows() ? path + ".exe" : path;
    }
}
