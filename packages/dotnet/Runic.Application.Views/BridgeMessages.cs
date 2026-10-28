using System.Buffers;
using System.Text.Json;

namespace Runic.Application.Views;

// Writers of the Views protocol 4 envelope messages that the dispatcher and route handlers share: failure replies,
// the hello reply and the receive reply. Each returns the message as UTF-8 that the caller owns.
internal static class BridgeMessages
{
    internal const string ReleasedMessage = "This View was released.";

    // The reply to a request for a route the producer does not serve (specs/application/README.md, "Errors").
    internal static readonly ReadOnlyMemory<byte> Released =
        """{"ok":false,"error":{"kind":"released","message":"This View was released."}}"""u8.ToArray();

    internal static readonly ReadOnlyMemory<byte> EmptyReceive = """{"ok":true,"messages":[]}"""u8.ToArray();

    internal static readonly ReadOnlyMemory<byte> Ok = """{"ok":true}"""u8.ToArray();

    // The reply of a close request, or the failure of an open that registered nothing.
    internal static ReadOnlyMemory<byte> Presentation(BridgePresentationOutcome outcome) => outcome switch
    {
        BridgePresentationOutcome.Ok => PresentationOk,
        BridgePresentationOutcome.Ignored => PresentationIgnored,
        BridgePresentationOutcome.Capacity => Rejected("capacity",
            $"The connection already holds {BridgePresentationOwners.MaximumPerConnection} presentations of this route."),
        _ => Released,
    };

    private static readonly ReadOnlyMemory<byte> PresentationOk = """{"ok":true,"presentation":"ok"}"""u8.ToArray();
    private static readonly ReadOnlyMemory<byte> PresentationIgnored = """{"ok":true,"presentation":"ignored"}"""u8.ToArray();

    // {"ok":false,"error":{"kind","reason"?,"message"}}, with the hello members when the request was a hello.
    internal static byte[] Failure(string kind, string? reason, string message, bool hello = false) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            if (hello) WriteIdentity(writer);
            writer.WriteStartObject("error");
            writer.WriteString("kind", kind);
            if (reason is not null) writer.WriteString("reason", reason);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Rejected(string reason, string message, bool hello = false) =>
        Failure("rejected", reason, message, hello);

    internal static byte[] Hello() =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            WriteIdentity(writer);
            writer.WriteEndObject();
        });

    // {"ok":true,"messages":[…]} with each queued push embedded as it was sent.
    internal static byte[] Receive(IReadOnlyList<byte[]> messages)
    {
        var length = 0;
        foreach (var message in messages) length += message.Length + 1;
        var buffer = new byte[EmptyReceive.Length + length - (messages.Count > 0 ? 1 : 0)];
        var prefix = """{"ok":true,"messages":["""u8;
        prefix.CopyTo(buffer);
        var offset = prefix.Length;
        for (var index = 0; index < messages.Count; index++)
        {
            if (index > 0) buffer[offset++] = (byte)',';
            messages[index].CopyTo(buffer, offset);
            offset += messages[index].Length;
        }
        "]}"u8.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteIdentity(Utf8JsonWriter writer)
    {
        writer.WriteNumber("protocol", BridgeDispatcher.ProtocolVersion);
        writer.WriteStartObject("implementation");
        writer.WriteString("name", BridgeDispatcher.ImplementationName);
        writer.WriteString("version", BridgeProtocol.PackageVersion);
        writer.WriteEndObject();
    }
}
