using System.Collections.Generic;
using System.Text.Json.Serialization;
using RunicToolkit.Samples.AdvancedTodo.Domain;
using RunicToolkit.Samples.AdvancedTodo.UI;

namespace RunicToolkit.Samples.AdvancedTodo;

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
