using System.Text.Json.Serialization;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Provides reflection-free JSON metadata for the closed MVVM bindings.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TodoItem))]
internal sealed partial class TodoJsonContext : JsonSerializerContext;
