using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The canonical form (RFC 8785, JCS) against vectors in the
// runic.views.vector/1 format. Each case canonicalizes its input bytes and
// expects either the exact output bytes or an error code.
internal sealed class CanonicalJsonVectorTests
{
    // The project file copies this suite's vectors and those of
    // specs/application/conformance/vectors/canonical here.
    private static readonly string s_vectors = Path.Combine(AppContext.BaseDirectory, "vectors", "canonical");

    [Test]
    public void CanonicalVectors()
    {
        var files = Directory.GetFiles(s_vectors, "*.json");
        Require(files.Length > 0, $"No canonical vectors in {s_vectors}.");
        var failures = new List<string>();
        var count = 0;
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var vector in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (vector.GetProperty("operation").GetString() != "canonicalize") continue;
                count++;
                var id = vector.GetProperty("id").GetString();
                var input = Bytes(vector.GetProperty("input"));
                var expect = vector.GetProperty("expect");
                if (expect.TryGetProperty("output", out var output))
                {
                    var expected = Bytes(output);
                    try
                    {
                        var actual = BridgeWire.Canonicalize(input);
                        if (!actual.AsSpan().SequenceEqual(expected))
                            failures.Add($"{id}: {Encoding.UTF8.GetString(actual)} instead of {Encoding.UTF8.GetString(expected)}");
                    }
                    catch (Exception error) when (error is JsonException or FormatException)
                    {
                        failures.Add($"{id}: {error.GetType().Name} {error.Message}");
                    }
                    continue;
                }
                var code = expect.GetProperty("error").GetProperty("code").GetString();
                var exception = ExceptionFor(code);
                try
                {
                    var actual = BridgeWire.Canonicalize(input);
                    failures.Add($"{id}: accepted as {Encoding.UTF8.GetString(actual)} instead of {code}");
                }
                catch (Exception error) when (error is JsonException or FormatException)
                {
                    if (!exception.IsInstanceOfType(error)) failures.Add($"{id}: {error.GetType().Name} instead of {exception.Name} for {code}");
                }
            }
        }
        Require(failures.Count == 0, $"{failures.Count} of {count} canonical vectors failed:\n" + string.Join('\n', failures));
    }

    // RFC 8785 Appendix B: IEEE 754 doubles and their ECMAScript form.
    [Test]
    public void AppendixBNumbers()
    {
        Span<byte> output = stackalloc byte[BridgeCanonicalJson.MaximumNumberLength];
        foreach (var (bits, expected) in new (ulong, string?)[]
        {
            (0x0000000000000000, "0"), (0x8000000000000000, "0"), (0x0000000000000001, "5e-324"),
            (0x8000000000000001, "-5e-324"), (0x7fefffffffffffff, "1.7976931348623157e+308"),
            (0xffefffffffffffff, "-1.7976931348623157e+308"), (0x4340000000000000, "9007199254740992"),
            (0xc340000000000000, "-9007199254740992"), (0x4430000000000000, "295147905179352830000"),
            (0x7fffffffffffffff, null), (0x7ff0000000000000, null), (0x44b52d02c7e14af5, "9.999999999999997e+22"),
            (0x44b52d02c7e14af6, "1e+23"), (0x44b52d02c7e14af7, "1.0000000000000001e+23"),
            (0x444b1ae4d6e2ef4e, "999999999999999700000"), (0x444b1ae4d6e2ef4f, "999999999999999900000"),
            (0x444b1ae4d6e2ef50, "1e+21"), (0x3eb0c6f7a0b5ed8c, "9.999999999999997e-7"),
            (0x3eb0c6f7a0b5ed8d, "0.000001"), (0x41b3de4355555553, "333333333.3333332"),
            (0x41b3de4355555554, "333333333.33333325"), (0x41b3de4355555555, "333333333.3333333"),
            (0x41b3de4355555556, "333333333.3333334"), (0x41b3de4355555557, "333333333.33333343"),
            (0xbecbf647612f3696, "-0.0000033333333333333333"), (0x43143ff3c1cb0959, "1424953923781206.2"),
        })
        {
            var value = BitConverter.UInt64BitsToDouble(bits);
            if (expected is null)
            {
                Require(Throws<FormatException>(() => BridgeCanonicalJson.FormatNumber(value, new byte[BridgeCanonicalJson.MaximumNumberLength])),
                    $"The non-finite double {bits:x16} was accepted.");
                continue;
            }
            var actual = Encoding.UTF8.GetString(output[..BridgeCanonicalJson.FormatNumber(value, output)]);
            Require(actual == expected, $"The double {bits:x16} was written {actual}, not {expected}.");
        }
    }

    // Messages are not canonical, but the bridge's message writer uses the
    // same minimal escaping: non-ASCII text is literal UTF-8.
    [Test]
    public void MessagesUseMinimalEscaping()
    {
        const string text = "über <a href='x'>&+</a> 😀 \u2028\"\\\n\u0001";
        const string expected = "\"über <a href='x'>&+</a> 😀 \u2028\\\"\\\\\\n\\u0001\"";
        using (var raw = BridgeCanonicalJson.WriteRaw(static (writer, value) => writer.WriteStringValue(value), text))
            Require(Encoding.UTF8.GetString(raw.Written) == expected, $"A message string was escaped as {Encoding.UTF8.GetString(raw.Written)}.");
        var codec = new BridgeValueCodec<string>(element => element.GetString()!, (writer, value) => writer.WriteStringValue(value));
        Require(codec.Encode(text) == expected, $"A canonical string was escaped as {codec.Encode(text)}.");
        // Ill-formed text is written as U+FFFD, wherever it is.
        foreach (var (input, output) in new[]
        {
            ("a\ud800b", "\"a\\ufffdb\""), ("é\ud800", "\"é\\ufffd\""), ("😀\ud83dx", "\"😀\\ufffdx\""),
            ("\udc00😀", "\"\\ufffd😀\""), ("long ascii text before a lone surrogate \udfff", "\"long ascii text before a lone surrogate \\ufffd\""),
        })
            using (var raw = BridgeCanonicalJson.WriteRaw(static (writer, value) => writer.WriteStringValue(value), input))
                Require(Encoding.UTF8.GetString(raw.Written) == output, $"An ill-formed .NET string was written as {Encoding.UTF8.GetString(raw.Written)}.");
        foreach (var (input, output) in new (byte[], string)[]
        {
            ([0x61, 0xFF, 0x62], "\"a\\ufffdb\""), ([0xC3, 0xA9, 0xC3, 0x0A], "\"é\\ufffd\\n\""),
            ([0xF0, 0x9F, 0x98, 0x80, 0xED, 0xA0, 0x80], "\"😀\\ufffd\\ufffd\\ufffd\""),
        })
            using (var raw = BridgeCanonicalJson.WriteRaw(static (writer, value) => writer.WriteStringValue(value), input))
                Require(Encoding.UTF8.GetString(raw.Written) == output, $"Ill-formed UTF-8 was written as {Encoding.UTF8.GetString(raw.Written)}.");
    }

    private static Type ExceptionFor(string? code) => code switch
    {
        "invalid-json" or "depth-exceeded" => typeof(JsonException),
        "duplicate-member" or "lone-surrogate" or "invalid-utf8" or "number-out-of-range" => typeof(FormatException),
        _ => throw new InvalidOperationException($"Unknown canonical error code {code}."),
    };

    private static byte[] Bytes(JsonElement value)
    {
        var text = value.GetProperty("value").GetString()!;
        return value.GetProperty("encoding").GetString() switch
        {
            "utf8" => Encoding.UTF8.GetBytes(text),
            "base64" => Convert.FromBase64String(text),
            var encoding => throw new InvalidOperationException($"Unknown vector encoding {encoding}."),
        };
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
