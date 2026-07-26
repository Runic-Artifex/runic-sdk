using System.Collections.Generic;
using System.Text.Json.Serialization;
using WebUIToolkit.Samples.AdvancedTodo.Domain;
using WebUIToolkit.Samples.AdvancedTodo.UI;

namespace WebUIToolkit.Samples.AdvancedTodo;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(DiagnosticEntry))]
[JsonSerializable(typeof(AdvancedTodoState))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(IReadOnlyList<TodoItem>))]
internal sealed partial class AdvancedTodoJsonContext : JsonSerializerContext;
