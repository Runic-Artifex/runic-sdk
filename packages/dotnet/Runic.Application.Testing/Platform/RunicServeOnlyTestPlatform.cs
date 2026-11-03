using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>One call the application made to its test platform fakes, read by a serve-only browser test.</summary>
/// <param name="Sequence">The call's position, from 1, among the application's calls.</param>
/// <param name="Service">The fake: <c>fileDialogs</c>, <c>clipboard</c>, <c>messageBoxes</c>, <c>contextMenus</c>,
/// <c>notifications</c>, <c>launcher</c> or <c>announcements</c>; or <c>script</c> with the operation <c>error</c>
/// when the application rejected a scripted line, with its <c>message</c> and <c>line</c>.</param>
/// <param name="Operation">What was called, such as <c>openFile</c>, <c>saveFile</c>, <c>commit</c>, <c>write</c> or <c>show</c>.</param>
/// <param name="Values">What the call carried, such as a dialog's <c>title</c> and <c>outcome</c> or a commit's
/// <c>file</c> and base64 <c>content</c>; absent values are left out.</param>
public sealed record RunicTestPlatformEvent(long Sequence, string Service, string Operation, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>A value, or <see langword="null"/> when the call did not carry it.</summary>
    /// <param name="name">The value's name.</param>
    /// <returns>The value.</returns>
    public string? Get(string name) => Values.GetValueOrDefault(name);

    /// <summary>A base64 value, such as a commit's <c>content</c>, decoded.</summary>
    /// <param name="name">The value's name.</param>
    /// <returns>The bytes, or <see langword="null"/> when the call did not carry the value.</returns>
    public byte[]? GetBytes(string name) => Get(name) is { } value ? Convert.FromBase64String(value) : null;

    /// <summary>A base64 value decoded as UTF-8 text.</summary>
    /// <param name="name">The value's name.</param>
    /// <returns>The text, or <see langword="null"/> when the call did not carry the value.</returns>
    public string? GetText(string name) => GetBytes(name) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
}

