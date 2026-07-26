using System.Text.Json.Serialization;

namespace WebUIToolkit.Samples.MvvmCounterProjection;

[JsonSerializable(typeof(int))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
