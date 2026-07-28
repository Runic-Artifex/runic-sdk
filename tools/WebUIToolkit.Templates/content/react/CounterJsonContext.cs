using System.Text.Json.Serialization;

namespace WebUIToolkitStarter;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
