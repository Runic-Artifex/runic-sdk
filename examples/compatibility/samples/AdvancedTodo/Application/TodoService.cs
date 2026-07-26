using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.Application;

internal sealed class TodoService : IDisposable
{
    private readonly ITodoRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal TodoService(ITodoRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    internal ValueTask<IReadOnlyList<TodoItem>> GetAsync(CancellationToken cancellationToken) =>
        _repository.LoadAsync(cancellationToken);

    internal async ValueTask AddAsync(
        string title,
        string notes,
        TodoPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await MutateAsync(
            items =>
            {
                items.Add(new TodoItem(
                    Guid.NewGuid(),
                    title.Trim(),
                    notes.Trim(),
                    priority,
                    IsCompleted: false,
                    DateTimeOffset.UtcNow,
                    CompletedAt: null));
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ToggleAsync(Guid id, CancellationToken cancellationToken)
    {
        await MutateAsync(
            items =>
            {
                int index = items.FindIndex(item => item.Id == id);
                if (index < 0)
                {
                    return;
                }

                TodoItem item = items[index];
                bool completed = !item.IsCompleted;
                items[index] = item with
                {
                    IsCompleted = completed,
                    CompletedAt = completed ? DateTimeOffset.UtcNow : null,
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await MutateAsync(
            items => items.RemoveAll(item => item.Id == id),
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ClearCompletedAsync(CancellationToken cancellationToken)
    {
        await MutateAsync(
            items => items.RemoveAll(static item => item.IsCompleted),
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ImportStarterTasksAsync(CancellationToken cancellationToken)
    {
        // This delay stands in for a remote API. Cancellation happens before the
        // repository mutation, so an aborted HTTP request cannot partly import.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        await MutateAsync(
            items =>
            {
                if (items.Any(static item => item.Title == "Explore the guided creation flow"))
                {
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                items.AddRange(
                [
                    new TodoItem(
                        Guid.NewGuid(),
                        "Explore the guided creation flow",
                        "Open the planner and move through Details and Review.",
                        TodoPriority.High,
                        false,
                        now,
                        null),
                    new TodoItem(
                        Guid.NewGuid(),
                        "Try search and filters",
                        "Every interaction crosses a named cs-webui binding into a retained MVVM session.",
                        TodoPriority.Normal,
                        false,
                        now.AddMilliseconds(1),
                        null),
                ]);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MutateAsync(
        Action<List<TodoItem>> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<TodoItem> snapshot = await _repository
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            List<TodoItem> working = [.. snapshot];
            mutation(working);
            await _repository.SaveAsync(working, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _repository.Dispose();
        _gate.Dispose();
    }
}
