using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM.Workflows;
using WebUIToolkit.Samples.AdvancedTodo.Domain;
using WebUIToolkit.Samples.AdvancedTodo.Infrastructure;
using WebUIToolkit.Samples.AdvancedTodo.UI;

namespace WebUIToolkit.Samples.AdvancedTodo.Application;

internal static class SampleSmoke
{
    internal static async Task<int> RunAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-advanced-todo-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "todos.json");
        try
        {
            using var service = new TodoService(new JsonTodoRepository(path));
            await service.AddAsync(
                "Persisted task",
                "Written through the application service",
                TodoPriority.High,
                CancellationToken.None).ConfigureAwait(false);
            bool persisted = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Count == 1;

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool cancelled = false;
            try
            {
                await service.ImportStarterTasksAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            await using var flow = new TodoCreationFlow(static _ => { });
            _ = await flow.StartAsync(CancellationToken.None).ConfigureAwait(false);
            WorkflowTransition<TodoDraft> invalid = await flow
                .NextAsync(string.Empty, string.Empty, TodoPriority.Normal, CancellationToken.None)
                .ConfigureAwait(false);
            WorkflowTransition<TodoDraft> moved = await flow
                .NextAsync("Planned task", "Reviewed before save", TodoPriority.Normal, CancellationToken.None)
                .ConfigureAwait(false);
            WorkflowTransition<TodoDraft> finished = await flow
                .FinishAsync(CancellationToken.None)
                .ConfigureAwait(false);
            bool workflow =
                invalid.Kind == WorkflowTransitionKind.Stayed &&
                moved.Kind == WorkflowTransitionKind.Moved &&
                finished.Kind == WorkflowTransitionKind.Completed &&
                finished.Outcome?.Value?.Title == "Planned task";

            TodoItem item = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false))[0];
            var snapshot = new TodoSnapshot(
                [item],
                1,
                1,
                0,
                string.Empty,
                "All",
                string.Empty,
                string.Empty,
                "Normal",
                null,
                [],
                [],
                []);
            string browserJson = JsonSerializer.Serialize(
                snapshot,
                AdvancedTodoJsonContext.Default.TodoSnapshot);
            bool browserContract =
                browserJson.Contains("\"totalCount\": 1", StringComparison.Ordinal) &&
                browserJson.Contains("\"priority\": \"High\"", StringComparison.Ordinal);

            bool passed = persisted && cancelled && workflow && browserContract;
            Console.WriteLine(passed
                ? "Advanced ToDo self-test passed: persistence, cancellation, Flow, and browser contract."
                : "Advanced ToDo self-test failed.");
            return passed ? 0 : 1;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
