using System.Text.Json.Serialization;

namespace WebUIToolkitStarter;

[JsonSerializable(typeof(int))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
