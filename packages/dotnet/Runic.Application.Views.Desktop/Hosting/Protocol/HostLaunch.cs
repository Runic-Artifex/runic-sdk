using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Runic.Application.Views;

namespace Runic.Application.Views.Desktop.Hosting;

// The host's side of the launch contract and the handshake (host-protocol.md, Lines, Handshake and Host rules): the
// guest's launch line, the hello and the check of its reply. Shared by the host (RunicHostedApplication) and
// RunicTestHost, which links this file.
internal static class HostLaunch
{
    // Creates the start failure (host-protocol.md, Host start failures); the caller adds what it knows of the guest.
    internal delegate RunicHostedApplicationException Failure(string code, string message, Exception? inner = null, string? guestCode = null);

    internal sealed record Hello(WebSocket Socket, int Protocol, string Name, string Version, string? ApplicationId, IReadOnlyList<string> Features);

    // Whether a line of the guest's standard output is its launch line: the first such line decides the start.
    internal static bool IsLaunchLine(string line) =>
        line.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal) ||
        line.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal);

    // The channel of the guest's launch line, or the failure of a refusal, an invalid line or none (null: the guest's
    // standard output ended first).
    internal static HostProtocol.ChannelContent Channel(string? line, IReadOnlyList<int> offered, Func<string> exitCode, Failure failure)
    {
        if (line is null)
            throw failure("guest-exited", $"The guest exited with {exitCode()} before it wrote a {HostProtocol.ChannelPrefix} line.");
        if (line.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal))
        {
            var refusal = HostProtocol.ParseLine(line, HostProtocol.RefusedPrefix);
            var code = refusal is null ? null : HostProtocol.String(refusal, "code");
            var message = refusal is null ? null : HostProtocol.String(refusal, "message");
            if (message is { Length: > 1_024 }) message = message[..1_024];
            throw failure(code == "protocol-unsupported" ? "protocol-unsupported" : "guest-refused",
                $"The guest refused to be hosted ({code ?? "unknown"}): {message}", guestCode: code);
        }
        if (HostProtocol.ParseLine(line, HostProtocol.ChannelPrefix) is not { } channelLine ||
            HostProtocol.ReadChannel(channelLine) is not { } channel || !offered.Contains(channel.Protocol))
            throw failure("guest-not-hostable", "The guest wrote an invalid channel line.");
        return channel;
    }

    // Connects to the channel, sends hello with members (features, locale, languages, appearance) and checks the reply:
    // the guest proof first, then the protocol (host-protocol.md, Host rules). deadline is the start timeout's token,
    // cancellationToken the caller's.
    internal static async Task<Hello> HandshakeAsync(HostProtocol.ChannelContent channel, byte[] nonce, string hostName,
        string hostVersion, JsonObject members, Failure failure, CancellationToken deadline, CancellationToken cancellationToken)
    {
        var offered = Names(members["features"] as JsonArray);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try { await socket.ConnectAsync(channel.Url, deadline).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw failure(error is OperationCanceledException ? "guest-start-timeout" : "guest-not-hostable",
                "The guest's channel refused the connection.", error);
        }
        JsonObject? reply;
        try
        {
            var hello = new JsonObject
            {
                ["id"] = 1,
                ["kind"] = "hello",
                ["protocol"] = channel.Protocol,
                ["key"] = channel.Key,
                ["proof"] = HostProtocol.Proof(nonce, HostProtocol.HostProofLabel, channel.Key),
                ["host"] = HostProtocol.ProductNode(hostName, hostVersion),
            };
            foreach (var (name, value) in members.ToArray())
            {
                members.Remove(name);
                hello[name] = value;
            }
            await HostChannel.SendAsync(socket, hello, deadline).ConfigureAwait(false);
            reply = await HostChannel.ReceiveAsync(socket, deadline).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or HostProtocolViolationException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw failure(error is OperationCanceledException ? "guest-start-timeout" : "guest-not-hostable",
                "The guest did not answer the hello.", error);
        }
        // The guest proof first: a process that took a gone guest's port cannot compute it (T9).
        if (reply is null || !HostProtocol.FixedTimeEquals(HostProtocol.String(reply, "proof"),
                HostProtocol.Proof(nonce, HostProtocol.GuestProofLabel, channel.Key)))
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "").ConfigureAwait(false);
            throw failure("guest-not-hostable", "The guest's hello reply did not prove that it knows the offer's nonce.");
        }
        if (HostProtocol.Boolean(reply, "ok") != true)
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
            var reason = reply["error"] is JsonObject error ? HostProtocol.String(error, "reason") : null;
            throw failure(reason == "protocol-mismatch" ? "protocol-unsupported" : "guest-not-hostable", "The guest refused the hello.");
        }
        if (HostProtocol.Integer(reply, "protocol") != channel.Protocol)
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "").ConfigureAwait(false);
            throw failure("guest-not-hostable", "The guest's hello reply named another protocol version.");
        }
        var guest = reply["guest"] as JsonObject;
        // A feature is in use when both hellos list it (host-protocol.md, Features).
        var features = Names(reply["features"] as JsonArray).Intersect(offered, StringComparer.Ordinal).ToArray();
        return new Hello(socket, channel.Protocol,
            guest is null ? "" : HostProtocol.Text(guest, "name", 128) ?? "",
            guest is null ? "" : HostProtocol.Text(guest, "version", 64) ?? "",
            guest is null ? null : HostProtocol.Text(guest, "applicationId", 255),
            features);
    }

    private static string[] Names(JsonArray? list) =>
        [.. (list ?? []).Select(static item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
            .OfType<string>().Distinct(StringComparer.Ordinal)];
}
