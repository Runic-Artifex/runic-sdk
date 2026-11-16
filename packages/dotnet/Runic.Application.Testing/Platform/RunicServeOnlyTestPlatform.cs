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
/// that shows a dialog; the application applies them in order at its next platform call. The fakes have the names
/// of <see cref="RunicTestPlatform"/>'s (<c>FileDialogs.EnqueueOpen</c>, <c>Clipboard.SetText</c>,
/// <c>ContextMenus.EnqueueChoice</c>), so a test moves between the in-process host and serve-only unchanged:
/// </para>
/// <code>
/// using var platform = RunicServeOnlyTestPlatform.Create();
/// var start = platform.Configure(RunicServeOnlyApplication.CreateStartInfo(appPath));
/// await using var app = await RunicServeOnlyApplication.StartAsync(start);
/// platform.FileDialogs.EnqueueOpen("notes.txt", "Hello");
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

    private RunicServeOnlyTestPlatform(string directory)
    {
        Directory = directory;
        FileDialogs = new RunicServeOnlyFileDialogs(this);
        MessageBoxes = new RunicServeOnlyMessageBoxes(this);
        ContextMenus = new RunicServeOnlyContextMenus(this);
        Clipboard = new RunicServeOnlyClipboard(this);
    }

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

    /// <summary>Scripts the application's file dialogs, with the names of <see cref="RunicTestPlatform.FileDialogs"/>.</summary>
    public RunicServeOnlyFileDialogs FileDialogs { get; }

    /// <summary>Scripts the application's message boxes, with the names of <see cref="RunicTestPlatform.MessageBoxes"/>.</summary>
    public RunicServeOnlyMessageBoxes MessageBoxes { get; }

    /// <summary>Scripts the application's context menus, with the names of <see cref="RunicTestPlatform.ContextMenus"/>.</summary>
    public RunicServeOnlyContextMenus ContextMenus { get; }

    /// <summary>Scripts the application's clipboard, with the names of <see cref="RunicTestPlatform.Clipboard"/>.</summary>
    public RunicServeOnlyClipboard Clipboard { get; }

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

    internal void Append(string service, string operation, params (string Name, string? Value)[] values)
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

    internal static T Defined<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
