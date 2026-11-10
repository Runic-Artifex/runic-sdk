using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Platform;

namespace Runic.Application.Testing;

// The appearance and locale pushes and window-services/1 of the test host (host-protocol.md, Appearance and locale, and
// Window services): a simulated clipboard and monitors, recorded launches and dropped files.
public sealed partial class RunicTestHost
{
    private readonly object _monitorGate = new();
    private IReadOnlyList<DesktopMonitor>? _monitors;

    /// <summary>
    /// The host's simulated clipboard, which the guest's clipboard reads and writes with <c>window-services/1</c>: set what
    /// the user copied with <see cref="RunicTestClipboard.SetText"/> before the guest reads, and check
    /// <see cref="RunicTestClipboard.Writes"/> after it wrote. By default the guest reads it only for its active window
    /// (<see cref="RunicTestHostOptions.ClipboardReadsOnlyActiveWindow"/>).
    /// </summary>
    public RunicTestClipboard Clipboard { get; } = new RunicTestPlatform().Clipboard;

    /// <summary>The host's monitors that the guest lists: <see cref="RunicTestHostOptions.Monitors"/> until <see cref="SetMonitorsAsync"/>.</summary>
    public IReadOnlyList<DesktopMonitor> Monitors
    {
        get { lock (_monitorGate) return _monitors ?? _options.Monitors; }
    }

    /// <summary>
    /// Reports a change of the host's application locale and its user's languages to the guest, as the <c>locale</c> push
    /// does. The guest's <c>IRunicApplicationLocale</c> follows it unless the guest set a locale of its own.
    /// </summary>
    /// <param name="locale">The host's locale, a BCP 47 tag, or <see langword="null"/> for none.</param>
    /// <param name="languages">The languages the host's user prefers; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    /// <exception cref="ArgumentException"><paramref name="locale"/> or a language is not a locale tag.</exception>
    public Task SetLocaleAsync(string? locale, IReadOnlyList<string>? languages = null, CancellationToken cancellationToken = default)
    {
        CheckLocale(locale, languages);
        return PushAsync(new JsonObject
        {
            ["kind"] = "locale",
            ["locale"] = locale,
            ["languages"] = HostServices.WriteLanguages(languages ?? []),
        }, cancellationToken);
    }

    internal static void CheckLocale(string? locale, IReadOnlyList<string>? languages)
    {
        if (locale is not null && !HostServices.IsLocale(locale))
            throw new ArgumentException($"'{locale}' is not a locale tag, such as \"de\" or \"pt-BR\".", nameof(locale));
        if (languages?.FirstOrDefault(language => !HostServices.IsLocale(language)) is { } invalid)
            throw new ArgumentException($"'{invalid}' is not a locale tag.", nameof(languages));
    }

