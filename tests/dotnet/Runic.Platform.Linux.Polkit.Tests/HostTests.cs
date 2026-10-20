using System.Diagnostics;
using Runic.Platform.Linux.Polkit;
using TUnit.Core;
using static DeclarationTests;

/// <summary>The real filesystem checks: only root may be able to replace the program pkexec runs.</summary>
internal sealed class HostTests
{
    [Test, DisplayName("api: the helper's cancellation token has the same name as on Windows (W319-003 D27 k)")]
    public void CancellationName()
    {
        Check(typeof(PolkitHelper).GetMethod(nameof(PolkitHelper.WatchCancellation))?.ReturnType == typeof(CancellationToken), "WatchCancellation");
        Check(typeof(PolkitHelper).GetMethod("WatchStandardInput") is null, "the mechanism-named WatchStandardInput is gone");
    }

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
        // /tmp is root's and sticky but writable by everyone, so another user could have created the path.
        Check(host.CheckProgram("/tmp/runic-polkit-absent/helper") == ProgramCheck.Replaceable, "a world-writable sticky directory is not trusted");

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
        if (Environment.UserName == "root") return; // a root caller never kills: pkexec and the program look alike
        foreach (var withPidfd in new[] { true, false })
        {
            using var process = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
            using var pidfd = withPidfd ? LinuxPolkitHost.Instance.OpenProcess(process.Id) : ProcessHandle.None;
            Check(!withPidfd || pidfd.IsValid, "pidfd_open is available");
            Check(LinuxPolkitHost.Instance.TryStopBeforeStart(process, pidfd), $"the caller's own process can be stopped (pidfd {withPidfd})");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(!LinuxPolkitHost.Instance.TryStopBeforeStart(process, pidfd), "an exited process is not stopped again");
        }
    }

    private static string RealPath(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("realpath", path) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return result;
    }
}
