using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Runic.Application.CsWebUi;

/// <summary>
/// Small fixed-shape JSON writer for the internal Window Bridge host.
/// Keeping this protocol hand-written avoids activating reflection-based JSON
/// metadata in a NativeAOT application.
/// </summary>
internal static class WindowBridgeJson
{
    internal static string Write(Action<Utf8JsonWriter> write)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            write(writer);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    internal static string StringLiteral(string value) => Write(writer => writer.WriteStringValue(value));
}
