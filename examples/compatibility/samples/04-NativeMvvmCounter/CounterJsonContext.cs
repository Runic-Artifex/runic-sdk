using System.Text.Json.Serialization;

namespace RunicToolkit.Samples.NativeMvvmCounter;

[JsonSerializable(typeof(int))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
