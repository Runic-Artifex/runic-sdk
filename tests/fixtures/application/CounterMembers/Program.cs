using System.Text.Json;
#if EFFECT
using Codec = Runic.Application.Template.Contract.CounterBridgeContractCodec;
#else
using Codec = Runic.Application.Bridge.Generated.Module_47d92ca654f27069Codec;
#endif

foreach (JsonElement entry in JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "counter-members.json"))).RootElement.EnumerateArray())
{
    bool reject = entry.TryGetProperty("reject", out _);
    string name = entry.GetProperty("schema").GetString()!;
    JsonElement output;
    JsonElement input = entry.GetProperty("input");
    try
    {
#if EFFECT
        output = name == "IncrementCounter" ? Codec.EncodeIncrementCounter(Codec.DecodeIncrementCounter(input)) : Codec.EncodeCounterSnapshot(Codec.DecodeCounterSnapshot(input));
#else
        output = name == "IncrementCounter" ? Codec.EncodeWire_4948f5b6fa950720(Codec.DecodeWire_4948f5b6fa950720(input)) : Codec.EncodeWire_c271cbe6f2a472b1(Codec.DecodeWire_c271cbe6f2a472b1(input));
#endif

    }
    catch (Exception error) when (reject && error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { continue; }
        if (reject) throw new InvalidOperationException("Accepted invalid " + input);
        if (output.GetRawText() != entry.GetProperty("canonical").GetString()) throw new InvalidOperationException("Noncanonical " + output);
}
Console.WriteLine("PASS shared counter acceptance, rejection and canonical JSON.");
