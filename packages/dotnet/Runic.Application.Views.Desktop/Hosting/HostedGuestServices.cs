using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// window-services/1 on the guest (host-protocol.md, Window services): a hosted Window's clipboard, monitors and file
// launcher ask the host, which serves them for its own window with its own rights and its policy. Each request names
// the window and is answered with a platform outcome; a channel that closed reports OwnerClosed, as a closed window does.
internal static class HostedServiceRequests
{
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    // The reply of a window-services request and its outcome; a window that closed does not ask.
    internal static async ValueTask<(JsonObject? Reply, PlatformResult<PlatformUnit> Outcome)> RequestAsync(HostedWindow window, JsonObject request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window.IsClosed || window.Guest.IsHostGone) return (null, Closed);
        // A request larger than a message is not sent (host-protocol.md, Messages).
        request["window"] = window.Id;
        if (!HostServices.Fits(request)) return (null, new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
        var reply = await window.Guest.WindowRequestAsync(window.Id, request, cancellationToken).ConfigureAwait(false);
        return reply is null ? (null, Closed) : (reply, HostedWindowPresentation.OutcomeOf(reply));
    }

    internal static PlatformResult<T> As<T>(PlatformResult<PlatformUnit> outcome) => outcome switch
    {
        PlatformResult<PlatformUnit>.Unavailable(var reason) => new PlatformResult<T>.Unavailable(reason),
        PlatformResult<PlatformUnit>.Failed(var code, var diagnostic) => new PlatformResult<T>.Failed(code, diagnostic),
        _ => new PlatformResult<T>.Failed(PlatformFailureCode.Unknown),
    };

    // A successful reply whose value is not what the request asked for: the host broke the protocol.
    internal static PlatformResult<T> Invalid<T>() =>
        new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData, new PlatformDiagnostic("Runic host protocol", 0, "The host's reply is not valid."));
}

// The clipboard of a hosted Window: the host's clipboard, through its window. The host's policy decides each read and
// write; Runic's host reads only for the active window (host-protocol.md, Window services). The watch is not proxied: a
// guest learns of the clipboard's content only when it reads it.
internal sealed class HostedClipboard(HostedWindow window) : IDesktopClipboard
{
    private static readonly IDesktopClipboard Checks = (IDesktopClipboard)WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).Clipboard;

    public async ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        _ = await Checks.ReadTextAsync(maximumCharacters, cancellationToken).ConfigureAwait(false);
        return await ReadStringAsync("text", maximumCharacters, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        _ = await Checks.ReadHtmlAsync(maximumCharacters, cancellationToken).ConfigureAwait(false);
        return await ReadStringAsync("html", maximumCharacters, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        _ = await Checks.ReadRtfAsync(maximumCharacters, cancellationToken).ConfigureAwait(false);
        return await ReadStringAsync("rtf", maximumCharacters, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        _ = await Checks.ReadImageAsync(maximumBytes, cancellationToken).ConfigureAwait(false);
        var (reply, outcome) = await ReadAsync("image", maximumBytes, cancellationToken).ConfigureAwait(false);
        if (reply is null) return HostedServiceRequests.As<byte[]?>(outcome);
        if (reply["value"] is null) return new PlatformResult<byte[]?>.Success(null);
        return HostServices.Base64(HostProtocol.String(reply, "value")) is { } image && image.Length <= maximumBytes
            ? new PlatformResult<byte[]?>.Success(image)
            : HostedServiceRequests.Invalid<byte[]?>();
    }

    public async ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        _ = await Checks.ReadFilesAsync(maximumCount, cancellationToken).ConfigureAwait(false);
        var (reply, outcome) = await ReadAsync("files", maximumCount, cancellationToken).ConfigureAwait(false);
        if (reply is null) return HostedServiceRequests.As<ImmutableArray<string>>(outcome);
        return reply["value"] is JsonArray files && files.Count <= maximumCount && files.All(HostServices.IsAbsolutePath)
            ? new PlatformResult<ImmutableArray<string>>.Success([.. files.Select(static file => file!.GetValue<string>())])
            : HostedServiceRequests.Invalid<ImmutableArray<string>>();
    }

    public async ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default)
    {
        _ = await Checks.GetFormatsAsync(cancellationToken).ConfigureAwait(false);
        var (reply, outcome) = await HostedServiceRequests.RequestAsync(window,
            new JsonObject { ["id"] = 0, ["kind"] = "clipboard-read", ["format"] = "formats" }, cancellationToken).ConfigureAwait(false);
        if (outcome is not PlatformResult<PlatformUnit>.Success) return HostedServiceRequests.As<DesktopClipboardFormats>(outcome);
        return reply!["value"] is JsonArray
            ? new PlatformResult<DesktopClipboardFormats>.Success(HostServices.ReadFormats(reply["value"]))
            : HostedServiceRequests.Invalid<DesktopClipboardFormats>();
    }

    public async ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        _ = await Checks.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
        return await WriteCoreAsync(new DesktopClipboardContent { Text = text }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        _ = await Checks.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        return await WriteCoreAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PlatformResult<PlatformUnit>> WriteCoreAsync(DesktopClipboardContent content, CancellationToken cancellationToken) =>
        (await HostedServiceRequests.RequestAsync(window, new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "clipboard-write",
            ["content"] = HostServices.WriteClipboardContent(content),
        }, cancellationToken).ConfigureAwait(false)).Outcome;

    // The host's clipboard changes are not reported to a guest: it would learn when the user copies, also in other
    // applications.
    public async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new PlatformResult<DesktopClipboardFormats>.Unavailable(
            window.IsClosed ? PlatformUnavailableReason.OwnerClosed : PlatformUnavailableReason.BackendUnavailable);
    }

    private async ValueTask<PlatformResult<string?>> ReadStringAsync(string format, int maximum, CancellationToken cancellationToken)
    {
        var (reply, outcome) = await ReadAsync(format, maximum, cancellationToken).ConfigureAwait(false);
        if (reply is null) return HostedServiceRequests.As<string?>(outcome);
        if (reply["value"] is null) return new PlatformResult<string?>.Success(null);
        return HostProtocol.String(reply, "value") is { } text && text.Length <= maximum
            ? new PlatformResult<string?>.Success(text)
            : HostedServiceRequests.Invalid<string?>();
    }

    // The successful reply, or null and the outcome.
    private async ValueTask<(JsonObject? Reply, PlatformResult<PlatformUnit> Outcome)> ReadAsync(string format, int maximum, CancellationToken cancellationToken)
    {
        var (reply, outcome) = await HostedServiceRequests.RequestAsync(window, new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "clipboard-read",
            ["format"] = format,
            ["maximum"] = Math.Min(maximum, HostServices.MaximumClipboardBound),
        }, cancellationToken).ConfigureAwait(false);
        return outcome is PlatformResult<PlatformUnit>.Success ? (reply, outcome) : (null, outcome);
    }
}

