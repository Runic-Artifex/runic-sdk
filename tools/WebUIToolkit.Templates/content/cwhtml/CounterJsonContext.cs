using System.Text.Json.Serialization;

namespace WebUIToolkitStarter;

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
