using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The 0.11 additions to Runic.Platform and Runic.Platform.Runtime (B13): result helpers, thumbnail scale, application-scoped
// service status, text scale, trusted notification activations, ClaimAsync with arguments and the Windows staging ACL.
internal sealed class PlatformHelpersTests
{
    [Test, DisplayName("RESULT: Succeeded, TryGetValue and Match cover every case; ToString is unchanged")]
    public void ResultHelpers()
    {
        PlatformResult<int> success = new PlatformResult<int>.Success(42);
        PlatformResult<int> unavailable = new PlatformResult<int>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        PlatformResult<int> failed = new PlatformResult<int>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2));

        Check(success.Succeeded && !unavailable.Succeeded && !failed.Succeeded, "Succeeded is true for Success only.");
        Check(success.TryGetValue(out int value) && value == 42, "TryGetValue returns a success's value.");
        Check(!unavailable.TryGetValue(out value) && value == 0 && !failed.TryGetValue(out value) && value == 0,
            "TryGetValue is false with the default for the other cases.");
        Check(!new PlatformResult<string>.Failed(PlatformFailureCode.IOError).TryGetValue(out string? text) && text is null,
            "TryGetValue leaves a reference type null.");

        string Describe(PlatformResult<int> result) => result.Match(
            v => $"value {v}", u => $"unavailable {u.Reason}", f => $"failed {f.Code} {f.Diagnostic?.Code}");
        Check(Describe(success) == "value 42", Describe(success));
        Check(Describe(unavailable) == "unavailable BackendUnavailable", Describe(unavailable));
        Check(Describe(failed) == "failed NotFound 2", Describe(failed));
        Throws<ArgumentNullException>(() => success.Match(null!, _ => 0, _ => 0));
        Throws<ArgumentNullException>(() => success.Match(v => v, null!, _ => 0));
        Throws<ArgumentNullException>(() => success.Match(v => v, _ => 0, null!));

        // Records print their public properties; the base keeps Succeeded out, so logged results read as in 0.10.
        Check(success.ToString() == "Success { Value = 42 }", success.ToString());
        Check(unavailable.ToString() == "Unavailable { Reason = BackendUnavailable }", unavailable.ToString());
        Check(success == new PlatformResult<int>.Success(42), "Equality is unchanged.");
    }

    [Test, DisplayName("RESULT: PickerResult's TryGetValue and Match cover every case; ToString is unchanged")]
    public void PickerResultHelpers()
    {
        var lease = new Lease();
        var filter = new FileDialogFilter("Text", ["txt"]);
        PickerResult<Lease> selected = new PickerResult<Lease>.Selected(lease) { SelectedFilter = filter };
        PickerResult<Lease> dismissed = new PickerResult<Lease>.Dismissed();
        PickerResult<Lease> unavailable = new PickerResult<Lease>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        PickerResult<Lease> failed = new PickerResult<Lease>.Failed(PlatformFailureCode.NotFound, PlatformDiagnostic.FromErrno(2));

        Check(selected.TryGetValue(out var value) && ReferenceEquals(value, lease), "TryGetValue returns the selected resource.");
        Check(!dismissed.TryGetValue(out value) && value is null && !unavailable.TryGetValue(out value) && value is null
            && !failed.TryGetValue(out value) && value is null, "TryGetValue is false with null for the other cases.");

        string Describe(PickerResult<Lease> result) => result.Match(
            s => $"selected {s.SelectedFilter?.Name}", _ => "dismissed", u => $"unavailable {u.Reason}", f => $"failed {f.Code} {f.Diagnostic?.Code}");
        Check(Describe(selected) == "selected Text", Describe(selected));
        Check(Describe(dismissed) == "dismissed", Describe(dismissed));
        Check(Describe(unavailable) == "unavailable BackendUnavailable", Describe(unavailable));
        Check(Describe(failed) == "failed NotFound 2", Describe(failed));
        Throws<ArgumentNullException>(() => selected.Match(null!, _ => 0, _ => 0, _ => 0));
        Throws<ArgumentNullException>(() => selected.Match(_ => 0, null!, _ => 0, _ => 0));
        Throws<ArgumentNullException>(() => selected.Match(_ => 0, _ => 0, null!, _ => 0));
        Throws<ArgumentNullException>(() => selected.Match(_ => 0, _ => 0, _ => 0, null!));

        Check(dismissed.ToString() == "Dismissed { }", dismissed.ToString());
        Check(unavailable.ToString() == "Unavailable { Reason = BackendUnavailable }", unavailable.ToString());
    }

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test, DisplayName("THUMBNAILS: the scale overload asks for device pixels, at most MaximumSize")]
    public async Task ThumbnailScaleAsync()
    {
        var recorder = new RecordingThumbnails();
        IDesktopThumbnails thumbnails = recorder;
        string path = Path.Combine(Path.GetTempPath(), "image.png");
        await thumbnails.GetThumbnailAsync(path, 128, 2, default);
        await thumbnails.GetThumbnailAsync(path, 200, 3, default);
        await thumbnails.GetThumbnailAsync(path, 1024, 16, default);
        await thumbnails.GetThumbnailAsync(path, 64, 1, default);
        // Three arguments still bind to the overload without a scale.
        await thumbnails.GetThumbnailAsync(path, 96, default);
        Check(recorder.Sizes.SequenceEqual([256, 600, 1024, 64, 96]), "Sizes: " + string.Join(",", recorder.Sizes));
        Check(DesktopThumbnails.MaximumScale == 16, "The largest scale matches DesktopIconRequest.");

        await ThrowsAsync<ArgumentOutOfRangeException>(() => thumbnails.GetThumbnailAsync(path, 128, 0, default).AsTask());
        await ThrowsAsync<ArgumentOutOfRangeException>(() => thumbnails.GetThumbnailAsync(path, 128, 17, default).AsTask());
        await ThrowsAsync<ArgumentOutOfRangeException>(() => thumbnails.GetThumbnailAsync(path, 0, 2, default).AsTask());
        await ThrowsAsync<ArgumentOutOfRangeException>(() => thumbnails.GetThumbnailAsync(path, 1025, 1, default).AsTask());
        Check(recorder.Sizes.Count == 5, "Invalid requests never reach the provider.");

        IDesktopThumbnails unavailable = DesktopThumbnails.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.GetThumbnailAsync(path, 128, 2, default) is PlatformResult<DesktopThumbnail?>.Unavailable
            { Reason: PlatformUnavailableReason.ProviderNotConfigured }, "The unavailable provider answers the scale overload.");
        await ThrowsAsync<ArgumentException>(() => unavailable.GetThumbnailAsync("relative.png", 128, 2, default).AsTask());
    }

    [Test, DisplayName("STATUS: Unavailable factories report their reason; other services report available")]
    public async Task ServiceStatusAsync()
    {
        const PlatformUnavailableReason reason = PlatformUnavailableReason.ProviderNotConfigured;
        object[] services =
        [
            DesktopSettings.Unavailable(reason), DesktopNotifications.Unavailable(reason), DesktopStatusIcon.Unavailable(reason),
            DesktopLoginStartup.Unavailable(reason), DesktopInhibition.Unavailable(reason), DesktopSecretStore.Unavailable(reason),
            DesktopOpenRequests.Unavailable(reason), DesktopTrash.Unavailable(reason), DesktopFileTransfers.Unavailable(reason),
            DesktopVolumes.Unavailable(reason), DesktopApplications.Unavailable(reason), DesktopThumbnails.Unavailable(reason),
            DesktopPrivilegedActions.Unavailable(reason), DesktopGlobalShortcuts.Unavailable(reason),
        ];
        foreach (object service in services)
        {
            Check(DesktopServiceStatus.Get(service) is CapabilityStatus.Unavailable { Reason: reason }, service.GetType().Name + " reports its reason.");
            if (service is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        Check(DesktopServiceStatus.Get(new RecordingThumbnails()) is CapabilityStatus.Available, "A service without a status is available.");
        Check(DesktopServiceStatus.Get(LocalFileTransfers.Create()) is CapabilityStatus.Available, "The local file transfers are available.");
        Throws<ArgumentNullException>(() => DesktopServiceStatus.Get(null!));
    }

    [Test, DisplayName("APPEARANCE: TextScale is optional, validated and part of equality")]
    public void TextScale()
    {
        var appearance = new DesktopAppearance(DesktopColorScheme.Dark);
        Check(appearance.TextScale is null, "Unknown by default.");
        var scaled = appearance with { TextScale = 1.5 };
        Check(scaled.TextScale == 1.5 && scaled != appearance && scaled == appearance with { TextScale = 1.5 },
            "A text scale change is a different appearance, so a watch reports it.");
        var (scheme, accent, contrast, motion) = scaled;
        Check(scheme == DesktopColorScheme.Dark && accent is null && contrast is null && motion is null, "Deconstruct is unchanged.");
        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Throws<ArgumentOutOfRangeException>(() => _ = appearance with { TextScale = invalid });
    }

    [Test, DisplayName("NOTIFICATIONS: an activation is untrusted unless its provider marks it")]
    public void ActivationTrust()
    {
        var activation = new DesktopNotificationActivation("saved", "open", new Uri("https://example.org/"));
        Check(!activation.IsTrusted, "Activations are untrusted by default.");
        Check((activation with { IsTrusted = true }).IsTrusted && activation with { IsTrusted = true } != activation, "IsTrusted is part of the activation.");
    }

    [Test, DisplayName("INSTANCE: ClaimAsync with args forwards them as ApplicationLaunch.FromProcess does")]
    public async Task ClaimWithArgumentsAsync()
    {
        // These overloads use the per-user runtime directory. On Linux that is XDG_RUNTIME_DIR, which containers such as
        // the Alpine CI job do not set: use a private one, so the test neither depends on it nor touches the user's.
        string? runtime = null, previousRuntime = null, previousFlatpak = null;
        if (OperatingSystem.IsLinux())
        {
            runtime = Path.Combine(Path.GetTempPath(), "runic-xdg-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            (previousRuntime, previousFlatpak) = (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.GetEnvironmentVariable("FLATPAK_ID"));
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
            Environment.SetEnvironmentVariable("FLATPAK_ID", null);
        }
        try { await ClaimWithArgumentsCoreAsync(); }
        finally
        {
            if (runtime is not null)
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
                Environment.SetEnvironmentVariable("FLATPAK_ID", previousFlatpak);
                Directory.Delete(runtime, recursive: true);
            }
        }
    }

    private static async Task ClaimWithArgumentsCoreAsync()
    {
        string applicationId = "org.example.RunicTest" + Guid.NewGuid().ToString("N")[..12];
        var first = await ApplicationInstance.ClaimAsync(applicationId, ["--first"]);
        Check(first is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary }, "The first claim is the running instance: " + first);
        await using var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)first).Value).Instance;
        var launches = Channel.CreateUnbounded<ApplicationLaunch>();
        instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);

        var second = await ApplicationInstance.ClaimAsync(applicationId, ["--open", "report"]);
        Check(second is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "The second claim is forwarded: " + second);
        var received = await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Check(received.Arguments.SequenceEqual(["--open", "report"]) && received.WorkingDirectory == Environment.CurrentDirectory,
            "The arguments and the current directory arrive.");

        var forwardOnly = await ApplicationInstance.ClaimAsync(applicationId, ["--again"], new ApplicationInstanceOptions { ForwardOnly = true });
        Check(forwardOnly is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "The options overload forwards: " + forwardOnly);
        await launches.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5));

        // The overloads check their arguments before returning a task.
        Throws<ArgumentNullException>(() => ApplicationInstance.ClaimAsync(applicationId, (string[])null!).AsTask());
        Throws<ArgumentNullException>(() => ApplicationInstance.ClaimAsync(applicationId, [], (ApplicationInstanceOptions)null!).AsTask());
    }

    [Test, DisplayName("FILE: on Windows the staging file has the target's DACL from creation (SEC-28)")]
    public async Task WindowsStagingDescriptorAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), $"runic-acl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "private.txt");
            await File.WriteAllTextAsync(path, "private");
            // A private file in a folder that others can read: protected, only the current user.
            var target = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            target.SetAccessControl(security);

            await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
            {
                if (await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is not PlatformResult<IFileWriteTransaction>.Success { Value: var write })
                    throw new InvalidOperationException("Staging unavailable.");
                string staging = Directory.GetFiles(directory, ".runic-*.tmp").Single();
                var staged = new FileInfo(staging).GetAccessControl(AccessControlSections.Access);
                Check(staged.AreAccessRulesProtected, "The staging file does not inherit the folder's ACL.");
                Check(Rules(staged).SequenceEqual(Rules(target.GetAccessControl(AccessControlSections.Access))),
                    "The staging file has the target's rules: " + staged.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                await write.Content.WriteAsync("changed"u8.ToArray());
                Check(await write.CommitAsync() is FileCommitResult.Committed, "Commit failed.");
            }
            var replaced = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            Check(replaced.AreAccessRulesProtected && Rules(replaced).SequenceEqual(Rules(security)), "The committed file keeps the target's DACL.");
            Check(await File.ReadAllTextAsync(path) == "changed", "Commit did not replace the target.");

            // A new file keeps the folder's inherited ACL, as it will after the move.
            string created = Path.Combine(directory, "new.txt");
            await using (var save = await SaveFileLease.CreateAsync(created, new LocalAtomicFileReplacement()))
            {
                if (await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is not PlatformResult<IFileWriteTransaction>.Success)
                    throw new InvalidOperationException("Staging unavailable for a new file.");
                var staged = new FileInfo(Directory.GetFiles(directory, ".runic-*.tmp").Single()).GetAccessControl(AccessControlSections.Access);
                Check(!staged.AreAccessRulesProtected, "A new file's staging inherits from the folder.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static string[] Rules(FileSecurity security)
        {
            var rules = new List<string>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
                rules.Add($"{rule.IdentityReference.Value} {rule.AccessControlType} {(int)rule.FileSystemRights} {rule.IsInherited}");
            rules.Sort(StringComparer.Ordinal);
            return [.. rules];
        }
    }

    private sealed class RecordingThumbnails : IDesktopThumbnails
    {
        public List<int> Sizes { get; } = [];

        public ValueTask<PlatformResult<DesktopThumbnail?>> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
        {
            Sizes.Add(size);
            return ValueTask.FromResult<PlatformResult<DesktopThumbnail?>>(new PlatformResult<DesktopThumbnail?>.Success(null));
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(typeof(T).Name + " expected.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(typeof(T).Name + " expected.");
    }
}
