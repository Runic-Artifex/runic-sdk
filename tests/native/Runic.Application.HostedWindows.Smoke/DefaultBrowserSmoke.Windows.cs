using System.Runtime.Versioning;
using Microsoft.Win32;
using Runic.Application.Views.Desktop;
using Runic.Platform;

internal static partial class DefaultBrowserSmoke
{
    // AssocIsDangerous against a type Windows registers itself: .cer (CERFile, the certificate dialog), which the shell
    // lists as dangerous and Runic's own list of program types does not name. Opening it is refused before the shell, so
    // no certificate dialog opens.
    [SupportedOSPlatform("windows")]
    private static async Task DangerousTypeAsync()
    {
        string? registered;
        using (var type = Registry.ClassesRoot.OpenSubKey(".cer")) registered = type?.GetValue("") as string;
        Console.WriteLine($"Default browser: .cer is registered as '{registered ?? "(nothing)"}'.");
        var directory = Directory.CreateTempSubdirectory("runic-dangerous-type-");
        try
        {
            var file = Path.Combine(directory.FullName, "runic-launch-smoke.cer");
            await File.WriteAllTextAsync(file, "not a certificate");
            var result = await DesktopPlatformServices.ForApplication().FileLauncher.LaunchAsync(file).AsTask()
                .WaitAsync(TestTimeouts.Scale(StepTimeout));
            if (result is not PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied })
                throw new InvalidOperationException($"LaunchAsync of a .cer file returned {result}, not PermissionDenied.");
            Console.WriteLine("PASS default browser: LaunchAsync refused a .cer file (AssocIsDangerous, a type Windows registers) with PermissionDenied.");
        }
        finally
        {
            Remove(directory);
        }
    }
}
