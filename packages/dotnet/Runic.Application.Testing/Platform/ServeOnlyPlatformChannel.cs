using System.Text;
using System.Text.Json;

namespace Runic.Application.Testing;

// The application's side of RunicServeOnlyTestPlatform: two JSON-lines files in a directory the test owns. The test
// appends scripted responses to script.jsonl; the fakes apply new complete lines before each call. The fakes append
// what they were called with to events.jsonl, which the test reads. Each line is written in one write, and a reader
// only takes lines that end with a newline, so neither side sees half a line.
internal sealed class ServeOnlyPlatformChannel
{
    internal const string ScriptFileName = "script.jsonl";
    internal const string EventsFileName = "events.jsonl";
    // A scripted line, such as a file's content in base64, is bounded so a broken script cannot exhaust memory.
    internal const int MaximumLineBytes = 64 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _script;
    private readonly string _events;
    private long _scriptOffset;
    private long _sequence;

    internal ServeOnlyPlatformChannel(string directory)
    {
        _script = Path.Combine(directory, ScriptFileName);
        _events = Path.Combine(directory, EventsFileName);
    }

    internal void Pump(RunicTestPlatform platform)
    {
        lock (_gate)
        {
            foreach (var line in ReadNewLines(_script, ref _scriptOffset))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var service = root.GetProperty("service").GetString();
                var operation = root.GetProperty("operation").GetString() ?? "";
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (root.TryGetProperty("values", out var properties))
                {
                    foreach (var property in properties.EnumerateObject())
                        values[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                }
                switch (service)
                {
                    case "fileDialogs": platform.FileDialogs.EnqueueScripted(operation, values); break;
                    case "messageBoxes": platform.MessageBoxes.EnqueueScripted(operation, values); break;
                    case "contextMenus": platform.ContextMenus.EnqueueScripted(operation, values); break;
                    case "clipboard": platform.Clipboard.EnqueueScripted(operation, values); break;
                    default: throw new InvalidOperationException($"Unknown scripted platform service '{service}'.");
                }
            }
        }
    }

    internal void Record(string service, string operation, (string Name, string? Value)[] values)
    {
        lock (_gate)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", ++_sequence);
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
            AppendLine(_events, buffer.ToArray());
        }
    }

    internal static void AppendLine(string path, byte[] line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(line);
        stream.Flush(flushToDisk: false);
    }

    // The complete lines after offset, which moves past them.
    internal static List<string> ReadNewLines(string path, ref long offset)
    {
        var lines = new List<string>();
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return lines; }
        catch (DirectoryNotFoundException) { return lines; }
        using (stream)
        {
            if (stream.Length <= offset) return lines;
            var length = stream.Length - offset;
            if (length > MaximumLineBytes * 4L) throw new InvalidDataException("The scripted platform file grew beyond its limit.");
            var bytes = new byte[length];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0)
            {
                if (length > MaximumLineBytes) throw new InvalidDataException("A scripted platform line exceeds its limit.");
                return lines;
            }
            offset += end + 1;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
            {
                if (line.Length > 0) lines.Add(line);
            }
        }
        return lines;
    }
}
