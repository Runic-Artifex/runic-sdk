using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.MVVM;
using RunicToolkit.Samples.AdvancedTodo.Application;
using RunicToolkit.Samples.AdvancedTodo.Domain;
using AdvancedTodoViewModel = RunicToolkit.Samples.AdvancedTodo.UI.TodoViewModel;
using SimpleTodoViewModel = RunicToolkit.Samples.SimpleTodo.TodoViewModel;

namespace RunicToolkit.Samples.Todo.FrontendHost;

/// <summary>
/// Framework-neutral lifecycle gates run by every browser variant before its
/// native CsWebUi checks. Keeping them here proves both shared Todo contracts
/// without copying assertions into four frontend implementations.
/// </summary>
internal static class TodoFrontendQualityGates
{
    internal static async Task<int> RunManagedAsync(TodoDemo demo)
    {
        try
        {
            await VerifyRecoveryCancellationAndValidationAsync(demo).ConfigureAwait(false);
            VerifyDisposedGraphCanBeCollected(demo);
            Console.WriteLine(
                $"PASS: {demo} Todo authoritative recovery, cancellation, validation, and leak gates.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {demo} Todo managed quality gate.");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task VerifyRecoveryCancellationAndValidationAsync(TodoDemo demo)
    {
        string recoveryTitle = $"Recovery {Guid.NewGuid():N}";
        var contract = new MvvmContract(
            demo == TodoDemo.Simple ? TodoContracts.SimpleTodo.Name : TodoContracts.AdvancedTodo.Name);
        var registry = new MvvmSessionRegistry();
        if (demo == TodoDemo.Simple)
        {
            registry.Map(contract, static _ =>
            {
                var model = new SimpleTodoViewModel();
                return ValueTask.FromResult(
                    new MvvmSessionActivation(TodoContracts.SimpleTodo.CreateAdapter(model)));
            });
        }
        else
        {
            registry.Map(contract, static async cancellationToken =>
            {
                var model = new AdvancedTodoViewModel(new TodoService(new MemoryTodoRepository()));
                await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return new MvvmSessionActivation(
                    TodoContracts.AdvancedTodo.CreateAdapter(model),
                    model);
            });
        }

        await using IMvvmSessionFactory factory = registry.Build();
        await using IMvvmSession session = await factory.OpenAsync(contract).ConfigureAwait(false);

        int titleMember = demo == TodoDemo.Simple
            ? TodoContracts.SimpleTodo.Members.NewTitle
            : TodoContracts.AdvancedTodo.Members.NewTitle;
        int addMember = demo == TodoDemo.Simple
            ? TodoContracts.SimpleTodo.Members.Add
            : TodoContracts.AdvancedTodo.Members.Add;

        using (JsonDocument invalidValue = JsonDocument.Parse("\"x\""))
        {
            MvvmResponse invalid = await session.DispatchAsync(new MvvmMutationRequest(
                NewRequest(),
                MvvmMutationKind.SetProperty,
                session.Revision,
                titleMember,
                invalidValue.RootElement)).ConfigureAwait(false);
            Require(invalid.Succeeded, "The invalid draft mutation was not projected.");
            if (demo == TodoDemo.Advanced)
            {
                Require(
                    invalid.Patches.OfType<MvvmValidationPatch>().Any(patch =>
                        patch.MemberId == titleMember && patch.Errors.Count != 0),
                    "AdvancedTodo did not project validation errors.");
            }

            if (demo == TodoDemo.Simple)
            {
                MvvmResponse commandState = await session.DispatchAsync(
                    new MvvmSnapshotRequest(NewRequest())).ConfigureAwait(false);
                Require(commandState.Succeeded, "The validation snapshot failed.");
                JsonElement add = commandState.Payload!.Value
                    .GetProperty("members")
                    .EnumerateArray()
                    .Single(member =>
                        member.GetProperty("type").GetString() == "command" &&
                        member.GetProperty("member").GetInt32() == addMember);
                Require(
                    !add.GetProperty("canExecute").GetBoolean(),
                    "SimpleTodo kept Add executable for an invalid draft.");
            }
        }

        long beforeCancelledMutation = session.Revision;
        using (var cancelled = new CancellationTokenSource())
        using (JsonDocument cancelledValue = JsonDocument.Parse("\"must-not-commit\""))
        {
            cancelled.Cancel();
            MvvmResponse response = await session.DispatchAsync(
                new MvvmMutationRequest(
                    NewRequest(),
                    MvvmMutationKind.SetProperty,
                    session.Revision,
                    titleMember,
                    cancelledValue.RootElement),
                cancelled.Token).ConfigureAwait(false);
            Require(!response.Succeeded, "A pre-cancelled mutation unexpectedly succeeded.");
            Require(
                session.Revision == beforeCancelledMutation,
                "A pre-cancelled mutation changed the authoritative revision.");
        }

        using (JsonDocument recoveredValue = JsonDocument.Parse(
            JsonSerializer.Serialize(
                recoveryTitle,
                RunicToolkit.Samples.SimpleTodo.TodoJsonContext.Default.String)))
        {
            MvvmResponse changed = await session.DispatchAsync(new MvvmMutationRequest(
                NewRequest(),
                MvvmMutationKind.SetProperty,
                session.Revision,
                titleMember,
                recoveredValue.RootElement)).ConfigureAwait(false);
            Require(changed.Succeeded, "The recovery sentinel mutation failed.");
        }

        MvvmResponse recovery = await session.DispatchAsync(
            new MvvmSnapshotRequest(NewRequest())).ConfigureAwait(false);
        Require(recovery.Succeeded, "The authoritative recovery snapshot failed.");
        Require(
            recovery.Payload!.Value.GetRawText().Contains(recoveryTitle, StringComparison.Ordinal),
            "The authoritative recovery snapshot lost committed Todo state.");

        if (demo == TodoDemo.Advanced)
        {
            using JsonDocument noArgument = JsonDocument.Parse("null");
            MvvmResponse started = await session.DispatchAsync(new MvvmMutationRequest(
                NewRequest(),
                MvvmMutationKind.ExecuteCommand,
                session.Revision,
                TodoContracts.AdvancedTodo.Members.Import,
                noArgument.RootElement)).ConfigureAwait(false);
            Require(started.Succeeded, "The cancellable import did not start.");

            MvvmResponse stopped = await session.DispatchAsync(new MvvmMutationRequest(
                NewRequest(),
                MvvmMutationKind.ExecuteCommand,
                session.Revision,
                TodoContracts.AdvancedTodo.Members.CancelImport,
                noArgument.RootElement)).ConfigureAwait(false);
            Require(stopped.Succeeded, "The cancellable import did not stop cleanly.");

            MvvmResponse afterCancellation = await session.DispatchAsync(
                new MvvmSnapshotRequest(NewRequest())).ConfigureAwait(false);
            string snapshot = afterCancellation.Payload!.Value.GetRawText();
            Require(
                snapshot.Contains(
                    "Starter-task import was cancelled before persistence.",
                    StringComparison.Ordinal),
                "AdvancedTodo did not retain its safe cancellation outcome.");
            Require(
                !snapshot.Contains("Explore the guided creation flow", StringComparison.Ordinal),
                "A cancelled import partially persisted starter tasks.");
        }
    }

    private static void VerifyDisposedGraphCanBeCollected(TodoDemo demo)
    {
        WeakReference reference = demo == TodoDemo.Simple
            ? CreateDisposedSimpleGraph()
            : CreateDisposedAdvancedGraph();
        for (int attempt = 0; attempt < 3 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Require(!reference.IsAlive, "A disposed Todo adapter retained its ViewModel graph.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedSimpleGraph()
    {
        var model = new SimpleTodoViewModel();
        var reference = new WeakReference(model);
        var adapter = TodoContracts.SimpleTodo.CreateAdapter(model);
        adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedAdvancedGraph()
    {
        var model = new AdvancedTodoViewModel(new TodoService(new MemoryTodoRepository()));
        model.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var reference = new WeakReference(model);
        var adapter = TodoContracts.AdvancedTodo.CreateAdapter(model);
        adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        model.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return reference;
    }

    private static MvvmRequestId NewRequest() => new(Guid.NewGuid());

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MemoryTodoRepository : ITodoRepository
    {
        private IReadOnlyList<TodoItem> _items = [];

        public ValueTask<IReadOnlyList<TodoItem>> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_items);
        }

        public ValueTask SaveAsync(
            IReadOnlyList<TodoItem> items,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _items = items.ToArray();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            _items = [];
        }
    }
}
