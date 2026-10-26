using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using Runic.Application.Views.Desktop;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using static Runic.Sandbox.Probe.ProbeWindow;

namespace Runic.Sandbox.Probe;

/// <summary>
/// <c>--sandbox-probe &lt;outside&gt;</c>: what Runic Desktop can do in the App Sandbox without a dialog. It prints the
/// window's capability snapshot and the application services that report themselves unavailable, saves a file in the
/// container twice through a save lease, keeps it with a security-scoped bookmark, and finds that a file outside the
/// container, which the user never chose, can be neither saved nor bookmarked.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class SandboxProbe
{
    internal static int Run(string outside) => ProbeWindow.Run(context => RunAsync(context, outside));

    private static async Task RunAsync(ProbeContext context, string outside)
    {
        Check(Path.IsPathFullyQualified(outside), $"a fully qualified path outside the container: {outside}");
        await ReportCapabilitiesAsync(context);
        // The container's own Application Support folder: the sandbox redirects HOME into the container.
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "org.runic.tests.SandboxProbe");
        Check(directory.Contains("/Library/Containers/", StringComparison.Ordinal), $"application data in the container: {directory}");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "saved.txt");
        File.Delete(file);
        try
        {
            await SaveAsync(context, directory, "first");
            await SaveAsync(context, directory, "second");
            Check(await File.ReadAllTextAsync(file) == "second", "the second save replaced the first");
            Console.WriteLine("PASS container save: a save lease created the file and replaced it atomically.");

            var bookmarks = MacOSPlatformProvider.CreateFileBookmarks();
            var created = await bookmarks.CreateAsync(file);
            Check(created.TryGetValue(out var bookmark) && bookmark.Length > 0, $"a bookmark of the saved file: {created}");
            var resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out var access), $"the bookmark resolves: {resolved}");
            await using (access!)
                Check(await File.ReadAllTextAsync(access!.LocalPath) == "second" && !access.IsStale, $"the bookmark reaches the saved file: {access.LocalPath}");
            var refused = await bookmarks.CreateAsync(outside);
            Check(refused is PlatformResult<ImmutableArray<byte>>.Failed, $"no bookmark for the file outside the container: {refused}");
            Console.WriteLine($"PASS bookmarks: security-scoped bookmark of the saved file; outside the container {refused}.");

            string outcome = await SaveOutsideAsync(context, outside);
            Console.WriteLine($"PASS outside save: a lease for a file the user never chose did not write it ({outcome}).");
        }
        finally { File.Delete(file); }
        Console.WriteLine("SANDBOX_PROBE_OK");
    }

    // The window's snapshot, and the application services whose result shows what the sandbox takes away. Only the
    // file rows are checked; the rest is printed for the record.
    private static async Task ReportCapabilitiesAsync(ProbeContext context)
    {
        var snapshot = context.Services.GetSnapshot();
        foreach ((string id, CapabilityStatus status) in snapshot.Statuses.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            Console.WriteLine($"CAPABILITY {id} {Describe(status)}");
        Check(snapshot.Statuses.GetValueOrDefault(PlatformCapabilityIds.FilesOpen) is CapabilityStatus.Available, "an owned open dialog is available");
        Check(snapshot.Statuses.GetValueOrDefault(PlatformCapabilityIds.DialogsOwned) is CapabilityStatus.Available, "owned dialogs are available");
        // A user-selected grant covers the selected file only, never a staging file beside it: a chosen file saves
        // through the item replacement directory and NSFileManager instead (A1, --document-probe), so saving is available.
        Check(snapshot.Statuses.GetValueOrDefault(PlatformCapabilityIds.FilesSave) is CapabilityStatus.Available,
            $"the save dialog is available in the sandbox, not {Describe(snapshot.Statuses.GetValueOrDefault(PlatformCapabilityIds.FilesSave))}");

        await using (var volumes = MacOSPlatformProvider.CreateVolumes())
            Console.WriteLine($"SERVICE volumes.list {Describe(await volumes.ListAsync())}");
        Console.WriteLine($"SERVICE loginStartup.isEnabled {Describe(await MacOSPlatformProvider.CreateLoginStartup().IsEnabledAsync())}");
        Console.WriteLine($"SERVICE openRegistration.isRegistered {Describe(await MacOSPlatformProvider.CreateOpenRegistration()
            .IsRegisteredAsync(new DesktopOpenRegistration(Title) { UrlSchemes = ["runic-sandbox-probe"] }))}");
    }

    // One save through the platform's lease and staged write, with a picker that returns the container file instead
    // of showing a dialog: the container is the application's own, so staging beside the file is allowed there.
    private static async Task SaveAsync(ProbeContext context, string directory, string text)
    {
        var backend = new NativePickerBackend(new DesktopNativeOwner(context.Window), new FixedPicker(directory, true));
        var picked = await backend.SaveFileAsync(new SaveFileOptions("saved.txt"));
        Check(picked is PickerResult<ISaveFileLease>.Selected, $"a save lease in the container: {picked}");
        await using var lease = ((PickerResult<ISaveFileLease>.Selected)picked).Value;
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        Check(begun.TryGetValue(out var transaction), $"a staged write in the container: {begun}");
        await using (transaction!)
        {
            await transaction!.Content.WriteAsync(Encoding.UTF8.GetBytes(text));
            var committed = await transaction.CommitAsync();
            Check(committed is FileCommitResult.Committed, $"the save commits: {committed}");
        }
    }

    // The same lease for a file outside the container: the sandbox refuses it at some step, and the file keeps its
    // content (CI compares it after the probe exits).
    private static async Task<string> SaveOutsideAsync(ProbeContext context, string outside)
    {
        var backend = new NativePickerBackend(new DesktopNativeOwner(context.Window),
            new FixedPicker(Path.GetDirectoryName(outside)!, true, Path.GetFileName(outside)));
        var picked = await backend.SaveFileAsync(new SaveFileOptions(Path.GetFileName(outside)));
        if (picked is not PickerResult<ISaveFileLease>.Selected selected) return $"lease {picked}";
        await using var lease = selected.Value;
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        if (!begun.TryGetValue(out var transaction)) return $"write {begun}";
        await using (transaction)
        {
            try { await transaction.Content.WriteAsync("overwritten"u8.ToArray()); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return $"stream {error.GetType().Name}"; }
            var committed = await transaction.CommitAsync();
            Check(committed is not FileCommitResult.Committed, $"the sandbox refuses the save outside the container, not {committed}");
            return $"commit {committed}";
        }
    }

    private static string Describe(CapabilityStatus? status) => status switch
    {
        CapabilityStatus.Available => "Available",
        CapabilityStatus.Unavailable unavailable => $"Unavailable({unavailable.Reason})",
        _ => "missing",
    };

    private static string Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<bool>.Success { Value: var value } => $"Success({value})",
        PlatformResult<T>.Success => "Success",
        PlatformResult<T>.Unavailable unavailable => $"Unavailable({unavailable.Reason})",
        PlatformResult<T>.Failed failed => $"Failed({failed.Code})",
        _ => result.GetType().Name,
    };

    /// <summary>A picker that selects a fixed file instead of showing a dialog.</summary>
    private sealed class FixedPicker(string directory, bool siblingReplacement, string? name = null) : INativeFilePicker
    {
        public bool SupportsAtomicReplace => siblingReplacement;

        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NativeFileSelection>?>(
                [new NativeFileSelection(Path.Combine(directory, name ?? request.SuggestedName!), null, siblingReplacement)]);
    }
}