    /// <summary>
    /// Changes the host's monitors and reports the change to the guest's open windows (<c>monitors-changed</c>), whose
    /// monitor watches list them again.
    /// </summary>
    /// <param name="monitors">The monitors, the primary one first.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the pushes were sent.</returns>
    public async Task SetMonitorsAsync(IReadOnlyList<DesktopMonitor> monitors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        lock (_monitorGate) _monitors = [.. monitors];
        foreach (var window in Windows.Where(static window => !window.Refused && !window.IsClosed))
            await PushAsync(new JsonObject { ["kind"] = "monitors-changed", ["window"] = window.Id }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> OnServiceRequestAsync(HostRequest request, string? id)
    {
        if (request.Kind is not ("clipboard-read" or "clipboard-write" or "monitors" or "launch")) return false;
        var message = request.Message;
        var valid = Features.Contains(HostServices.Feature, StringComparer.Ordinal) && request.Kind switch
        {
            "clipboard-read" => HostServices.CheckClipboardRead(message),
            "clipboard-write" => HostServices.CheckClipboardWrite(message),
            "launch" => HostServices.CheckLaunch(message),
            _ => true,
        };
        if (!valid || FindWindow(id) is not { Refused: false } window)
        {
            await request.FailAsync("rejected", "invalid-request", $"The '{request.Kind}' request is not valid here.").ConfigureAwait(false);
            return true;
        }
        if (window.IsClosed)
        {
            await request.ReplyAsync(new JsonObject { ["outcome"] = "unavailable", ["reason"] = "owner-closed" }).ConfigureAwait(false);
            return true;
        }
        switch (request.Kind)
        {
            case "clipboard-read":
                // As Runic's host by default: the clipboard's content goes to the active window only.
                if (_options.ClipboardReadsOnlyActiveWindow && !window.IsActive)
                {
                    await request.FailAsync("rejected", "refused", "The test host reads the clipboard only for the active window.").ConfigureAwait(false);
                    return true;
                }
                var maximum = (int)(HostProtocol.Integer(message, "maximum") ?? 0);
                await request.ReplyAsync(HostProtocol.String(message, "format") switch
                {
                    "formats" => Reply(await Clipboard.GetFormatsAsync().ConfigureAwait(false), HostServices.WriteFormats),
                    "text" => Reply(await Clipboard.ReadTextAsync(maximum).ConfigureAwait(false), static text => text),
                    "html" => Reply(await Clipboard.ReadHtmlAsync(maximum).ConfigureAwait(false), static text => text),
                    "rtf" => Reply(await Clipboard.ReadRtfAsync(maximum).ConfigureAwait(false), static text => text),
                    "image" => Reply(await Clipboard.ReadImageAsync(maximum).ConfigureAwait(false), static image => image is null ? null : Convert.ToBase64String(image)),
                    _ => Reply(await Clipboard.ReadFilesAsync(maximum).ConfigureAwait(false), static files => new JsonArray([.. files.Select(static file => (JsonNode)file)])),
                }).ConfigureAwait(false);
                return true;
            case "clipboard-write":
                var content = HostServices.ReadClipboardContent((JsonObject)message["content"]!);
                PlatformResult<PlatformUnit> written;
                try { written = await Clipboard.WriteAsync(content).ConfigureAwait(false); }
                catch (ArgumentException error)
                {
                    await request.FailAsync("rejected", "invalid-request", $"The clipboard content is not valid: {error.Message}").ConfigureAwait(false);
                    return true;
                }
                await request.ReplyAsync(Outcome(written)).ConfigureAwait(false);
                return true;
            case "monitors":
                var monitors = Monitors;
                await request.ReplyAsync(new JsonObject
                {
                    ["outcome"] = "success",
                    ["monitors"] = new JsonArray([.. monitors.Select(static monitor => (JsonNode)HostServices.WriteMonitor(monitor))]),
                    ["current"] = monitors.Count > 0 ? monitors[0].Id : null,
                }).ConfigureAwait(false);
                return true;
            default:
                var launch = HostProtocol.String(message, "uri") is { } uri
                    ? new RunicTestLaunchRequest(window.Id, new Uri(uri, UriKind.Absolute), null, DesktopFileOperation.Open)
                    : new RunicTestLaunchRequest(window.Id, null, HostProtocol.String(message, "path"),
                        HostServices.Operation(HostProtocol.String(message, "operation") ?? "open")!.Value);
                window.RecordLaunch(launch);
                await request.ReplyAsync(Outcome(_options.Launch?.Invoke(launch) ?? new PlatformResult<PlatformUnit>.Success(new PlatformUnit()))).ConfigureAwait(false);
                return true;
        }
    }

    private static JsonObject Outcome<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Unavailable unavailable => new JsonObject { ["outcome"] = "unavailable", ["reason"] = HostProtocol.Kebab(unavailable.Reason.ToString()) },
        PlatformResult<T>.Failed failed => new JsonObject { ["outcome"] = "failed", ["code"] = HostProtocol.Kebab(failed.Code.ToString()) },
        _ => new JsonObject { ["outcome"] = "success" },
    };

    // A clipboard-read reply: the value of a success, else the outcome.
    private static JsonObject Reply<T>(PlatformResult<T> result, Func<T, JsonNode?> write)
    {
        var reply = Outcome(result);
        if (result is PlatformResult<T>.Success(var value)) reply["value"] = write(value);
        return reply;
    }
}

/// <summary>A file or link a guest asked the test host to open (<c>launch</c>).</summary>
/// <param name="WindowId">The guest's window.</param>
/// <param name="Uri">The link, or <see langword="null"/> for a file.</param>
/// <param name="Path">The absolute path of the file, or <see langword="null"/> for a link.</param>
/// <param name="Operation">What to do with the file.</param>
/// <remarks>The protocol and this type are experimental; see <c>docs/experimental.md</c>.</remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestLaunchRequest(string WindowId, Uri? Uri, string? Path, DesktopFileOperation Operation);

public sealed partial class RunicTestHostedWindow
{
    private readonly List<RunicTestLaunchRequest> _launches = [];
    private volatile bool _active = true;

    /// <summary>The guest Window's locale, as its <c>window-open</c> named it, or <see langword="null"/>.</summary>
    public string? Locale { get; internal init; }

    /// <summary>Whether the guest's Window takes dropped files (<c>window-open</c>'s <c>fileDrops</c>).</summary>
    public bool AcceptsFileDrops { get; internal init; }

    /// <summary>The files and links the guest asked the host to open for this window, in order.</summary>
    public IReadOnlyList<RunicTestLaunchRequest> LaunchRequests
    {
        get { lock (_launches) return [.. _launches]; }
    }

    // Whether the window has the keyboard focus, as the last activity push reported it; each window is active when it opens.
    internal bool IsActive => _active;

    internal void RecordLaunch(RunicTestLaunchRequest request)
    {
        lock (_launches) _launches.Add(request);
    }

    /// <summary>
    /// Drops files onto the window, as the user does: the guest's Window receives them (<c>files-dropped</c>), when it takes
    /// dropped files (<see cref="AcceptsFileDrops"/>).
    /// </summary>
    /// <param name="paths">The absolute paths of the dropped files and directories.</param>
    /// <param name="target">The page's routing label (<c>data-runic-drop-target</c>), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty or has a path that is not absolute.</exception>
    /// <exception cref="InvalidOperationException">The window does not take dropped files, or <c>window-services/1</c> is not in use.</exception>
    public Task DropFilesAsync(IReadOnlyList<string> paths, string? target = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || paths.Any(static path => !HostServices.IsAbsolutePath(JsonValue.Create(path))))
            throw new ArgumentException("A drop has at least one path, and every path is absolute.", nameof(paths));
        if (!AcceptsFileDrops) throw new InvalidOperationException("The guest's Window does not take dropped files.");
        var push = new JsonObject
        {
            ["kind"] = "files-dropped",
            ["window"] = Id,
            ["paths"] = new JsonArray([.. paths.Select(static path => (JsonNode)path)]),
            ["effect"] = "copy",
        };
        if (target is not null) push["target"] = target;
        return _host.PushAsync(push, cancellationToken);
    }
}
