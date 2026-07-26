using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>
/// Translates four explicit CsWebUi bindings into ordered WebUIToolkit MVVM requests.
/// </summary>
internal sealed class TodoBackend : IAsyncDisposable
{
    internal static readonly MvvmContract Contract = new("samples.simple-todo");

    private readonly SemaphoreSlim gate = new(1, 1);
    private TodoViewModel? model;
    private IMvvmSessionFactory? sessionFactory;
    private IMvvmSession? session;

    internal async ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                return;
            }

            model = new TodoViewModel();
            var registry = new MvvmSessionRegistry();
            registry.Map(Contract, _ =>
            {
                CommunityToolkitMvvmBindingAdapter<TodoViewModel> adapter =
                    new CommunityToolkitMvvmAdapterBuilder<TodoViewModel>(model)
                        .BindProperty(
                            1,
                            nameof(TodoViewModel.NewTitle),
                            static state => state.NewTitle,
                            static (state, value) => state.NewTitle = value,
                            TodoJsonContext.Default.String,
                            includeValidation: true)
                        .BindProperty(
                            2,
                            nameof(TodoViewModel.SelectedId),
                            static state => state.SelectedId,
                            static (state, value) => state.SelectedId = value,
                            TodoJsonContext.Default.String)
                        .BindCommand(3, nameof(TodoViewModel.AddCommand), static state => state.AddCommand)
                        .BindCommand(4, nameof(TodoViewModel.ToggleCommand), static state => state.ToggleCommand)
                        .BindCommand(5, nameof(TodoViewModel.RemoveCommand), static state => state.RemoveCommand)
                        .Build();
                return ValueTask.FromResult(new MvvmSessionActivation(adapter));
            });

            sessionFactory = registry.Build();
            session = await sessionFactory.OpenAsync(Contract, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask DeactivateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                await sessionFactory!.CloseAsync(session.Id).ConfigureAwait(false);
                session = null;
            }

            if (sessionFactory is not null)
            {
                await sessionFactory.DisposeAsync().ConfigureAwait(false);
                sessionFactory = null;
            }

            model = null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<string> SnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            MvvmResponse response = await session!.DispatchAsync(
                new MvvmSnapshotRequest(new MvvmRequestId(Guid.NewGuid())),
                cancellationToken).ConfigureAwait(false);
            return SerializeState(response.Succeeded ? null : "Could not read the task list.");
        }
        finally
        {
            gate.Release();
        }
    }

    internal ValueTask<string> AddAsync(string title, CancellationToken cancellationToken) =>
        MutateAsync(
            propertyMemberId: 1,
            commandMemberId: 3,
            title,
            "Give the task a name between 2 and 80 characters.",
            cancellationToken);

    internal ValueTask<string> ToggleAsync(string id, CancellationToken cancellationToken) =>
        MutateAsync(
            propertyMemberId: 2,
            commandMemberId: 4,
            id,
            "That task is no longer available.",
            cancellationToken);

    internal ValueTask<string> RemoveAsync(string id, CancellationToken cancellationToken) =>
        MutateAsync(
            propertyMemberId: 2,
            commandMemberId: 5,
            id,
            "That task is no longer available.",
            cancellationToken);

    internal async Task<int> RunSmokeTestAsync()
    {
        await ActivateAsync(CancellationToken.None);
        string initial = await SnapshotAsync(CancellationToken.None);
        string invalid = await AddAsync("x", CancellationToken.None);
        string added = await AddAsync("Run the desktop sample", CancellationToken.None);
        string? id = model!.Items.FirstOrDefault(
            static item => item.Title == "Run the desktop sample")?.Id.ToString("D");
        string toggled = id is null
            ? string.Empty
            : await ToggleAsync(id, CancellationToken.None);
        string removed = id is null
            ? string.Empty
            : await RemoveAsync(id, CancellationToken.None);
        bool passed =
            initial.Contains("\"remaining\":3", StringComparison.Ordinal) &&
            invalid.Contains(
                "Give the task a name between 2 and 80 characters.",
                StringComparison.Ordinal) &&
            added.Contains("Run the desktop sample", StringComparison.Ordinal) &&
            toggled.Contains("\"completed\":1", StringComparison.Ordinal) &&
            !removed.Contains("Run the desktop sample", StringComparison.Ordinal);
        Console.WriteLine(passed
            ? "SimpleTodo MVVM smoke test passed."
            : "SimpleTodo MVVM smoke test failed.");
        await DeactivateAsync(CancellationToken.None);
        return passed ? 0 : 1;
    }

    public async ValueTask DisposeAsync()
    {
        await DeactivateAsync(CancellationToken.None);
        gate.Dispose();
    }

    private async ValueTask<string> MutateAsync(
        int propertyMemberId,
        int commandMemberId,
        string value,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            JsonElement propertyValue =
                JsonSerializer.SerializeToElement(value, TodoJsonContext.Default.String);
            MvvmResponse property = await session!.DispatchAsync(
                new MvvmMutationRequest(
                    new MvvmRequestId(Guid.NewGuid()),
                    MvvmMutationKind.SetProperty,
                    session.Revision,
                    propertyMemberId,
                    propertyValue),
                cancellationToken).ConfigureAwait(false);
            if (!property.Succeeded)
            {
                return SerializeState(failureMessage);
            }

            using JsonDocument emptyParameter = JsonDocument.Parse("null");
            MvvmResponse command = await session.DispatchAsync(
                new MvvmMutationRequest(
                    new MvvmRequestId(Guid.NewGuid()),
                    MvvmMutationKind.ExecuteCommand,
                    session.Revision,
                    commandMemberId,
                    emptyParameter.RootElement),
                cancellationToken).ConfigureAwait(false);
            return SerializeState(command.Succeeded ? null : failureMessage);
        }
        finally
        {
            gate.Release();
        }
    }

    private string SerializeState(string? error)
    {
        TodoViewModel current = model!;
        var state = new TodoState(
            current.Items
                .Select(static item => new TodoItemState(
                    item.Id.ToString("D"),
                    item.Title,
                    item.IsCompleted))
                .ToArray(),
            current.NewTitle,
            current.Items.Count - current.CompletedCount,
            current.CompletedCount,
            error);
        return JsonSerializer.Serialize(state, TodoJsonContext.Default.TodoState);
    }

    private void EnsureActive()
    {
        if (session is null || model is null)
        {
            throw new InvalidOperationException("The todo session is not active.");
        }
    }
}

/// <summary>The JSON-safe task shape returned to the local browser UI.</summary>
internal sealed record TodoItemState(string Id, string Title, bool IsCompleted);

/// <summary>The complete authoritative page state returned after every action.</summary>
internal sealed record TodoState(
    TodoItemState[] Items,
    string Draft,
    int Remaining,
    int Completed,
    string? Error);
