using System.Collections.Generic;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

internal sealed record TodoSnapshot(
    IReadOnlyList<TodoItem> Items,
    int TotalCount,
    int RemainingCount,
    int CompletedCount,
    string Query,
    string Filter,
    string NewTitle,
    string NewNotes,
    string NewPriority,
    string? WizardStep,
    IReadOnlyList<string> WizardIssues,
    IReadOnlyList<string> ValidationMessages,
    IReadOnlyList<DiagnosticEntry> Diagnostics);
