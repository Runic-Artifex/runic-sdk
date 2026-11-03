using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The platform fakes of Runic.Application.Testing follow the contracts of the real services: leases are single-use or
// write through one transaction at a time, commits detect other writers, and closing the window ends everything.
internal sealed class PlatformFakeTests
{
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Test]
    public async Task OpenDialogHandsOutAScriptedSingleUseReadLeaseAsync()
    {
        await using var platform = new RunicTestPlatform();
        var filter = new FileDialogFilter("Text", ["txt"]);
        platform.FileDialogs.EnqueueOpen(RunicTestFile.FromText("notes.txt", "Hello"), filter);
        var options = new OpenFileOptions { Title = "Import", Filters = [filter] };

        var result = await platform.FileDialogs.OpenFileAsync(options);

        Require(result is PickerResult<IReadFileLease>.Selected { SelectedFilter: var selected } && selected == filter, "The scripted filter was not reported.");
        if (!result.TryGetValue(out var lease)) throw new InvalidOperationException("The scripted file was not selected.");
        await using (lease)
        {
            Require(lease.DisplayName == "notes.txt", "The lease has the wrong name.");
            using var reader = new StreamReader(await lease.OpenReadAsync());
            Require(await reader.ReadToEndAsync() == "Hello", "The lease read the wrong content.");
            Require(await CatchAsync(() => lease.OpenReadAsync()) is InvalidOperationException, "A read lease was consumed twice.");
        }
        var call = platform.FileDialogs.Calls.Single();
        Require(call is { Kind: RunicFileDialogKind.OpenFile, Outcome: RunicTestOutcome.Selected, Scripted: true } && call.OpenOptions == options,
            "The dialog call was not recorded with its options.");
    }

    [Test]
    public async Task UnscriptedDialogsAreDismissedAndMismatchesThrowAsync()
    {
        await using var platform = new RunicTestPlatform();

        var dismissed = await platform.FileDialogs.OpenFileAsync(new OpenFileOptions());
        platform.FileDialogs.EnqueueSave("out.txt");
        var mismatch = await CatchAsync(() => platform.FileDialogs.OpenFileAsync(new OpenFileOptions()));
        platform.FileDialogs.EnqueueUnavailable(PlatformUnavailableReason.BackendUnavailable);
        var unavailable = await platform.FileDialogs.SaveFileAsync(new SaveFileOptions("out.txt"));
        var invalid = await CatchAsync(() => platform.FileDialogs.SaveFileAsync(new SaveFileOptions("a/b.txt")));

        Require(dismissed is PickerResult<IReadFileLease>.Dismissed, "An unscripted dialog was not dismissed.");
        Require(platform.FileDialogs.Calls[0] is { Scripted: false, Outcome: RunicTestOutcome.Dismissed }, "The unscripted dialog was not recorded.");
        Require(mismatch is InvalidOperationException { Message: var message } && message.Contains("save destination", StringComparison.Ordinal),
            "A save destination answered an open dialog.");
        Require(unavailable is PickerResult<ISaveFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "The scripted unavailability was not reported.");
        Require(invalid is ArgumentException, "A suggested path was not rejected as the real dialogs reject it.");
    }

    [Test]
    public async Task SaveLeaseCommitsAndDetectsAnotherWriterAsync()
    {
        await using var platform = new RunicTestPlatform();
        var destination = platform.FileDialogs.EnqueueSave("report.csv");
        (await platform.FileDialogs.SaveFileAsync(new SaveFileOptions("report.csv"))).TryGetValue(out var lease);
        await using var _ = lease!;

        var first = await WriteAsync(lease!, "a,b");
        Require(first is FileCommitResult.Committed && destination.ReadAllText() == "a,b", "The first write did not commit.");

        Require((await lease!.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)).TryGetValue(out var open), "A second write did not begin.");
        Require(await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Failed { Code: PlatformFailureCode.ResourceBusy },
            "Two transactions were open at once.");
        await open!.DisposeAsync();

        destination.WriteText("changed elsewhere");
        var conflict = await WriteAsync(lease, "c,d");
        Require(conflict is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict } && destination.ReadAllText() == "changed elsewhere",
            "A commit replaced another writer's content.");

        Require(destination.CommittedWrites.Count == 1, "The committed writes were not recorded.");
    }

    [Test]
    public async Task UncertainCommitAcceptsBothContentsAndCommitsOnceAsync()
    {
        await using var platform = new RunicTestPlatform();
        var destination = platform.FileDialogs.EnqueueSave(RunicTestFile.FromText("doc.txt", "v1"));
        (await platform.FileDialogs.SaveFileAsync(new SaveFileOptions("doc.txt"))).TryGetValue(out var lease);
        await using var _ = lease!;
        destination.EnqueueCommitResult(new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError));

        Require((await lease!.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)).TryGetValue(out var transaction), "The write did not begin.");
        await using (transaction)
        {
            await transaction!.Content.WriteAsync(Encoding.UTF8.GetBytes("v2"));
            Require(await transaction.CommitAsync() is FileCommitResult.CommitUnknown, "The scripted uncertain commit was not reported.");
            Require(await CatchAsync(() => transaction.CommitAsync()) is InvalidOperationException, "A commit was attempted twice.");
        }
        // The file may hold either content: another application writing v2 is indistinguishable, and is no conflict.
        destination.WriteText("v2");
        Require(await WriteAsync(lease, "v3") is FileCommitResult.Committed && destination.ReadAllText() == "v3",
            "The staged bytes of an uncertain commit did not count as the application's own.");
    }

    [Test]
    public async Task UncertainCommitCanBeScriptedAsLandedOrNotAsync()
    {
        foreach (var applied in new[] { true, false })
        {
            await using var platform = new RunicTestPlatform();
            var destination = platform.FileDialogs.EnqueueSave(RunicTestFile.FromText("doc.txt", "v1"));
            (await platform.FileDialogs.SaveFileAsync(new SaveFileOptions("doc.txt"))).TryGetValue(out var lease);
            await using var _ = lease!;
            destination.EnqueueCommitResult(new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError), applied);

            Require(await WriteAsync(lease!, "v2") is FileCommitResult.CommitUnknown, "The scripted uncertain commit was not reported.");
            Require(destination.ReadAllText() == (applied ? "v2" : "v1"), $"An uncertain commit with applied = {applied} left the wrong content.");
            Require(destination.CommittedWrites.Count == (applied ? 1 : 0), "A landed uncertain write was not listed with the committed writes.");
            // Either way the lease takes both contents as its own, so the next commit finds no conflict.
            Require(await WriteAsync(lease!, "v3") is FileCommitResult.Committed && destination.ReadAllText() == "v3",
                "The lease did not accept the content after an uncertain commit.");
        }
        var file = RunicTestFile.FromText("doc.txt", "v1");
        Require(Catch(() => file.EnqueueCommitResult(new FileCommitResult.Committed(), applied: false)) is ArgumentException
            && Catch(() => file.EnqueueCommitResult(new FileCommitResult.NotCommitted(PlatformFailureCode.IOError), applied: true)) is ArgumentException,
            "A contradictory scripted commit was accepted.");
    }

    [Test]
    public async Task DocumentReadsBecomeTheBaselineAsync()
    {
        await using var platform = new RunicTestPlatform();
        var file = platform.FileDialogs.EnqueueOpen("draft.md", "# One");
        (await platform.FileDialogs.OpenDocumentAsync(new OpenFileOptions())).TryGetValue(out var document);
        await using var _ = document!;

        file.WriteText("# Two");
        Require(await WriteAsync(document!, "# Mine") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }, "The external change was missed.");
        Require((await document!.OpenReadAsync()).TryGetValue(out var read), "The document did not open for reading.");
        await read!.DisposeAsync();
        Require(await WriteAsync(document, "# Mine") is FileCommitResult.Committed && file.ReadAllText() == "# Mine",
            "Reading the document did not make its content the baseline.");
        Require(document.LocalPath == file.LocalPath && Path.IsPathFullyQualified(document.LocalPath), "The document has no fully qualified path.");

        var created = platform.FileDialogs.EnqueueSave("new.md");
        (await platform.FileDialogs.SaveDocumentAsync(new SaveFileOptions("new.md"))).TryGetValue(out var saved);
        await using var __ = saved!;
        Require(await saved!.OpenReadAsync() is PlatformResult<Stream>.Failed { Code: PlatformFailureCode.NotFound } && !created.Exists,
            "A new document existed before its first write.");
    }

    [Test]
    public async Task ClosingTheWindowReleasesLeasesAndReportsOwnerClosedAsync()
    {
        var platform = new RunicTestPlatform();
        platform.FileDialogs.EnqueueOpenFiles(RunicTestFile.FromText("a.txt", "a"), RunicTestFile.FromText("b.txt", "b"));
        platform.FileDialogs.EnqueueOpenDirectory(Path.GetTempPath());
        (await platform.FileDialogs.OpenFilesAsync(new OpenFileOptions())).TryGetValue(out var files);
        (await platform.FileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions())).TryGetValue(out var directory);
        Require(files!.Count == 2 && files[1].DisplayName == "b.txt", "The multiple selection was not handed out in order.");
        var watch = platform.Clipboard.WatchAsync().GetAsyncEnumerator();
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: DesktopClipboardFormats.None },
            "The watch did not report the empty clipboard first.");

        await platform.DisposeAsync();

        Require(await CatchAsync(() => files[0].OpenReadAsync()) is ObjectDisposedException, "A read lease outlived its window.");
        Require(Catch(() => _ = directory!.LocalPath) is ObjectDisposedException, "A directory lease outlived its window.");
        Require(await platform.FileDialogs.OpenFileAsync(new OpenFileOptions()) is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A dialog opened for a closed window.");
        Require(await platform.MessageBoxes.ShowAsync(new DesktopMessageBox("Hi")) is PlatformResult<DesktopMessageBoxButton?>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A message box opened for a closed window.");
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }
            && !await watch.MoveNextAsync(), "The clipboard watch did not end with the window.");
        Require(platform.GetSnapshot().Statuses.Values.All(static status => status is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }),
            "The snapshot of a closed window reports capabilities.");
    }

    [Test]
    public async Task MessageBoxesAnswerAsScriptedAsync()
    {
        await using var platform = new RunicTestPlatform();
        var box = new DesktopMessageBox("Save changes?")
        {
            Buttons = [new("save", "Save") { Role = DesktopMessageBoxButtonRole.Default }, new("discard", "Don't Save"),
                new("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel }],
        };
        platform.MessageBoxes.EnqueueAnswer("discard");
        platform.MessageBoxes.EnqueueAnswer("missing");

        var answered = await platform.MessageBoxes.ShowAsync(box);
        var unknown = await CatchAsync(() => platform.MessageBoxes.ShowAsync(box));
        var escaped = await platform.MessageBoxes.ShowAsync(box);
        var invalid = await CatchAsync(() => platform.MessageBoxes.ShowAsync(new DesktopMessageBox(" ")));

        Require(answered.TryGetValue(out var button) && button?.Id == "discard", "The scripted button was not chosen.");
        Require(unknown is InvalidOperationException, "A button the box does not have was chosen.");
        Require(escaped.TryGetValue(out var cancel) && cancel?.Id == "cancel", "An unscripted box was not cancelled.");
        Require(invalid is ArgumentException, "A box without a message was shown.");
        Require(platform.MessageBoxes.Calls.Count == 2 && platform.MessageBoxes.Calls[0].MessageBox == box, "The boxes were not recorded.");
    }

    [Test]
    public async Task ContextMenusChooseEnabledItemsAsync()
    {
        await using var platform = new RunicTestPlatform();
        IWindowPlatformServices services = platform;
        ImmutableArray<DesktopMenuEntry> items =
        [
            new DesktopMenuItem("Open", "row.open") { CommandParameter = "7" },
            new DesktopMenu("More", [new DesktopMenuItem("Delete", "row.delete")]) { IsEnabled = false },
        ];
        platform.ContextMenus.EnqueueChoice("row.open", "7");
        platform.ContextMenus.EnqueueChoice("row.delete");

        var chosen = await services.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 10, Y = 20 });
        var disabled = await CatchAsync(() => services.ShowContextMenuAsync(items));
        var dismissed = await services.ShowContextMenuAsync(items);

        Require(chosen.TryGetValue(out var item) && item?.CommandParameter == "7", "The scripted item was not chosen.");
        Require(disabled is InvalidOperationException { Message: var message } && message.Contains("disabled", StringComparison.Ordinal),
            "An item of a disabled submenu was chosen.");
        Require(dismissed.TryGetValue(out var none) && none is null, "An unscripted menu was not dismissed.");
        Require(platform.ContextMenus.Calls[0].Options?.X == 10, "The menu's position was not recorded.");
        Require(await CatchAsync(() => services.ShowContextMenuAsync([])) is ArgumentException, "An empty menu was shown.");
    }

    [Test]
    public async Task ClipboardKeepsContentAndChecksWritesAsync()
    {
        await using var platform = new RunicTestPlatform();
        var clipboard = platform.Clipboard;
        platform.Clipboard.SetText("from another app");
        var watch = clipboard.WatchAsync().GetAsyncEnumerator();
        await watch.MoveNextAsync();

        var pasted = await clipboard.ReadTextAsync(100);
        var tooLong = await clipboard.ReadTextAsync(3);
        await clipboard.WriteAsync(new DesktopClipboardContent { Text = "copied", Image = s_png });
        var notPng = await clipboard.WriteAsync(new DesktopClipboardContent { Image = new byte[] { 1, 2, 3 } });
        var missing = await clipboard.WriteAsync(new DesktopClipboardContent { Files = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))] });

        Require(pasted.TryGetValue(out var text) && text == "from another app", "The clipboard's text was not read.");
        Require(tooLong is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "An overlong read was not refused.");
        Require(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success { Value: DesktopClipboardFormats.Text | DesktopClipboardFormats.Image },
            "The written formats were not reported.");
        Require((await clipboard.ReadImageAsync(1024)).TryGetValue(out var image) && image!.SequenceEqual(s_png), "The image was not kept.");
        Require(notPng is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }, "A non-PNG image was written.");
        Require(missing is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "A missing file was written.");
        Require(platform.Clipboard.Writes.Count == 1 && platform.Clipboard.Content?.Text == "copied", "The write was not recorded.");
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success { Value: DesktopClipboardFormats.Text | DesktopClipboardFormats.Image },
            "The watch did not report the application's write.");
        await watch.DisposeAsync();
    }

    [Test]
    public async Task NotificationsRecordAndActivateAsync()
    {
        await using var notifications = new RunicTestNotifications();
        DesktopNotificationActivation? activated = null;
        notifications.Activated += (_, activation) => activated = activation;
        var notification = new DesktopNotification("export-1", "Export done", "3 files") { Actions = [new("open", "Open")] };

        await notifications.ShowAsync(notification);
        var raised = notifications.Activate("export-1", "open");
        await notifications.RemoveAsync("export-1");

        Require(notifications.Shown.Single() == notification && notifications.Current.Count == 0 && notifications.Removed.Single() == "export-1",
            "The notification was not recorded.");
        Require(activated == raised && raised is { ActionId: "open", IsTrusted: true }, "The activation was not raised.");
        Require(Catch(() => notifications.Activate("export-1")) is InvalidOperationException, "A removed notification was activated as trusted.");
        notifications.UnavailableReason = PlatformUnavailableReason.ProviderNotConfigured;
        Require(await notifications.ShowAsync(notification) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
            "Unavailable notifications were shown.");
    }

    [Test]
    public async Task ServicesResolveTheScopesPlatformAndTheHostClosesItAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFileDialogs>(_ => throw new InvalidOperationException("The application's own registration was kept."));
        services.AddRunicTestPlatform();
        services.AddScoped<RootViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, RootViewModel, IDisposable>>(
            _ => (transport, content, vm) => new RootBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var host = RunicWindowTestHost.Create<RootViewModel>(scope.ServiceProvider);
        var platform = host.Platform;

        Require(platform is not null && ReferenceEquals(scope.ServiceProvider.GetRequiredService<IFileDialogs>(), platform.FileDialogs)
            && ReferenceEquals(scope.ServiceProvider.GetRequiredService<IWindowPlatformServices>(), platform)
            && ReferenceEquals(scope.ServiceProvider.GetRequiredService<IDesktopClipboard>(), platform.Clipboard)
            && ReferenceEquals(scope.ServiceProvider.GetRequiredService<IDesktopNotifications>(), provider.GetRequiredService<RunicTestNotifications>()),
            "The host's platform is not the one the scope's ViewModels take.");
        host.Dispose();
        Require(platform!.IsClosed, "Disposing the host did not close the window's platform services.");
    }

    [Test]
    public async Task ServeOnlyScriptCrossesTheProcessBoundaryAsync()
    {
        using var script = RunicServeOnlyTestPlatform.Create();
        var start = script.Configure(new System.Diagnostics.ProcessStartInfo("app"));
        var variables = new[] { "RUNIC_APPLICATION_SERVE_ONLY", RunicTestPlatformServiceCollectionExtensions.DirectoryEnvironmentVariable };
        var saved = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            // Without serve-only mode, the method registers nothing.
            Environment.SetEnvironmentVariable(variables[1], start.Environment[variables[1]]);
            Require(new ServiceCollection().AddRunicServeOnlyTestPlatform().Count == 0, "The test platform was registered outside serve-only mode.");
            Environment.SetEnvironmentVariable(variables[0], "1");
            await using var provider = new ServiceCollection().AddRunicServeOnlyTestPlatform().BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var dialogs = scope.ServiceProvider.GetRequiredService<IFileDialogs>();
            var boxes = scope.ServiceProvider.GetRequiredService<IDesktopMessageBoxes>();

            script.EnqueueOpenFile("in.txt", "hello");
            script.EnqueueSaveFile("out.txt");
            script.EnqueueMessageBoxAnswer("ok");
            (await dialogs.OpenFileAsync(new OpenFileOptions { Title = "Import" })).TryGetValue(out var read);
            await using (read!)
            {
                using var reader = new StreamReader(await read!.OpenReadAsync());
                Require(await reader.ReadToEndAsync() == "hello", "The scripted file's content did not arrive.");
            }
            (await dialogs.SaveFileAsync(new SaveFileOptions("out.txt"))).TryGetValue(out var save);
            await using (save!) await WriteAsync(save!, "result");
            await boxes.ShowAsync(new DesktopMessageBox("Done") { Buttons = [new("ok", "OK")] });

            var commit = await script.WaitForEventAsync(static e => e.Operation == "commit");
            var events = script.ReadEvents();
            Require(commit.GetText("content") == "result" && commit.Get("file") == "out.txt", "The committed bytes were not recorded.");
            Require(events[0] is { Service: "fileDialogs", Operation: "openFile", Sequence: 1 } && events[0].Get("title") == "Import",
                "The dialog was not recorded with its options.");
            Require(events.Any(static e => e is { Service: "messageBoxes" } && e.Get("button") == "ok"), "The message box answer was not recorded.");
            Require(ReferenceEquals(provider.GetRequiredService<RunicTestPlatform>(), scope.ServiceProvider.GetRequiredService<IWindowPlatformServices>()),
                "Serve-only windows do not share the scripted platform.");
        }
        finally
        {
            for (var i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], saved[i]);
        }
    }

    [Test]
    public async Task ClosingOneServeOnlyWindowLeavesTheSharedPlatformOpenAsync()
    {
        await WithServeOnlyEnvironmentAsync(async script =>
        {
            await using var provider = new ServiceCollection().AddRunicServeOnlyTestPlatform().BuildServiceProvider();
            var first = provider.CreateAsyncScope();
            await using var second = provider.CreateAsyncScope();
            var shared = first.ServiceProvider.GetRequiredService<IWindowPlatformServices>();
            _ = first.ServiceProvider.GetRequiredService<IDesktopFileLauncher>();
            _ = first.ServiceProvider.GetRequiredService<IFileDialogs>();
            await first.DisposeAsync();

            var platform = provider.GetRequiredService<RunicTestPlatform>();
            Require(ReferenceEquals(second.ServiceProvider.GetRequiredService<IWindowPlatformServices>(), shared) && !platform.IsClosed,
                "Closing one window closed the platform the other windows share.");
            var opened = await second.ServiceProvider.GetRequiredService<IFileDialogs>().OpenFileAsync(new OpenFileOptions());
            Require(opened is PickerResult<IReadFileLease>.Dismissed, $"The other window's dialog reported {opened} instead of a dismissal.");
        });
    }

    [Test]
    public async Task ServeOnlyPlatformRequiresTheBuildOptInAndAPrivateDirectoryAsync()
    {
        await WithServeOnlyEnvironmentAsync(script =>
        {
            Require(typeof(PlatformFakeTests).Assembly.IsDefined(typeof(RunicServeOnlyTestPlatformAttribute), inherit: false),
                "The test assembly does not opt in through its AssemblyAttribute item.");
            Require(Catch(() => RunicTestPlatformServiceCollectionExtensions.AddServeOnlyTestPlatform(new ServiceCollection(), typeof(string).Assembly))
                is InvalidOperationException { Message: var message } && message.Contains("RunicServeOnlyTestPlatform", StringComparison.Ordinal),
                "A build without the opt-in started the serve-only test platform.");
            Require(Catch(() => RunicTestPlatformServiceCollectionExtensions.AddServeOnlyTestPlatform(new ServiceCollection(), null))
                is InvalidOperationException, "A process without an entry assembly started the serve-only test platform.");
            if (!OperatingSystem.IsWindows())
            {
                Require((File.GetUnixFileMode(script.Directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherRead)) == 0,
                    "The script directory is open to other users.");
                File.SetUnixFileMode(script.Directory, File.GetUnixFileMode(script.Directory) | UnixFileMode.OtherWrite);
                Require(Catch(() => new ServiceCollection().AddRunicServeOnlyTestPlatform()) is InvalidOperationException { Message: var shared }
                    && shared.Contains("Other users", StringComparison.Ordinal), "A directory other users can write to was accepted.");
            }
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task MalformedScriptLinesAreRecordedNotThrownAsync()
    {
        await WithServeOnlyEnvironmentAsync(async script =>
        {
            await using var provider = new ServiceCollection().AddRunicServeOnlyTestPlatform().BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var dialogs = scope.ServiceProvider.GetRequiredService<IFileDialogs>();
            await File.AppendAllTextAsync(Path.Combine(script.Directory, "script.jsonl"),
                "{not json\n{\"service\":\"printer\",\"operation\":\"print\"}\n");
            script.EnqueueOpenFile("in.txt", "hello");

            var opened = await dialogs.OpenFileAsync(new OpenFileOptions());
            Require(opened.TryGetValue(out var read), $"The valid line after the malformed ones was not applied: {opened}.");
            await read!.DisposeAsync();
            var errors = script.ReadEvents().Where(static e => e is { Service: "script", Operation: "error" }).ToArray();
            Require(errors.Length == 2 && errors[0].Get("line") == "{not json" && errors[1].Get("message")!.Contains("printer", StringComparison.Ordinal),
                "The malformed script lines were not recorded as script errors.");
            Require(await CatchAsync(() => new ValueTask<RunicTestPlatformEvent>(script.WaitForEventAsync(static e => e.Operation == "never")))
                is InvalidOperationException { Message: var message } && message.Contains("rejected", StringComparison.Ordinal),
                "WaitForEventAsync did not report the script error.");
        });
    }

    private static async Task WithServeOnlyEnvironmentAsync(Func<RunicServeOnlyTestPlatform, Task> test)
    {
        using var script = RunicServeOnlyTestPlatform.Create();
        var variables = new[] { "RUNIC_APPLICATION_SERVE_ONLY", RunicTestPlatformServiceCollectionExtensions.DirectoryEnvironmentVariable };
        var saved = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "1");
            Environment.SetEnvironmentVariable(variables[1], script.Directory);
            await test(script);
        }
        finally
        {
            for (var i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], saved[i]);
        }
    }

    private static async Task<FileCommitResult> WriteAsync(ISaveFileLease lease, string text)
    {
        Require((await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)).TryGetValue(out var transaction), "The write did not begin.");
        await using (transaction)
        {
            await transaction!.Content.WriteAsync(Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }

    private static async Task<Exception?> CatchAsync<T>(Func<ValueTask<T>> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
