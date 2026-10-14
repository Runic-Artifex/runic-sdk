using System.Diagnostics;
using Runic.Platform.Linux.Polkit;
using TUnit.Core;
using static DeclarationTests;

/// <summary>The real filesystem checks: only root may be able to replace the program pkexec runs.</summary>
internal sealed class HostTests
{
    [Test, DisplayName("host: a root-owned program in root-owned directories is trusted")]
    public void TrustedProgram()
    {
        if (!OperatingSystem.IsLinux()) return;
        var shell = RealPath("/bin/sh");
        Check(LinuxPolkitHost.Instance.CheckProgram(shell) == ProgramCheck.Trusted, $"{shell} (the store on NixOS is sticky and group-writable)");
    }

    [Test, DisplayName("host: symbolic links, other owners and missing programs are not trusted")]
    public void UntrustedPrograms()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = LinuxPolkitHost.Instance;
        var shell = RealPath("/bin/sh");
        if (shell != "/bin/sh") Check(host.CheckProgram("/bin/sh") == ProgramCheck.NotRealPath, "/bin/sh resolves elsewhere, so pkexec would not match the policy");
        Check(host.CheckProgram("/nonexistent/runic/helper") == ProgramCheck.Missing, "missing");
        Check(host.CheckProgram(shell + "/child") == ProgramCheck.Missing, "a file used as a directory");
        Check(host.CheckProgram("/proc/self") is ProgramCheck.NotRealPath, "a symbolic link");

        var directory = Path.Combine(Path.GetTempPath(), "runic-polkit-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var mine = Path.Combine(directory, "helper");
            File.WriteAllText(mine, "#!/bin/sh\n");
            File.SetUnixFileMode(mine, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            if (Environment.UserName != "root") Check(host.CheckProgram(mine) == ProgramCheck.Replaceable, "a program the user owns can be replaced by the user");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("host: stopping pkexec works while it runs as the caller, and not after it exited")]
    public async Task StopBeforeStartAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        Check(LinuxPolkitHost.Instance.TryStopBeforeStart(process), "the caller's own process can be stopped");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(!LinuxPolkitHost.Instance.TryStopBeforeStart(process), "an exited process is not stopped again");
    }

    private static string RealPath(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("realpath", path) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return result;
    }
}
