using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// One keystroke in a large text member (specs/application/state-changes.md, "Text splices"): the page's set request,
// the setter, the publication of the frame and its broadcast, in the producer. Without text-splice/1 the request and
// the frame carry the whole value; with it, a splice of one character. Each keystroke inserts a character in the middle
// of the value or deletes it again, so the value keeps its length. Up and Down are the bytes of the request and of the
// frame, the same in every operation.
public class TextSpliceBenchmarks
{
    private TextHost _host = null!;
    private TextModel _model = null!;
    private TextBridge _bridge = null!;
    private string _value = "";
    private long _edit;
    private long _revision;
    private byte[] _request = [];

    [Params(100_000)]
    public int Length { get; set; }

    [Params(false, true)]
    public bool Splices { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _value = Text(Length);
        _model = new TextModel { Body = _value };
        _host = new TextHost();
        _bridge = new TextBridge(_host, _model);
        _host.Send(Encoding.UTF8.GetBytes("{\"kind\":\"hello\",\"protocol\":4,\"session\":\"benchmarks\",\"client\":{\"name\":\"benchmarks\",\"version\":\"1.0.0\"}"
            + (Splices ? ",\"features\":[\"text-splice/1\"]}" : "}")));
        using var open = JsonDocument.Parse(_host.Send("{\"kind\":\"open\",\"route\":\"text\",\"presentation\":\"p-1\"}"u8.ToArray()));
        _revision = open.RootElement.GetProperty("revision").GetInt64();
        _request = new byte[Encoding.UTF8.GetMaxByteCount(Length + 64) + 256];
        // One keystroke to warm up and report the sizes.
        Keystroke();
        Keystroke();
        Console.WriteLine($"// Text splices {(Splices ? "on" : "off")}, {Length} characters: {Up} bytes up, {_host.LastFrame} bytes down per keystroke");
    }

    // The bytes of the last request.
    public int Up { get; private set; }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bridge.Dispose();
        _host.Dispose();
    }

    [Benchmark]
    public int Keystroke()
    {
        var edit = ++_edit;
        var middle = Length / 2;
        var insert = edit % 2 == 1;
        var written = Splices ? WriteSplice(edit, insert, middle) : WriteValue(edit, insert, middle);
        Up = written;
        _host.Send(_request.AsMemory(0, written));
        return _host.LastFrame;
    }

    // {"kind":"set","route":"text","member":"body","edit":E,"splice":{"baseEdit"|"baseRevision":B,"length":L,"index":I,"delete":D,"text":T}}
    private int WriteSplice(long edit, bool insert, int middle)
    {
        var span = _request.AsSpan();
        var at = Append(span, 0, "{\"kind\":\"set\",\"route\":\"text\",\"member\":\"body\",\"edit\":"u8);
        at = Append(span, at, edit);
        at = Append(span, at, edit == 1 ? ",\"splice\":{\"baseRevision\":"u8 : ",\"splice\":{\"baseEdit\":"u8);
        at = Append(span, at, edit == 1 ? _revision : edit - 1);
        at = Append(span, at, ",\"length\":"u8);
        at = Append(span, at, insert ? Length : Length + 1);
        at = Append(span, at, ",\"index\":"u8);
        at = Append(span, at, middle);
        at = Append(span, at, insert ? ",\"delete\":0,\"text\":\"x\"}}"u8 : ",\"delete\":1,\"text\":\"\"}}"u8);
        return at;
    }

    // {"kind":"set","route":"text","member":"body","edit":E,"value":"…"}: the value a page's JSON.stringify writes.
    private int WriteValue(long edit, bool insert, int middle)
    {
        var span = _request.AsSpan();
        var at = Append(span, 0, "{\"kind\":\"set\",\"route\":\"text\",\"member\":\"body\",\"edit\":"u8);
        at = Append(span, at, edit);
        at = Append(span, at, ",\"value\":\""u8);
        at += Encoding.UTF8.GetBytes(_value.AsSpan(0, middle), span[at..]);
        if (insert) span[at++] = (byte)'x';
        at += Encoding.UTF8.GetBytes(_value.AsSpan(middle), span[at..]);
        return Append(span, at, "\"}"u8);
    }

    private static int Append(Span<byte> span, int at, ReadOnlySpan<byte> text)
    {
        text.CopyTo(span[at..]);
        return at + text.Length;
    }

    private static int Append(Span<byte> span, int at, long value)
    {
        value.TryFormat(span[at..], out var written, provider: CultureInfo.InvariantCulture);
        return at + written;
    }

    // Prose without JSON escapes, which does not repeat within the splice's reach.
    internal static string Text(int length)
    {
        var builder = new StringBuilder(length + 32);
        for (var line = 0; builder.Length < length; line++)
            builder.Append(CultureInfo.InvariantCulture, $"Line {line}: the quick brown fox jumps over the lazy dog. ");
        return builder.ToString(0, length);
    }
}

public sealed class TextModel : INotifyPropertyChanged
{
    private string _body = "";

    public string Body
    {
        get => _body;
        set { _body = value; PropertyChanged?.Invoke(this, new(nameof(Body))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// The bridge that generated code writes for TextModel: a settable text member.
public sealed class TextBridge(IBridgeTransport transport, TextModel model)
    : ViewModelBridge<TextModel>(transport, model, "text",
        [new("body", static (writer, model) => writer.WriteStringValue(model.Body))],
        [
            new("Body", "body", model => model.Body, (model, value) => model.Body = value.GetString()!)
            { TextMember = true, TextSetter = static (model, value) => model.Body = value },
        ],
        [], [], contractFingerprint: "benchmarks");

// A transport that delivers at once and records the size of the last frame.
public sealed class TextHost : IBridgeTransport, IDisposable
{
    private IBridgeRequestHandler? _handler;

    public int LastFrame { get; private set; }

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return this;
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        LastFrame = message.Length;
        return ValueTask.CompletedTask;
    }

    public ReadOnlyMemory<byte> Send(ReadOnlyMemory<byte> request) =>
        (_handler ?? throw new InvalidOperationException("No window serves the transport."))
            .HandleAsync(new BridgeRequest(request, "benchmarks", "benchmarks"), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void Dispose() => _handler = null;
}
