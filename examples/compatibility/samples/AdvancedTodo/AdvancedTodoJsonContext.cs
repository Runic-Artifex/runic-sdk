using System.Collections.Generic;
using System.Text.Json.Serialization;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(IReadOnlyList<TodoItem>))]
[JsonSerializable(typeof(UI.TodoSnapshot))]
internal sealed partial class AdvancedTodoJsonContext : JsonSerializerContext;
