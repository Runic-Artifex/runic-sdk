using System.Diagnostics;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// The shared process stop and directory delete (tests/support/TestProcesses.cs), checked here once for every project
// that compiles it.
internal sealed class TestProcessesTests
{
    [Test]
    public async Task AStopWaitsForTheDescendantsItSawOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 60 & sleep 60 & wait" } };
        using var root = Process.Start(start) ?? throw new InvalidOperationException("sh did not start.");
        IReadOnlyList<(int Id, string Started)> descendants = [];
        for (var attempt = 0; attempt < 200 && descendants.Count < 2; attempt++)
        {
            await Task.Delay(10);
            descendants = TestProcesses.Descendants(root.Id);
        }
        Require(descendants.Count == 2, $"sh's two sleeps were not found: {descendants.Count}.");
        await TestProcesses.StopTreeAsync(root);
        Require(root.HasExited, "The root did not exit.");
        foreach (var (id, _) in descendants)
            Require(!File.Exists($"/proc/{id}/stat") || File.ReadAllText($"/proc/{id}/stat").Split(") ")[1][0] == 'Z',
                $"Descendant {id} still runs after the stop.");
        await TestProcesses.StopTreeAsync(root);
    }

    [Test]
    public async Task ADirectoryIsDeletedOnceAndAMissingOneIsNoFailure()
    {
        var directory = Directory.CreateTempSubdirectory("runic-test-processes-");
        File.WriteAllText(Path.Combine(directory.FullName, "file"), "x");
        TestProcesses.DeleteDirectory(directory.FullName);
        Require(!directory.Exists && !Directory.Exists(directory.FullName), "The directory remained.");
        TestProcesses.DeleteDirectory(directory.FullName);
        await TestProcesses.DeleteDirectoryAsync(directory.FullName);
    }

    [Test]
    public async Task OnWindowsADeleteWaitsForAFileToBeReleased()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("runic-test-processes-");
        var held = new FileStream(Path.Combine(directory.FullName, "held"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Delay(200).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
        await TestProcesses.DeleteDirectoryAsync(directory.FullName);
        await release;
        Require(!Directory.Exists(directory.FullName), "The directory remained after the file was released.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
