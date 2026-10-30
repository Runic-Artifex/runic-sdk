using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// IDesktopClipboard and the window's badge and progress without a native provider: the content and argument rules, the
// presentation facades over fake providers, and the watch semantics every provider shares.
internal sealed class ClipboardAndTaskbarTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Test, DisplayName("CLIPBOARD-DATA: content rules throw for shape and report data the write cannot take")]
    public async Task ContentRules()
    {
        var unavailable = (IDesktopClipboard)WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).Clipboard;
        await Throws<ArgumentNullException>(() => unavailable.WriteAsync(null!).AsTask());
        await Throws<ArgumentException>(() => unavailable.WriteAsync(new DesktopClipboardContent()).AsTask());
        await Throws<ArgumentException>(() => unavailable.WriteAsync(new() { Files = ["relative/file.txt"] }).AsTask());
        await Throws<ArgumentException>(() => unavailable.WriteAsync(new() { Files = [""] }).AsTask());
        string root = Path.GetTempPath();
        await Throws<ArgumentException>(() => unavailable.WriteAsync(new() { Files = [.. Enumerable.Repeat(Path.Combine(root, "x"), 4097)] }).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => unavailable.ReadHtmlAsync(-1).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => unavailable.ReadRtfAsync(-1).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => unavailable.ReadImageAsync(-1).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => unavailable.ReadFilesAsync(-1).AsTask());
        Check(await unavailable.WriteAsync(new() { Text = "" }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "Empty text is content.");
        Check(await unavailable.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "Formats.");
        var watched = new List<PlatformResult<DesktopClipboardFormats>>();
        await foreach (var formats in unavailable.WatchAsync()) watched.Add(formats);
        Check(watched is [PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)],
            $"The unavailable watch reports once and ends: {string.Join(", ", watched)}");

        Check(DesktopClipboardRules.Formats(new() { Text = "a", Html = "<b>a</b>", Rtf = "{\\rtf1 a}", Image = Png, Files = [root] })
            == (DesktopClipboardFormats.Text | DesktopClipboardFormats.Html | DesktopClipboardFormats.Rtf | DesktopClipboardFormats.Image | DesktopClipboardFormats.Files),
            "Every format counts.");
        Check(DesktopClipboardRules.Validate(new() { Text = "a\0b" }) == PlatformFailureCode.InvalidData, "NUL in text.");
        Check(DesktopClipboardRules.Validate(new() { Html = "a\0" }) == PlatformFailureCode.InvalidData, "NUL in HTML.");
        Check(DesktopClipboardRules.Validate(new() { Rtf = "{\\rtf1 €}" }) == PlatformFailureCode.InvalidData, "RTF above U+00FF.");
        Check(DesktopClipboardRules.Validate(new() { Rtf = "{\\rtf1 café}" }) is null, "Latin-1 RTF.");
        Check(DesktopClipboardRules.Validate(new() { Image = new byte[] { 1, 2, 3 } }) == PlatformFailureCode.InvalidData, "An image that is not PNG.");
        Check(DesktopClipboardRules.Validate(new() { Image = Png }) is null, "A PNG.");
        Check(DesktopClipboardRules.Validate(new() { Files = [Path.Combine(root, Guid.NewGuid().ToString("N"))] }) == PlatformFailureCode.NotFound, "A missing file.");
        Check(DesktopClipboardRules.Validate(new() { Files = [root] }) is null, "A directory is a file entry.");
        Check(DesktopClipboardRules.RtfText([0x7B, 0xE9, 0x00, 0x41]) == "{é", "RTF ends at NUL and keeps Latin-1.");
    }

    [Test, DisplayName("TASKBAR: badge and progress arguments, labels and capability ids")]
    public async Task TaskbarRules()
    {
        var services = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed);
        await Throws<ArgumentException>(() => services.SetBadgeAsync(new() { Count = 0 }).AsTask());
        await Throws<ArgumentException>(() => services.SetBadgeAsync(new() { Description = "a\0" }).AsTask());
        foreach (var value in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
            await Throws<ArgumentException>(() => services.SetProgressAsync(new(value)).AsTask());
        await Throws<ArgumentException>(() => services.SetProgressAsync(new(0.5) { State = (DesktopProgressState)7 }).AsTask());
        Check(await services.SetBadgeAsync(null) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed), "Clearing.");
        Check(await services.SetProgressAsync(new(null)) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed), "Indeterminate.");
        // The default members of a custom implementation check the arguments too and report the owner unavailable.
        IWindowPlatformServices custom = new CustomServices();
        Check(await custom.SetBadgeAsync(new() { Count = 3 }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "Default badge.");
        Check(await custom.SetProgressAsync(new(1)) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "Default progress.");
        await Throws<ArgumentException>(() => custom.SetBadgeAsync(new() { Count = -1 }).AsTask());

        Check(DesktopTaskbarRules.Label(new()) == "" && DesktopTaskbarRules.Label(new() { Count = 7 }) == "7"
            && DesktopTaskbarRules.Label(new() { Count = 99 }) == "99" && DesktopTaskbarRules.Label(new() { Count = 100 }) == "99+", "Badge labels.");
        string[] ids = [PlatformCapabilityIds.ClipboardReadData, PlatformCapabilityIds.ClipboardWriteData, PlatformCapabilityIds.ClipboardWatch,
            PlatformCapabilityIds.TaskbarBadge, PlatformCapabilityIds.TaskbarProgress];
        Check(ids.SequenceEqual(["platform.clipboard.readData", "platform.clipboard.writeData", "platform.clipboard.watch", "platform.taskbar.badge", "platform.taskbar.progress"]),
            "The capability ids.");
        Check(ids.All(WindowPlatformServices.CapabilityIds.Contains) && ids.All(services.GetSnapshot().Statuses.ContainsKey), "Every window reports them.");
    }

    [Test, DisplayName("CLIPBOARD-DATA: the facade forwards, bounds, validates and drains")]
    public async Task PresentationClipboardForwards()
    {
        var backend = new FakeClipboard();
        var lifetime = Owned();
        var clipboard = new PresentationClipboard(lifetime, backend);
        Check(clipboard.GetSnapshot().Statuses[PlatformCapabilityIds.ClipboardWriteData] is CapabilityStatus.Available, "Data capability.");
        backend.Html = "<b>bold</b>";
        Check(await clipboard.ReadHtmlAsync(11) is PlatformResult<string?>.Success { Value: "<b>bold</b>" }, "HTML.");
        // A provider that ignores the bound is held to it.
        Check(await clipboard.ReadHtmlAsync(3) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "HTML bound.");
        backend.Image = Png;
        Check(await clipboard.ReadImageAsync(Png.Length - 1) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.TooLarge }, "Image bound.");
        backend.Files = ["/a", "/b"];
        Check(await clipboard.ReadFilesAsync(1) is PlatformResult<ImmutableArray<string>>.Failed { Code: PlatformFailureCode.TooLarge }, "File bound.");
        Check(await clipboard.ReadFilesAsync(2) is PlatformResult<ImmutableArray<string>>.Success { Value.Length: 2 }, "Files.");
        Check(await clipboard.ReadRtfAsync(10) is PlatformResult<string?>.Success { Value: null }, "No RTF.");
        Check(await clipboard.WriteAsync(new() { Text = "a\0" }) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && backend.Writes == 0, "Invalid content never reaches the provider.");
        Check(await clipboard.WriteAsync(new() { Html = "<i>x</i>", Text = "x" }) is PlatformResult<PlatformUnit>.Success && backend.Writes == 1
            && backend.Formats == (DesktopClipboardFormats.Html | DesktopClipboardFormats.Text), "A write.");
        Check(await clipboard.GetFormatsAsync() is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Html | DesktopClipboardFormats.Text), "Formats.");

        // The watch reports the provider's formats; stopping the presentation reports the owner closed and ends it.
        await using var watch = clipboard.WatchAsync().GetAsyncEnumerator();
        Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success, "First watch item.");
        var next = watch.MoveNextAsync().AsTask();
        await lifetime.DisposeAsync();
        Check(await next && watch.Current is PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed), $"Closed watch: {watch.Current}");
        Check(!await watch.MoveNextAsync(), "The closed watch ends.");
        Check(backend.WatchDisposed, "The provider's watch ends with it.");
        Check(await clipboard.ReadImageAsync(10) is PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed), "Closed.");

        // A text-only provider has no data formats.
        var text = new PresentationClipboard(Owned(), new TextOnlyClipboard());
        Check(text.GetSnapshot().Statuses[PlatformCapabilityIds.ClipboardReadText] is CapabilityStatus.Available
            && text.GetSnapshot().Statuses[PlatformCapabilityIds.ClipboardWatch] is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "Text-only capabilities.");
        Check(await text.ReadImageAsync(10) is PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "Text-only image.");
        Check(await text.WriteAsync(new() { Text = "x" }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "Text-only write.");
        Check(await text.ReadTextAsync(10) is PlatformResult<string?>.Success { Value: "text" }, "Text still reads.");
        Check(new PresentationClipboard(Owned()).GetSnapshot().Statuses[PlatformCapabilityIds.ClipboardReadData]
            is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "No provider.");
    }

    [Test, DisplayName("CLIPBOARD-WATCH: formats now, again after each change, collapsed, never content")]
    public async Task WatchSemantics()
    {
        var formats = Channel.CreateUnbounded<PlatformResult<DesktopClipboardFormats>>();
        Action? signal = null;
        bool disposed = false;
        int reads = 0;
        ValueTask<PlatformResult<DesktopClipboardFormats>> Read(CancellationToken token)
        {
            reads++;
            return formats.Reader.ReadAsync(token);
        }
        ValueTask<IAsyncDisposable?> Subscribe(Action changed, CancellationToken _)
        {
            signal = changed;
            return ValueTask.FromResult<IAsyncDisposable?>(new Disposal(() => disposed = true));
        }
        using var cancellation = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        await using var watch = ClipboardWatch.WatchAsync(Read, Subscribe, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        formats.Writer.TryWrite(Success(DesktopClipboardFormats.Text));
        Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Text), "Formats now.");
        Check(signal is not null, "Subscribed before the first read.");
        // Two changes while nothing reads collapse into one read; the same formats are reported again.
        signal!();
        signal();
        formats.Writer.TryWrite(Success(DesktopClipboardFormats.Text));
        Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Success(DesktopClipboardFormats.Text), "A change with the same formats.");
        var pending = watch.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Check(!pending.IsCompleted && reads == 2, $"Collapsed changes: {reads} reads.");
        signal();
        formats.Writer.TryWrite(new PlatformResult<DesktopClipboardFormats>.Failed(PlatformFailureCode.ResourceBusy));
        Check(await pending && watch.Current is PlatformResult<DesktopClipboardFormats>.Failed, "A failed read continues.");
        signal();
        formats.Writer.TryWrite(new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed));
        Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopClipboardFormats>.Unavailable, "Unavailable.");
        Check(!await watch.MoveNextAsync() && disposed, "Unavailable ends the watch and its subscription.");

        var none = new List<PlatformResult<DesktopClipboardFormats>>();
        await foreach (var item in ClipboardWatch.WatchAsync(Read, static (_, _) => ValueTask.FromResult<IAsyncDisposable?>(null))) none.Add(item);
        Check(none is [PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable)], "Without a listener.");

        // Cancellation ends the watch with OperationCanceledException and disposes the subscription.
        disposed = false;
        using var stop = new CancellationTokenSource();
        await using var cancelled = ClipboardWatch.WatchAsync(Read, Subscribe, stop.Token).GetAsyncEnumerator(stop.Token);
        formats.Writer.TryWrite(Success(DesktopClipboardFormats.None));
        Check(await cancelled.MoveNextAsync(), "Cancelled watch's first item.");
        var waiting = cancelled.MoveNextAsync().AsTask();
        await stop.CancelAsync();
        await Throws<OperationCanceledException>(() => waiting);
        await cancelled.DisposeAsync();
        Check(disposed, "Cancellation disposes the subscription.");
    }

    [Test, DisplayName("TASKBAR: the facade forwards, reports capabilities and removes the badge at shutdown")]
    public async Task PresentationTaskbarForwards()
    {
        var lifetime = new PresentationLifetime();
        var backend = new FakeTaskbar();
        var taskbar = new PresentationTaskbar(lifetime, backend);
        Check(taskbar.GetSnapshot().Statuses[PlatformCapabilityIds.TaskbarBadge] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "Before the owner.");
        Check(await taskbar.SetBadgeAsync(new() { Count = 2 }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "No owner.");
        lifetime.AttachTestOwner();
        Check(taskbar.GetSnapshot().Statuses[PlatformCapabilityIds.TaskbarProgress] is CapabilityStatus.Available, "Available.");
        Check(await taskbar.SetBadgeAsync(new() { Count = 2 }) is PlatformResult<PlatformUnit>.Success && backend.Badge?.Count == 2, "Badge.");
        Check(await taskbar.SetProgressAsync(new(0.25) { State = DesktopProgressState.Paused }) is PlatformResult<PlatformUnit>.Success
            && backend.Progress is { Value: 0.25, State: DesktopProgressState.Paused }, "Progress.");
        await Throws<ArgumentException>(() => taskbar.SetProgressAsync(new(2)).AsTask());
        backend.Status = new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Check(await taskbar.SetBadgeAsync(null) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "A desktop without badges.");
        await lifetime.DisposeAsync();
        Check(backend.Disposed, "Shutdown removes the window's badge and progress.");
        Check(await taskbar.SetBadgeAsync(null) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed), "Closed.");
        Check(new PresentationTaskbar(Owned()).GetSnapshot().Statuses[PlatformCapabilityIds.TaskbarBadge]
            is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "No provider.");
    }

    private static PlatformResult<DesktopClipboardFormats> Success(DesktopClipboardFormats formats) => new PlatformResult<DesktopClipboardFormats>.Success(formats);

    private sealed class FakeClipboard : IDesktopClipboard
    {
        internal string? Html;
        internal byte[]? Image;
        internal ImmutableArray<string> Files = [];
        internal DesktopClipboardFormats Formats;
        internal int Writes;
        internal bool WatchDisposed;
        public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default) => Ok<string?>(null);
        public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default) => Ok(new PlatformUnit());
        public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) => Ok(Formats);
        public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default) => Ok(Html);
        public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default) => Ok<string?>(null);
        public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default) => Ok(Image);
        public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default) => Ok(Files);
        public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
        {
            Writes++;
            Formats = DesktopClipboardRules.Formats(content);
            return Ok(new PlatformUnit());
        }
        public async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new PlatformResult<DesktopClipboardFormats>.Success(Formats);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { WatchDisposed = true; }
        }
        private static ValueTask<PlatformResult<T>> Ok<T>(T value) => ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Success(value));
    }

    private sealed class TextOnlyClipboard : ITextClipboard
    {
        public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<string?>>(new PlatformResult<string?>.Success("text"));
        public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new()));
    }

    private sealed class FakeTaskbar : IWindowTaskbar
    {
        internal DesktopBadge? Badge;
        internal DesktopProgress? Progress;
        internal bool Disposed;
        public CapabilityStatus Status { get; set; } = new CapabilityStatus.Available();
        public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken)
        {
            Badge = badge;
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new()));
        }
        public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new()));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    // Implements only the members without defaults.
    private sealed class CustomServices : IWindowPlatformServices
    {
        private readonly IWindowPlatformServices _inner = WindowPlatformServices.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        public IFileDialogs FileDialogs => _inner.FileDialogs;
        public ITextClipboard Clipboard => _inner.Clipboard;
        public IDesktopFileLauncher FileLauncher => _inner.FileLauncher;
        public CapabilitySnapshot GetSnapshot() => _inner.GetSnapshot();
    }

    private sealed class Disposal(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }

    private static PresentationLifetime Owned() { var owner = new PresentationLifetime(); owner.AttachTestOwner(); return owner; }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
