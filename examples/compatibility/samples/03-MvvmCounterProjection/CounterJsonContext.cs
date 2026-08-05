using System.Text.Json.Serialization;

namespace RunicToolkit.Samples.MvvmCounterProjection;

[JsonSerializable(typeof(int))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
