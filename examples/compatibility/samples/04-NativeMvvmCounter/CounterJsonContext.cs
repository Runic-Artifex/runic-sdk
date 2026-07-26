using System.Text.Json.Serialization;

namespace WebUIToolkit.Samples.NativeMvvmCounter;

[JsonSerializable(typeof(int))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
