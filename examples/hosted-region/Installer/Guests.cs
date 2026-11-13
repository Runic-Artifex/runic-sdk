using System.Reflection;
using Runic.Application.Views.Desktop;

namespace HostedInstaller;

// How the installer runs: --elevated makes it the elevated host of an elevated check (README, "Elevated").
public sealed record InstallerOptions(bool Elevated);

public static class Guests
{
    // The check's executable, with the parameter that selects its hosted mode (../Check/Program.cs), started directly,
    // never through a shell. AllowElevatedGuest lets an elevated installer host the check, which then runs elevated too
    // and checks that its parent is this installer, elevated and the same user. An installer that does not run elevated
    // cannot allow it: the start fails with host-not-elevated.
    public static RunicGuestStartInfo Check(bool elevated) => new(CheckPath(), ["hosted"])
    {
        AllowElevatedGuest = elevated,
        // The user asked for the check by entering its step.
        UserStarted = true,
    };

    // HostedInstaller.csproj records where the check is built, as a fully qualified path.
    private static string CheckPath()
    {
        var path = typeof(Guests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CheckTool").Value!;
        return OperatingSystem.IsWindows() ? path + ".exe" : path;
    }
}
