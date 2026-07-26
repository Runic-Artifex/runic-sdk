using System.Text.Json.Serialization;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Provides reflection-free JSON metadata for bound property values.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TodoState))]
internal sealed partial class TodoJsonContext : JsonSerializerContext;
