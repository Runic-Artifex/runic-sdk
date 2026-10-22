using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

internal sealed class ConformanceTests
{
    [Test, DisplayName("RESULT: failures carry optional native diagnostics")]
    public Task NativeDiagnostics()
    {
        var hresult = PlatformDiagnostic.FromHResult(unchecked((int)0x80070005));
        if (hresult.Domain != "HRESULT" || hresult.Code != 0x80070005L || !hresult.ToString().StartsWith("HRESULT 0x80070005", StringComparison.Ordinal))
            throw new InvalidOperationException($"HRESULT diagnostic lost its unsigned code: {hresult}");
        var errno = PlatformDiagnostic.FromErrno(13);
        if (errno is not { Domain: "errno", Code: 13 } || OperatingSystem.IsWindows() != (errno.Message is null))
            throw new InvalidOperationException($"errno diagnostic lost its code or described a foreign code: {errno}");
        if (OperatingSystem.IsWindows() != (hresult.Message is not null) || OperatingSystem.IsWindows() != (PlatformDiagnostic.FromWin32Error(5).Message is not null))
            throw new InvalidOperationException("A Windows code was described on another platform, or not on Windows.");
        if (new PlatformDiagnostic("IOReturn", 0xE00002C2L).ToString() != "IOReturn 0xE00002C2")
            throw new InvalidOperationException("An IOReturn code was not formatted as hexadecimal.");
        if (new PlatformDiagnostic("org.freedesktop.portal.Error.NotAllowed", 0).ToString() != "org.freedesktop.portal.Error.NotAllowed 0")
            throw new InvalidOperationException("A named diagnostic without a message formatted unexpectedly.");
        if (new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError).Diagnostic is not null)
            throw new InvalidOperationException("A failure without native detail reported a diagnostic.");
        return Task.CompletedTask;
    }

    // Positional patterns name the selected lease and the successful value without a cast.
    [Test, DisplayName("RESULT: picker and platform results deconstruct")]
    public Task ResultsDeconstruct()
    {
        var lease = new ControlledLease();
        PickerResult<IReadFileLease> picked = new PickerResult<IReadFileLease>.Selected(lease);
        if (picked is not PickerResult<IReadFileLease>.Selected(var selected) || !ReferenceEquals(selected, lease))
            throw new InvalidOperationException("A selected picker result did not deconstruct to its lease.");
        PlatformResult<string?> read = new PlatformResult<string?>.Success("text");
        if (read is not PlatformResult<string?>.Success(var text) || text != "text")
            throw new InvalidOperationException("A successful platform result did not deconstruct to its value.");
        return Task.CompletedTask;
    }

    [Test, DisplayName("WINDOW: hosts without an owner report every window service unavailable")]
    public async Task UnavailableWindowServices()
    {
        var services = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        var snapshot = services.GetSnapshot();
        if (snapshot.Generation != Guid.Empty || !snapshot.Statuses.Keys.Order().SequenceEqual(WindowPlatformServices.CapabilityIds.Order())
            || snapshot.Statuses.Values.Any(static status => status is not CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)))
            throw new InvalidOperationException("Unavailable window services reported another capability status.");
        if (await services.FileDialogs.OpenFileAsync(new()) is not PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await services.FileDialogs.SaveFileAsync(new("notes.txt")) is not PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await services.FileDialogs.OpenDirectoryAsync(new()) is not PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await services.Clipboard.ReadTextAsync(16) is not PlatformResult<string?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await services.Clipboard.WriteTextAsync("x") is not PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await services.FileLauncher.LaunchAsync("/tmp/notes.txt") is not PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable))
            throw new InvalidOperationException("Unavailable window services reported another outcome.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await services.FileDialogs.OpenFileAsync(new(), cancelled.Token);
            throw new InvalidOperationException("Unavailable window services ignored caller cancellation.");
        }
        catch (OperationCanceledException) { }
        try
        {
            _ = WindowPlatformServices.Unavailable((PlatformUnavailableReason)99);
            throw new InvalidOperationException("An undefined unavailability reason was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    // An implementation of IWindowPlatformServices written before AnnounceAsync keeps compiling and reports the owner
    // unavailable; WindowPlatformServices.Unavailable reports its own reason. Both check their arguments.
    [Test, DisplayName("WINDOW: announcements without an implementation report why")]
    public async Task AnnouncementsWithoutAnImplementation()
    {
        IWindowPlatformServices custom = new MinimalWindowServices();
        if (await custom.AnnounceAsync("Saved") is not PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await custom.AnnounceAsync("Failed", PlatformAnnouncementPriority.Assertive) is not PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable))
            throw new InvalidOperationException("The default AnnounceAsync did not report the owner unavailable.");
        foreach (var reason in new[] { PlatformUnavailableReason.OwnerClosed, PlatformUnavailableReason.OwnerUnavailable })
        {
            if (await WindowPlatformServices.Unavailable(reason).AnnounceAsync("Saved") is not PlatformResult<PlatformUnit>.Unavailable(var reported) || reported != reason)
                throw new InvalidOperationException($"Unavailable window services did not announce {reason}.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var services in new[] { custom, WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed) })
        {
            await Rejects<ArgumentNullException>(() => services.AnnounceAsync(null!));
            await Rejects<ArgumentException>(() => services.AnnounceAsync(" "));
            await Rejects<ArgumentOutOfRangeException>(() => services.AnnounceAsync("Saved", (PlatformAnnouncementPriority)2));
            await Rejects<OperationCanceledException>(() => services.AnnounceAsync("Saved", cancellationToken: cancelled.Token));
        }

        static async Task Rejects<TException>(Func<ValueTask<PlatformResult<PlatformUnit>>> announce) where TException : Exception
        {
            try { await announce(); }
            catch (TException) { return; }
            throw new InvalidOperationException($"AnnounceAsync did not throw {typeof(TException).Name}.");
        }
    }

    // An implementation written before ShowContextMenuAsync keeps compiling: it checks the arguments and reports the
    // owner unavailable, as WindowPlatformServices.Unavailable reports its own reason.
    [Test, DisplayName("WINDOW: context menus without an implementation report why")]
    public async Task ContextMenusWithoutAnImplementation()
    {
        System.Collections.Immutable.ImmutableArray<DesktopMenuEntry> menu =
            [new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7" }, new DesktopMenuSeparator(),
             new DesktopMenu("More", [new DesktopMenuItem("Rename", "row.rename")])];
        IWindowPlatformServices custom = new MinimalWindowServices();
        if (await custom.ShowContextMenuAsync(menu) is not PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            || await custom.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { X = 10, Y = 20 }) is not PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable))
            throw new InvalidOperationException("The default ShowContextMenuAsync did not report the owner unavailable.");
        if (await WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed).ShowContextMenuAsync(menu)
            is not PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed))
            throw new InvalidOperationException("Unavailable window services did not report their reason for a context menu.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var deep = (DesktopMenuEntry)new DesktopMenuItem("Leaf", "leaf");
        for (var depth = 0; depth < 6; depth++) deep = new DesktopMenu("Level", [deep]);
        foreach (var services in new[] { custom, WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed) })
        {
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync([]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync(default));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync([new DesktopMenuItem("", "x")]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync([new DesktopMenuItem("Pick", "x") { RadioGroup = "g" }]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync([new DesktopMenuItem("Pick", "x") { CommandParameter = "a\0b" }]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync([deep]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync(
                [.. Enumerable.Range(0, 257).Select(static index => (DesktopMenuEntry)new DesktopMenuItem("Item", "item"))]));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { X = 1 }));
            await Rejects<ArgumentException>(() => services.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { X = double.NaN, Y = 1 }));
            await Rejects<OperationCanceledException>(() => services.ShowContextMenuAsync(menu, cancellationToken: cancelled.Token));
        }

        static async Task Rejects<TException>(Func<ValueTask<PlatformResult<DesktopMenuItem?>>> show) where TException : Exception
        {
            try { await show(); }
            catch (TException) { return; }
            throw new InvalidOperationException($"ShowContextMenuAsync did not throw {typeof(TException).Name}.");
        }
    }

    private sealed class MinimalWindowServices : IWindowPlatformServices
    {
        private readonly IWindowPlatformServices _inner = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        public IFileDialogs FileDialogs => _inner.FileDialogs;
        public ITextClipboard Clipboard => _inner.Clipboard;
        public IDesktopFileLauncher FileLauncher => _inner.FileLauncher;
        public CapabilitySnapshot GetSnapshot() => _inner.GetSnapshot();
    }

    [Test, DisplayName("PICK: native owner closes after admission before dispatch")]
    public async Task NativeOwnerDispatchRace()
    {
        foreach (bool save in new[] { false, true })
        {
            var owner = new ClosingNativeOwner();
            await using var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            var files = new PresentationFiles(lifetime, new NativePickerBackend(owner, new DispatchingPicker(owner)));
            var openTask = save ? null : files.OpenFileAsync(new()).AsTask();
            var saveTask = save ? files.SaveFileAsync(new("document.txt")).AsTask() : null;
            await owner.Queued.Task;
            owner.Close();
            if (save) Check(await saveTask! is PickerResult<ISaveFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
            else Check(await openTask! is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
            Check(lifetime.GetResourceSnapshot().PendingOperationCount == 0);
        }

        // An arbitrary disposed resource is a provider defect, not an owner-close outcome.
        var validOwner = new ClosingNativeOwner();
        var defectiveBackend = new NativePickerBackend(validOwner, new DisposedResourcePicker());
        await Throws<ObjectDisposedException>(() => defectiveBackend.OpenFileAsync(new()).AsTask());
        await Throws<ObjectDisposedException>(() => defectiveBackend.SaveFileAsync(new("document.txt")).AsTask());
    }

    private sealed class ClosingNativeOwner : INativeWindowOwner
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => !_closed.Task.IsCompleted;
        internal void Close() => _closed.TrySetResult();
        public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queued.TrySetResult();
            await _closed.Task;
            throw new OwnerClosedException();
        }
    }

    private sealed class DispatchingPicker(INativeWindowOwner owner) : INativeFilePicker
    {
        public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
        {
            await owner.InvokeAsync(_ => { }, cancellationToken);
            return null;
        }
    }

    private sealed class DisposedResourcePicker : INativeFilePicker
    {
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
            => ValueTask.FromException<IReadOnlyList<NativeFileSelection>?>(new ObjectDisposedException("provider-resource"));
    }

    [Test, DisplayName("CLIPBOARD: outcomes, cancellation, retry and shutdown drain")]
    public async Task ClipboardConformance()
    {
        var backend = new ControlledClipboard();
        var lifetime = Owned();
        var clipboard = new PresentationClipboard(lifetime, backend);
        Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: null });
        backend.Text = "";
        Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: "" });
        backend.Text = "oversized";
        Check(await clipboard.ReadTextAsync(2) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge });
        backend.Failure = PlatformFailureCode.PermissionDenied;
        Check(await clipboard.ReadTextAsync(20) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.PermissionDenied });
        backend.Failure = PlatformFailureCode.ResourceBusy;
        Check(await clipboard.WriteTextAsync("busy") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy });
        backend.Failure = null;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => clipboard.WriteTextAsync("canceled", canceled.Token).AsTask());
        Check(await clipboard.WriteTextAsync("retry") is PlatformResult<PlatformUnit>.Success);
        backend.BlockWrite = true;
        using var late = new CancellationTokenSource();
        var write = clipboard.WriteTextAsync("actual", late.Token).AsTask();
        await backend.Started.Task;
        late.Cancel();
        var stopped = lifetime.DisposeAsync().AsTask();
        Check(!stopped.IsCompleted);
        Check(!backend.Disposed);
        backend.Finish.TrySetResult();
        Check(await write is PlatformResult<PlatformUnit>.Success);
        await stopped;
        Check(backend.Text == "actual" && backend.Disposed);
        Check(lifetime.GetResourceSnapshot() == (0, 0));
        Check(await clipboard.ReadTextAsync(20) is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
    }

    private sealed class ControlledClipboard : ITextClipboard, IAsyncDisposable
    {
        internal string? Text;
        internal PlatformFailureCode? Failure;
        internal bool BlockWrite;
        internal bool Disposed;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<string?>>(Failure is { } failure
                ? new PlatformResult<string?>.Failed(failure) : new PlatformResult<string?>.Success(Text));
        }
        public async ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure) return new PlatformResult<PlatformUnit>.Failed(failure);
            if (BlockWrite) { Started.TrySetResult(); await Finish.Task; }
            Text = text;
            return new PlatformResult<PlatformUnit>.Success(new());
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Test, DisplayName("CAP-01/02/03/04: readiness, immutable snapshots and explicit ownership")]
    public async Task Capabilities()
    {
        await using var lifetime = new PresentationLifetime();
        var absent = new PresentationFiles(lifetime);
        Check(await absent.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured });
        var backend = new ControlledBackend();
        var files = new PresentationFiles(lifetime, backend);
        var before = files.GetSnapshot();
        Check(before.Statuses["platform.files.open"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable });
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable });
        await Throws<ArgumentOutOfRangeException>(() => files.OpenFileAsync(new((OwnerPolicy)1)).AsTask());
        Check(files.GetSnapshot().Statuses["platform.dialogs.owned"] is CapabilityStatus.Unavailable);
        lifetime.AttachTestOwner();
        Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Available);
        Check(files.GetSnapshot().Statuses["platform.files.save"] is CapabilityStatus.Available);
        backend.SupportsAtomicReplace = false;
        Check(files.GetSnapshot().Statuses["platform.files.save"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.AtomicReplaceUnavailable });
        Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Available);
        backend.SupportsAtomicReplace = true;
        Check(before.Statuses["platform.files.open"] is CapabilityStatus.Unavailable);
        backend.IsAvailable = false;
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable });
        Check(backend.OpenCalls == 0);
        await lifetime.DisposeAsync();
        Check(files.GetSnapshot().Generation == before.Generation);
        Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
    }

    [Test, DisplayName("PICK-01/02/04: dismissal, pre-cancellation and one picker per owner")]
    public async Task PickerAdmission()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => files.OpenFileAsync(new(), cancelled.Token).AsTask());
        Check(backend.OpenCalls == 0);
        var first = files.OpenFileAsync(new()).AsTask();
        var anotherFacade = new PresentationFiles(owner, backend);
        Check(await anotherFacade.SaveFileAsync(new("contact.json")) is PickerResult<ISaveFileLease>.Failed { Code: PlatformFailureCode.ResourceBusy });
        Check(backend.SaveCalls == 0);
        await using var independent = Owned();
        var otherBackend = new ControlledBackend();
        var other = new PresentationFiles(independent, otherBackend).OpenFileAsync(new()).AsTask();
        Check(otherBackend.OpenCalls == 1);
        otherBackend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Dismissed());
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Dismissed());
        Check(await first is PickerResult<IReadFileLease>.Dismissed);
        Check(await other is PickerResult<IReadFileLease>.Dismissed);
        var save = files.SaveFileAsync(new("contact.json")).AsTask();
        backend.SaveResult.SetResult(new PickerResult<ISaveFileLease>.Dismissed());
        Check(await save is PickerResult<ISaveFileLease>.Dismissed);
    }

    [Test, DisplayName("PICK-03: late selection after caller cancellation releases access")]
    public async Task LateCancellation()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        using var cancellation = new CancellationTokenSource();
        var pending = files.OpenFileAsync(new(), cancellation.Token).AsTask();
        cancellation.Cancel();
        Check(backend.Token.IsCancellationRequested);
        var lease = new ControlledLease();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await lease.Disposing.Task;
        Check(!pending.IsCompleted);
        lease.Release.SetResult();
        await Throws<OperationCanceledException>(() => pending);
        Check(lease.Disposals == 1);
    }

    [Test, DisplayName("PICK-05: owner shutdown waits for late access release")]
    public async Task OwnerShutdown()
    {
        var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        var snapshot = files.GetSnapshot();
        var pending = files.OpenFileAsync(new()).AsTask();
        var closed = owner.DisposeAsync().AsTask();
        Check(!closed.IsCompleted);
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
        await using var replacement = Owned();
        Check(snapshot.Generation != replacement.Generation);
        Check(snapshot.Statuses["platform.files.open"] is CapabilityStatus.Available);
        var lease = new ControlledLease();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await lease.Disposing.Task;
        Check(!closed.IsCompleted);
        lease.Release.SetResult();
        Check(await pending is PickerResult<IReadFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
        await closed;
        await owner.DisposeAsync();
        Check(lease.Disposals == 1 && backend.OpenCalls == 1);
    }

    [Test, DisplayName("PICK-05: failed access release remains a shutdown failure")]
    public async Task CleanupFailure()
    {
        var owner = Owned();
        var backend = new ControlledBackend();
        var pending = new PresentationFiles(owner, backend).OpenFileAsync(new()).AsTask();
        var closing = owner.DisposeAsync().AsTask();
        var lease = new ControlledLease { FailRelease = true };
        lease.Release.SetResult();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await Throws<IOException>(() => pending);
        await Throws<IOException>(() => closing);
        await Throws<IOException>(() => owner.DisposeAsync().AsTask());
        Check(lease.Disposals == 1);
    }

    [Test, DisplayName("PICK: provider failure releases admission for the next request")]
    public async Task ProviderFailure()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        backend.OpenResult.SetException(new IOException("injected provider bug"));
        await Throws<IOException>(() => files.OpenFileAsync(new()).AsTask());
        backend.SaveResult.SetResult(new PickerResult<ISaveFileLease>.Failed(PlatformFailureCode.PermissionDenied));
        Check(await files.SaveFileAsync(new("contact.json")) is PickerResult<ISaveFileLease>.Failed { Code: PlatformFailureCode.PermissionDenied });
        Check(backend.SaveCalls == 1);
        await Throws<ArgumentException>(() => files.SaveFileAsync(new("../contact.json")).AsTask());
        Check(backend.SaveCalls == 1);
    }

    private static PresentationLifetime Owned() { var owner = new PresentationLifetime(); owner.AttachTestOwner(); return owner; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Conformance assertion failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class ControlledBackend : IPickerBackend
    {
        public bool IsAvailable { get; set; } = true;
        public bool SupportsAtomicReplace { get; set; } = true;
        internal int OpenCalls, SaveCalls;
        internal CancellationToken Token;
        internal TaskCompletionSource<PickerResult<IReadFileLease>> OpenResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PickerResult<ISaveFileLease>> SaveResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        { OpenCalls++; Token = cancellationToken; return new(OpenResult.Task); }
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
        { SaveCalls++; Token = cancellationToken; return new(SaveResult.Task); }
    }

    private sealed class ControlledLease : IReadFileLease
    {
        internal int Disposals;
        internal bool FailRelease;
        internal TaskCompletionSource Disposing { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public string DisplayName => "contact.json";
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("A cancelled selection must never be read.");
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); Disposing.TrySetResult(); await Release.Task;
            if (FailRelease) throw new IOException("injected access-release failure");
        }
    }
}