/// <summary>
/// Scripts the platform fakes of an application that a browser test runs with <see cref="RunicServeOnlyApplication"/>,
/// and reads what the application did with them: the file dialogs, message boxes, context menus and clipboard of
/// <see cref="RunicTestPlatform"/>, across the process boundary.
/// </summary>
/// <remarks>
/// <para>
/// The application opts in from a test build with
/// <see cref="RunicTestPlatformServiceCollectionExtensions.AddRunicServeOnlyTestPlatform"/>. The test creates this
/// object, passes the start information through <see cref="Configure"/>, and queues responses before the page action
/// that shows a dialog; the application applies them in order at its next platform call:
/// </para>
/// <code>
/// using var platform = RunicServeOnlyTestPlatform.Create();
/// var start = platform.Configure(RunicServeOnlyApplication.CreateStartInfo(appPath));
/// await using var app = await RunicServeOnlyApplication.StartAsync(start);
/// platform.EnqueueOpenFile("notes.txt", "Hello");
/// await page.GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();
/// var shown = await platform.WaitForEventAsync(e => e.Operation == "openFile");
/// </code>
/// <para>
/// The script and the record are JSON-lines files in a temporary directory that disposing deletes. Each method is
/// safe to call from any thread.
/// </para>
/// </remarks>
public sealed class RunicServeOnlyTestPlatform : IDisposable
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(25);
    private readonly object _gate = new();
    private readonly List<RunicTestPlatformEvent> _events = [];
    private long _eventsOffset;
    private bool _disposed;

    private RunicServeOnlyTestPlatform(string directory) => Directory = directory;

    /// <summary>Creates an empty script in a new temporary directory.</summary>
    /// <returns>The script.</returns>
    public static RunicServeOnlyTestPlatform Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runic-test-platform-" + Guid.NewGuid().ToString("N"));
        // Only the owner may write the script; the application refuses a directory that other users can write to.
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RunicServeOnlyTestPlatform(directory);
    }

    /// <summary>The directory of the script and the record.</summary>
    public string Directory { get; }

    /// <summary>Points the application at this script: sets <see cref="RunicTestPlatformServiceCollectionExtensions.DirectoryEnvironmentVariable"/>.</summary>
    /// <param name="startInfo">The start information for <see cref="RunicServeOnlyApplication.StartAsync"/>, which is changed.</param>
    /// <returns><paramref name="startInfo"/>.</returns>
    public ProcessStartInfo Configure(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment[RunicTestPlatformServiceCollectionExtensions.DirectoryEnvironmentVariable] = Directory;
        return startInfo;
    }

    /// <summary>Queues the selection of an existing file for the next open-file or open-document dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="content">Its bytes.</param>
    public void EnqueueOpenFile(string name, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Append("fileDialogs", "open", ("name", name), ("content", Convert.ToBase64String(content)));
    }

    /// <summary>Queues the selection of a file with UTF-8 text for the next open dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="text">Its text.</param>
    public void EnqueueOpenFile(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnqueueOpenFile(name, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Queues a new save destination for the next save dialog; its commits are recorded with their content.</summary>
    /// <param name="name">The name the user chose.</param>
    public void EnqueueSaveFile(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Append("fileDialogs", "save", ("name", name));
    }

    /// <summary>Queues the selection of directories for the next directory dialog.</summary>
    /// <param name="paths">Fully qualified paths.</param>
    public void EnqueueOpenDirectory(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0) throw new ArgumentException("Select at least one directory.", nameof(paths));
        Append("fileDialogs", "openDirectory", ("path", string.Join('\n', paths)));
    }

    /// <summary>Queues a dismissal of the next file dialog.</summary>
    public void EnqueueFileDialogDismissed() => Append("fileDialogs", "dismissed");

    /// <summary>Queues unavailability for the next file dialog.</summary>
    /// <param name="reason">The reason it reports.</param>
    public void EnqueueFileDialogUnavailable(PlatformUnavailableReason reason) =>
        Append("fileDialogs", "unavailable", ("reason", Defined(reason).ToString()));

    /// <summary>Queues a failure for the next file dialog.</summary>
    /// <param name="code">The failure it reports.</param>
    public void EnqueueFileDialogFailed(PlatformFailureCode code) =>
        Append("fileDialogs", "failed", ("code", Defined(code).ToString()));

    /// <summary>Queues the button the user chooses in the next message box.</summary>
    /// <param name="buttonId">The button's id, or <see langword="null"/> for a box closed without a button.</param>
    public void EnqueueMessageBoxAnswer(string? buttonId) => Append("messageBoxes", "answer", ("button", buttonId));

    /// <summary>Queues the item the user chooses in the next context menu.</summary>
    /// <param name="commandId">The item's command id.</param>
    /// <param name="commandParameter">The item's command parameter, or <see langword="null"/> for the first item with the id.</param>
    public void EnqueueContextMenuChoice(string commandId, string? commandParameter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        Append("contextMenus", "choose", ("commandId", commandId), ("commandParameter", commandParameter));
    }

    /// <summary>Queues a dismissal of the next context menu.</summary>
    public void EnqueueContextMenuDismissed() => Append("contextMenus", "dismissed");

    /// <summary>Replaces the clipboard's content, as another application's copy would, before the application's next clipboard call.</summary>
    /// <param name="content">The content, or <see langword="null"/> to empty the clipboard.</param>
    public void SetClipboard(DesktopClipboardContent? content)
    {
        if (content is null)
        {
            Append("clipboard", "clear");
            return;
        }
        Append("clipboard", "set", ("text", content.Text), ("html", content.Html), ("rtf", content.Rtf),
            ("image", content.Image.IsEmpty ? null : Convert.ToBase64String(content.Image.Span)),
            ("files", content.Files.IsDefaultOrEmpty ? null : string.Join('\n', content.Files)));
    }

    /// <summary>Replaces the clipboard's content with text.</summary>
    /// <param name="text">The text.</param>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetClipboard(new DesktopClipboardContent { Text = text });
    }

    /// <summary>The calls the application made so far, oldest first.</summary>
    /// <returns>The calls.</returns>
    public IReadOnlyList<RunicTestPlatformEvent> ReadEvents()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var line in ServeOnlyPlatformChannel.ReadNewLines(Path.Combine(Directory, ServeOnlyPlatformChannel.EventsFileName), ref _eventsOffset))
                _events.Add(Parse(line));
            return [.. _events];
        }
    }

    /// <summary>Waits for the first call that <paramref name="match"/> accepts, including calls made before this one.</summary>
    /// <param name="match">The condition.</param>
    /// <param name="timeout">How long to wait; 10 seconds by default.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The call.</returns>
    /// <exception cref="TimeoutException">No such call arrived in time; the message lists the calls that did.</exception>
    /// <exception cref="InvalidOperationException">
    /// The application rejected a scripted line, which it records as a <c>script</c> <c>error</c> event, and no
    /// earlier call matched; the message is the application's.
    /// </exception>
    public async Task<RunicTestPlatformEvent> WaitForEventAsync(Func<RunicTestPlatformEvent, bool> match, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        var wait = timeout ?? s_defaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero, nameof(timeout));
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var events = ReadEvents();
            if (events.FirstOrDefault(match) is { } found) return found;
            if (events.FirstOrDefault(static e => e is { Service: "script", Operation: "error" }) is { } error)
                throw new InvalidOperationException("The application rejected a scripted test platform line: " + error.Get("message"));
            if (deadline.Elapsed >= wait)
            {
                throw new TimeoutException($"No matching test platform call arrived within {wait}. The calls were: " +
                    (events.Count == 0 ? "none." : string.Join("; ", events.Select(static e => $"{e.Service}.{e.Operation}"))));
            }
            await Task.Delay(s_poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the script's directory.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { } // The application still holds a file open; the directory is temporary.
        catch (UnauthorizedAccessException) { }
    }

    private void Append(string service, string operation, params (string Name, string? Value)[] values)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("service", service);
            writer.WriteString("operation", operation);
            writer.WriteStartObject("values");
            foreach (var (name, value) in values)
            {
                if (value is not null) writer.WriteString(name, value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        var line = buffer.ToArray();
        if (line.Length > ServeOnlyPlatformChannel.MaximumLineBytes)
            throw new ArgumentException("The scripted response is larger than 64 MiB.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ServeOnlyPlatformChannel.AppendLine(Path.Combine(Directory, ServeOnlyPlatformChannel.ScriptFileName), line);
        }
    }

    private static RunicTestPlatformEvent Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("values").EnumerateObject())
            values[property.Name] = property.Value.GetString() ?? "";
        return new RunicTestPlatformEvent(root.GetProperty("sequence").GetInt64(), root.GetProperty("service").GetString() ?? "",
            root.GetProperty("operation").GetString() ?? "", values);
    }

    private static T Defined<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
