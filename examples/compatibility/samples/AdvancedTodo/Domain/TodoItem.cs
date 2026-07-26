using System;

namespace WebUIToolkit.Samples.AdvancedTodo.Domain;

internal enum TodoPriority
{
    Low,
    Normal,
    High,
}

internal sealed record TodoItem(
    Guid Id,
    string Title,
    string Notes,
    TodoPriority Priority,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
