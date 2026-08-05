using System.Text.Json.Serialization;

namespace RunicToolkitStarter;

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
