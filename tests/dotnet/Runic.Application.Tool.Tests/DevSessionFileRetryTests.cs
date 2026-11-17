using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// #1102: on Windows, the rename over dev-session.json or the frontend marker fails while another process holds the file
// open without delete sharing; dev retries briefly, and a write that still fails is not lost.
internal sealed partial class ToolTests
{
    private static readonly int SharingViolation = unchecked((int)0x80070020);
    private static readonly int AccessDenied = unchecked((int)0x80070005);

    [Test, DisplayName("a rename that Windows refused is retried until it succeeds, for about 0.6 s")]
    public void RefusedRenameOnWindowsIsRetried()
    {
        var waits = new List<TimeSpan>();
        int attempts = 0;
        FileReplace.Move("a.tmp", "a.json", (_, _) =>
        {
            attempts++;
            if (attempts == 1) throw new IOException("The process cannot access the file.", SharingViolation);
            if (attempts == 2) throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = AccessDenied };
        }, waits.Add, windows: true);
        Equal(3, attempts);
        SequenceEqual(FileReplace.RetryDelays.Take(2).ToArray(), waits);

        // A rename that stays refused reports the last error after the last retry.
        waits.Clear();
        attempts = 0;
        IOException error = Throws<IOException>(() => FileReplace.Move("a.tmp", "a.json", (_, _) =>
        {
            attempts++;
            throw new IOException("The process cannot access the file.", SharingViolation);
        }, waits.Add, windows: true));
        Equal(SharingViolation, error.HResult);
        Equal(FileReplace.RetryDelays.Length + 1, attempts);
        double total = waits.Sum(static wait => wait.TotalMilliseconds);
        True(total is > 300 and < 1000, $"The retries wait {total} ms; a held file is free about 177 ms after release.");
    }

    [Test, DisplayName("other errors, and access errors off Windows, are not retried")]
    public void OtherRenameErrorsAreNotRetried()
    {
        int attempts = 0;
        Throws<DirectoryNotFoundException>(() => FileReplace.Move("a.tmp", "a.json", (_, _) =>
        {
            attempts++;
            throw new DirectoryNotFoundException("Could not find a part of the path.");
        }, static _ => throw new InvalidOperationException("Waited."), windows: true));
        Equal(1, attempts);

        attempts = 0;
        Throws<UnauthorizedAccessException>(() => FileReplace.Move("a.tmp", "a.json", (_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = AccessDenied };
        }, static _ => throw new InvalidOperationException("Waited."), windows: false));
        Equal(1, attempts);
    }

    [Test, DisplayName("a session file that dev could not replace names the path and is written by the next try")]
    public void UnwrittenSessionFileIsWrittenLater()
    {
        using var workspace = new TestWorkspace();
        var session = new DevSession(CreateConfiguration(workspace, "vite"), new DevOptions(null, "Debug", true, true, true, false, []), [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        session.Start();
        string held = session.Path + ".held";
        File.Move(session.Path, held);
        // A directory in its place refuses the rename on every system, and longer than the retries.
        Directory.CreateDirectory(session.Path);
        DevSessionFileException error = Throws<DevSessionFileException>(session.Ready);
        Equal(session.Path, error.Path);
        True(error.Message.Contains(session.Path, StringComparison.Ordinal), $"The message does not name the file: {error.Message}");
        True(session.HasPendingWrites, "The failed write is not pending.");
        SequenceEqual(["dev-session.json", "dev-session.json.held"],
            Directory.GetFileSystemEntries(Path.GetDirectoryName(session.Path)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

        // Still refused: the heartbeat's retry fails quietly and keeps it pending.
        session.RetryPendingWrites();
        True(session.HasPendingWrites, "A refused retry cleared the pending write.");

        Directory.Delete(session.Path);
        File.Move(held, session.Path);
        session.RetryPendingWrites();
        False(session.HasPendingWrites, "The retried write is still pending.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
        Equal("ready", document.RootElement.GetProperty("state").GetString());
    }

    // In the WindowsCommands category, which the native job's Windows x64 leg runs, the leg that DevSession.cs plans.
    [Test, Category("WindowsCommands"), DisplayName("on Windows, dev replaces a session file and marker that a reader holds briefly without delete sharing")]
    public async Task HeldSessionFileIsReplacedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        var session = new DevSession(CreateConfiguration(workspace, "vite"), new DevOptions(null, "Debug", true, true, true, false, []), [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        session.Start();
        session.PublishFrontend([]);

        foreach ((string path, Action write) in new (string, Action)[] { (session.Path, session.Ready), (session.FrontendMarkerPath, () => session.PublishFrontend([])) })
        {
            // An IDE, a scan or an indexer reads the file and lets go about 150 ms later.
            var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task release = Task.Run(async () =>
            {
                await Task.Delay(150).ConfigureAwait(false);
                await holder.DisposeAsync().ConfigureAwait(false);
            });
            write();
            await release.ConfigureAwait(false);
        }

        False(session.HasPendingWrites, "A held file was not replaced once released.");
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal("ready", document.RootElement.GetProperty("state").GetString());
        }
        SequenceEqual([DevSession.FrontendMarkerName, "dev-session.json"],
            Directory.GetFiles(Path.GetDirectoryName(session.Path)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }
}