// The monitors of a hosted Window: the host's monitors. The watch lists them again each time the host reports a change
// (monitors-changed), and reports a list only when it changed.
internal sealed class HostedMonitors(HostedWindow window) : IDesktopMonitors
{
    public async ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ListWithCurrentAsync(cancellationToken).ConfigureAwait(false)).Monitors;

    // The list, and the id of the monitor that shows the window, as the host reports it.
    internal async ValueTask<(PlatformResult<ImmutableArray<DesktopMonitor>> Monitors, string? Current)> ListWithCurrentAsync(CancellationToken cancellationToken)
    {
        var (reply, outcome) = await HostedServiceRequests.RequestAsync(window, new JsonObject { ["id"] = 0, ["kind"] = "monitors" }, cancellationToken)
            .ConfigureAwait(false);
        if (outcome is not PlatformResult<PlatformUnit>.Success) return (HostedServiceRequests.As<ImmutableArray<DesktopMonitor>>(outcome), null);
        if (reply!["monitors"] is not JsonArray { Count: <= HostServices.MaximumMonitors } list) return (HostedServiceRequests.Invalid<ImmutableArray<DesktopMonitor>>(), null);
        var monitors = list.Select(HostServices.ReadMonitor).ToArray();
        if (monitors.Any(static monitor => monitor is null)) return (HostedServiceRequests.Invalid<ImmutableArray<DesktopMonitor>>(), null);
        return (new PlatformResult<ImmutableArray<DesktopMonitor>>.Success([.. monitors!]), HostProtocol.String(reply, "current"));
    }

    public async IAsyncEnumerable<PlatformResult<ImmutableArray<DesktopMonitor>>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ImmutableArray<DesktopMonitor>? last = null;
        while (true)
        {
            // Subscribe before listing, so a change during the list is not missed.
            var changed = window.MonitorsChanged();
            var result = await ListAsync(cancellationToken).ConfigureAwait(false);
            if (result is not PlatformResult<ImmutableArray<DesktopMonitor>>.Success(var monitors))
            {
                // Reading failed: the watch reports it and ends, as IDesktopMonitors.WatchAsync does.
                yield return result;
                yield break;
            }
            if (last is not { } previous || !previous.SequenceEqual(monitors))
            {
                last = monitors;
                yield return result;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

// The file launcher of a hosted Window: the host opens the file or URI with its own activation rights, under its policy.
// Runic's host by default opens web and mail links, and only the files the user gave the guest through the host, such as
// in its file dialogs (host-protocol.md, Window services).
internal sealed class HostedFileLauncher(HostedWindow window) : IDesktopFileLauncher
{
    private static readonly IDesktopFileLauncher Checks = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).FileLauncher;

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
        CancellationToken cancellationToken = default)
    {
        _ = await Checks.LaunchAsync(path, operation, cancellationToken).ConfigureAwait(false);
        // The host opens absolute paths only: a relative one would resolve against the host's working directory.
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        return (await HostedServiceRequests.RequestAsync(window, new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "launch",
            ["path"] = path,
            ["operation"] = HostServices.OperationToken(operation),
        }, cancellationToken).ConfigureAwait(false)).Outcome;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        // The launcher's own URI rules, as every implementation checks them.
        _ = await Checks.LaunchUriAsync(uri, cancellationToken).ConfigureAwait(false);
        return (await HostedServiceRequests.RequestAsync(window, new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "launch",
            ["uri"] = uri.AbsoluteUri,
        }, cancellationToken).ConfigureAwait(false)).Outcome;
    }
}
