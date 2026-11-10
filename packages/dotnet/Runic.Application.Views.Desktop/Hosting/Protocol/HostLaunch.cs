using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Runic.Application.Views;

namespace Runic.Application.Views.Desktop.Hosting;

// The host's side of the launch contract and the handshake (host-protocol.md, Lines, Handshake and Host rules): the
// guest's launch line, the hello and the check of its reply, for the host (RunicHostedApplication) and for RunicTestHost,
// which links this file.
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

    // Connects to the channel, sends hello with members (features, locale, languages, appearance) at once and checks the
    // reply: the guest proof first, then ok and the protocol (host-protocol.md, Host rules). An upgrade that is refused,
    // or a connection that closes before a reply, is tried again after a pause that grows from 100 milliseconds to 1
    // second, until deadline, the start timeout's token; a guest that exited meanwhile fails with guest-exited.
    // cancellationToken is the caller's.
    internal static async Task<Hello> HandshakeAsync(HostProtocol.ChannelContent channel, byte[] nonce, string hostName,
        string hostVersion, JsonObject members, Failure failure, Func<bool> exited, Func<string> exitCode,
        CancellationToken deadline, CancellationToken cancellationToken)
    {
        var offered = Names(members["features"] as JsonArray);
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
        var pause = TimeSpan.FromMilliseconds(100);
        ClientWebSocket socket;
        JsonObject reply;
        while (true)
        {
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            // Straight to the loopback channel: a proxy from HTTP_PROXY, ALL_PROXY or the system's must never see the key
            // and the host proof.
            socket.Options.Proxy = null;
            JsonObject? received = null;
            Exception? lost = null;
            try
            {
                await socket.ConnectAsync(channel.Url, deadline).ConfigureAwait(false);
                await HostChannel.SendAsync(socket, hello.DeepClone().AsObject(), deadline).ConfigureAwait(false);
                received = await HostChannel.ReceiveAsync(socket, deadline, HostProtocol.MaximumHelloBytes).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw failure("guest-start-timeout", "The guest did not answer the hello within the start timeout.", error);
            }
            catch (HostProtocolViolationException error)
            {
                socket.Dispose();
                throw failure("guest-not-hostable", "The guest's hello reply is not a message of the protocol.", error);
            }
            catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException) { lost = error; }
            if (received is not null)
            {
                reply = received;
                break;
            }
            socket.Dispose();
            if (exited()) throw failure("guest-exited", $"The guest exited with {exitCode()} before it answered the hello.", lost);
            try { await Task.Delay(pause, deadline).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw failure("guest-start-timeout", "The guest's channel did not take the hello within the start timeout.", lost);
            }
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, TimeSpan.TicksPerSecond));
        }
        // The guest proof first, before ok or error: a process that took a gone guest's port cannot compute it (T9), so
        // it cannot make the start fail with a code of its choice either.
        if (!HostProtocol.FixedTimeEquals(HostProtocol.String(reply, "proof"), HostProtocol.Proof(nonce, HostProtocol.GuestProofLabel, channel.Key)))
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "").ConfigureAwait(false);
            throw failure("guest-not-hostable", "The guest's hello reply did not prove that it knows the offer's nonce.");
        }
        if (HostProtocol.Boolean(reply, "ok") != true)
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
            var reason = reply["error"] is JsonObject error ? HostProtocol.String(error, "reason") : null;
            throw reason == "protocol-mismatch"
                ? failure("protocol-unsupported", "The guest answered the hello with protocol-mismatch.")
                : failure("guest-not-hostable", $"The guest refused the hello ({reason ?? "no reason"}).");
        }
        if (HostProtocol.Integer(reply, "protocol") != channel.Protocol)
        {
            await HostChannel.CloseAsync(socket, WebSocketCloseStatus.ProtocolError, "").ConfigureAwait(false);
            throw failure("guest-not-hostable", "The guest's hello reply names another protocol version.");
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
