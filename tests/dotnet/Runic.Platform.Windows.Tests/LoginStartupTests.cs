using Microsoft.Win32;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

internal sealed class LoginStartupTests
{
    // Pure quoting; runs on every OS.
    [Test]
    public void QuotesArgumentsAsWindowsReadsThem()
    {
#pragma warning disable CA1416 // Quote is pure string handling; nothing here calls native code.
        Check(WindowsLoginStartup.Quote("plain") == "plain");
        Check(WindowsLoginStartup.Quote("") == "\"\"");
        Check(WindowsLoginStartup.Quote(@"C:\Program Files\App\app.exe") == "\"C:\\Program Files\\App\\app.exe\"");
        Check(WindowsLoginStartup.Quote("say \"hi\"") == "\"say \\\"hi\\\"\"");
        Check(WindowsLoginStartup.Quote(@"ends in \ ") == "\"ends in \\ \"");
        Check(WindowsLoginStartup.Quote(@"dir with space\") == "\"dir with space\\\\\"");
#pragma warning restore CA1416
    }

    // The Run and StartupApproved keys, in a scratch key under the current user.
    [Test, Explicit, Category("NativeLoginStartup")]
    public async Task RunKeyAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Login startup requires Windows.");
        string root = @"Software\Runic.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var startup = new WindowsLoginStartup("org.example.App", root + @"\Run", root + @"\Approved");
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: false });
            Check(await startup.EnableAsync(new LoginStartupRequest(["--background", "two words"])) is PlatformResult<PlatformUnit>.Success);
            using (var run = Registry.CurrentUser.OpenSubKey(root + @"\Run"))
                Check(run?.GetValue("org.example.App") is string command && command.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase)
                    && command.EndsWith(" --background \"two words\"", StringComparison.Ordinal));
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: true });

            // Task Manager turned it off.
            using (var approved = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
                approved.SetValue("org.example.App", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: false });
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied });

            // Disabling forgets the user's choice, so the application can enable it again.
            Check(await startup.DisableAsync() is PlatformResult<PlatformUnit>.Success);
            using (var approved = Registry.CurrentUser.OpenSubKey(root + @"\Approved"))
                Check(approved?.GetValue("org.example.App") is null);
            Check(await startup.EnableAsync(new LoginStartupRequest([])) is PlatformResult<PlatformUnit>.Success);
            Check(await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: true });

            Check(await startup.DisableAsync() is PlatformResult<PlatformUnit>.Success);
            using (var run = Registry.CurrentUser.OpenSubKey(root + @"\Run"))
                Check(run?.GetValue("org.example.App") is null);
            Console.WriteLine("PASS Windows login startup: Run value written, quoted, read, respected when turned off, removed, enabled again.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Login startup check failed.");
    }
}
