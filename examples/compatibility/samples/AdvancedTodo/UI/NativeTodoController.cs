using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.Samples.AdvancedTodo.Application;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

internal sealed class NativeTodoController : IRootSessionFactory, IAsyncDisposable
{
    private static readonly MvvmContract Contract = new("samples.advanced-todo");

    private readonly TodoViewModel _model;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private IMvvmSessionFactory? _sessionFactory;
    private IMvvmSession? _session;
    private MvvmRequestId? _runningImport;
    private int _opened;
    private int _closed;

    internal NativeTodoController(TodoService service)
    {
        _model = new TodoViewModel(service);
    }

    internal void ConfigureWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.BindAsync("todoSnapshot", SnapshotAsync);
        window.BindAsync("todoAdd", AddAsync);
        window.BindAsync("todoFilter", FilterAsync);
        window.BindAsync("todoToggle", ToggleAsync);
        window.BindAsync("todoDelete", DeleteAsync);
        window.BindAsync("todoClearCompleted", ClearCompletedAsync);
        window.BindAsync("todoImport", ImportAsync);
        window.BindAsync("todoCancelImport", CancelImportAsync);
        window.BindAsync("todoWizardStart", WizardStartAsync);
        window.BindAsync("todoWizardNext", WizardNextAsync);
        window.BindAsync("todoWizardBack", WizardBackAsync);
        window.BindAsync("todoWizardFinish", WizardFinishAsync);
        window.BindAsync("todoWizardCancel", WizardCancelAsync);
    }

    public async ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _opened, 1) != 0)
        {
            throw new InvalidOperationException("The advanced ToDo root can be opened only once.");
        }

        try
        {
            await _model.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var registry = new MvvmSessionRegistry();
            registry.Map(
                Contract,
                _ => ValueTask.FromResult(
                    new MvvmSessionActivation(
                        new CommunityToolkitMvvmAdapterBuilder<TodoViewModel>(_model)
                            .BindProperty(
                                1,
                                nameof(TodoViewModel.NewTitle),
                                static model => model.NewTitle,
                                static (model, value) => model.NewTitle = value,
                                AdvancedTodoJsonContext.Default.String,
                                includeValidation: true)
                            .BindProperty(
                                2,
                                nameof(TodoViewModel.NewNotes),
                                static model => model.NewNotes,
                                static (model, value) => model.NewNotes = value,
                                AdvancedTodoJsonContext.Default.String)
                            .BindProperty(
                                3,
                                nameof(TodoViewModel.NewPriority),
                                static model => model.NewPriority,
                                static (model, value) => model.NewPriority = value,
                                AdvancedTodoJsonContext.Default.String)
                            .BindProperty(
                                4,
                                nameof(TodoViewModel.Query),
                                static model => model.Query,
                                static (model, value) => model.Query = value,
                                AdvancedTodoJsonContext.Default.String)
                            .BindProperty(
                                5,
                                nameof(TodoViewModel.Filter),
                                static model => model.Filter,
                                static (model, value) => model.Filter = value,
                                AdvancedTodoJsonContext.Default.String)
                            .BindProperty(
                                6,
                                nameof(TodoViewModel.SelectedId),
                                static model => model.SelectedId,
                                static (model, value) => model.SelectedId = value,
                                AdvancedTodoJsonContext.Default.String)
                            .BindAsyncCommand(
                                101,
                                nameof(TodoViewModel.AddCommand),
                                static model => model.AddCommand)
                            .BindCommand(
                                102,
                                nameof(TodoViewModel.ApplyFilterCommand),
                                static model => model.ApplyFilterCommand)
                            .BindAsyncCommand(
                                103,
                                nameof(TodoViewModel.ToggleCommand),
                                static model => model.ToggleCommand)
                            .BindAsyncCommand(
                                104,
                                nameof(TodoViewModel.DeleteCommand),
                                static model => model.DeleteCommand)
                            .BindAsyncCommand(
                                105,
                                nameof(TodoViewModel.ClearCompletedCommand),
                                static model => model.ClearCompletedCommand)
                            .BindAsyncCommand(
                                106,
                                nameof(TodoViewModel.ImportCommand),
                                static model => model.ImportCommand)
                            .BindAsyncCommand(
                                107,
                                nameof(TodoViewModel.StartWizardCommand),
                                static model => model.StartWizardCommand)
                            .BindAsyncCommand(
                                108,
                                nameof(TodoViewModel.WizardNextCommand),
                                static model => model.WizardNextCommand)
                            .BindAsyncCommand(
                                109,
                                nameof(TodoViewModel.WizardBackCommand),
                                static model => model.WizardBackCommand)
                            .BindAsyncCommand(
                                110,
                                nameof(TodoViewModel.WizardFinishCommand),
                                static model => model.WizardFinishCommand)
                            .BindAsyncCommand(
                                111,
                                nameof(TodoViewModel.WizardCancelCommand),
                                static model => model.WizardCancelCommand)
                            .Build())));

            _sessionFactory = registry.Build();
            _session = await _sessionFactory
                .OpenAsync(Contract, cancellationToken)
                .ConfigureAwait(false);
            return new RootSession(this);
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask<WebUiResult> SnapshotAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(static (_, _) => ValueTask.CompletedTask, cancellationToken);

    private ValueTask<WebUiResult> AddAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            async (controller, token) =>
            {
                RequireArguments(eventData, 3);
                await controller.SetAsync(1, Text(eventData, 0, 120), token).ConfigureAwait(false);
                await controller.SetAsync(2, Text(eventData, 1, 2_000), token).ConfigureAwait(false);
                await controller.SetAsync(3, Text(eventData, 2, 16), token).ConfigureAwait(false);
                await controller.ExecuteAsync(101, token).ConfigureAwait(false);
            },
            cancellationToken);

    private ValueTask<WebUiResult> FilterAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            async (controller, token) =>
            {
                RequireArguments(eventData, 2);
                await controller.SetAsync(4, Text(eventData, 0, 120), token).ConfigureAwait(false);
                await controller.SetAsync(5, Text(eventData, 1, 16), token).ConfigureAwait(false);
                await controller.ExecuteAsync(102, token).ConfigureAwait(false);
            },
            cancellationToken);

    private ValueTask<WebUiResult> ToggleAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        ItemCommandAsync(eventData, 103, cancellationToken);

    private ValueTask<WebUiResult> DeleteAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        ItemCommandAsync(eventData, 104, cancellationToken);

    private ValueTask<WebUiResult> ClearCompletedAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            static (controller, token) => controller.ExecuteAsync(105, token),
            cancellationToken);

    private async ValueTask<WebUiResult> ImportAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken)
    {
        await _calls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IMvvmSession session = RequireSession();
            var requestId = new MvvmRequestId(Guid.NewGuid());
            _runningImport = requestId;
            using JsonDocument payload = JsonDocument.Parse("null");
            _ = await session.DispatchAsync(
                    new MvvmMutationRequest(
                        requestId,
                        MvvmMutationKind.ExecuteCommand,
                        session.Revision,
                        106,
                        payload.RootElement),
                    cancellationToken)
                .ConfigureAwait(false);
            return JsonResult();
        }
        finally
        {
            _runningImport = null;
            _calls.Release();
        }
    }

    private async ValueTask<WebUiResult> CancelImportAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken)
    {
        if (_runningImport is { } target)
        {
            _ = await RequireSession()
                .DispatchAsync(
                    new MvvmCancelRequest(new MvvmRequestId(Guid.NewGuid()), target),
                    cancellationToken)
                .ConfigureAwait(false);
            await _calls.WaitAsync(cancellationToken).ConfigureAwait(false);
            _calls.Release();
        }

        return JsonResult();
    }

    private ValueTask<WebUiResult> WizardStartAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            static (controller, token) => controller.ExecuteAsync(107, token),
            cancellationToken);

    private ValueTask<WebUiResult> WizardNextAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            async (controller, token) =>
            {
                RequireArguments(eventData, 3);
                await controller.SetAsync(1, Text(eventData, 0, 120), token).ConfigureAwait(false);
                await controller.SetAsync(2, Text(eventData, 1, 2_000), token).ConfigureAwait(false);
                await controller.SetAsync(3, Text(eventData, 2, 16), token).ConfigureAwait(false);
                await controller.ExecuteAsync(108, token).ConfigureAwait(false);
            },
            cancellationToken);

    private ValueTask<WebUiResult> WizardBackAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            static (controller, token) => controller.ExecuteAsync(109, token),
            cancellationToken);

    private ValueTask<WebUiResult> WizardFinishAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            static (controller, token) => controller.ExecuteAsync(110, token),
            cancellationToken);

    private ValueTask<WebUiResult> WizardCancelAsync(
        WebUiEvent eventData,
        CancellationToken cancellationToken) =>
        LockedAsync(
            static (controller, token) => controller.ExecuteAsync(111, token),
            cancellationToken);

    private ValueTask<WebUiResult> ItemCommandAsync(
        WebUiEvent eventData,
        int commandId,
        CancellationToken cancellationToken) =>
        LockedAsync(
            async (controller, token) =>
            {
                RequireArguments(eventData, 1);
                await controller.SetAsync(6, Text(eventData, 0, 64), token).ConfigureAwait(false);
                await controller.ExecuteAsync(commandId, token).ConfigureAwait(false);
            },
            cancellationToken);

    private async ValueTask<WebUiResult> LockedAsync(
        Func<NativeTodoController, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        await _calls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(this, cancellationToken).ConfigureAwait(false);
            return JsonResult();
        }
        finally
        {
            _calls.Release();
        }
    }

    private async ValueTask SetAsync(
        int memberId,
        string value,
        CancellationToken cancellationToken)
    {
        IMvvmSession session = RequireSession();
        JsonElement payload = JsonSerializer.SerializeToElement(
            value,
            AdvancedTodoJsonContext.Default.String);
        MvvmResponse response = await session.DispatchAsync(
                new MvvmMutationRequest(
                    new MvvmRequestId(Guid.NewGuid()),
                    MvvmMutationKind.SetProperty,
                    session.Revision,
                    memberId,
                    payload),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(response);
    }

    private async ValueTask ExecuteAsync(
        int memberId,
        CancellationToken cancellationToken)
    {
        IMvvmSession session = RequireSession();
        using JsonDocument payload = JsonDocument.Parse("null");
        MvvmResponse response = await session.DispatchAsync(
                new MvvmMutationRequest(
                    new MvvmRequestId(Guid.NewGuid()),
                    MvvmMutationKind.ExecuteCommand,
                    session.Revision,
                    memberId,
                    payload.RootElement),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(response);
    }

    private WebUiResult JsonResult() =>
        WebUiResult.FromString(
            JsonSerializer.Serialize(_model.Snapshot(), AdvancedTodoJsonContext.Default.TodoSnapshot));

    private static void EnsureSucceeded(MvvmResponse response)
    {
        if (!response.Succeeded)
        {
            throw new InvalidOperationException(
                $"The MVVM session rejected an operation with '{response.Fault?.Code ?? "unknown"}'.");
        }
    }

    private IMvvmSession RequireSession() =>
        _session ?? throw new InvalidOperationException("The MVVM root session is not active.");

    private static string Text(WebUiEvent eventData, nuint index, int maximumLength)
    {
        string value = eventData.GetString(index);
        return value.Length <= maximumLength
            ? value
            : throw new ArgumentException("A browser value exceeded its declared limit.");
    }

    private static void RequireArguments(WebUiEvent eventData, nuint expected)
    {
        if (eventData.ArgumentCount != expected)
        {
            throw new ArgumentException("The browser callback has an invalid argument count.");
        }
    }

    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        IMvvmSession? session = Interlocked.Exchange(ref _session, null);
        IMvvmSessionFactory? factory = Interlocked.Exchange(ref _sessionFactory, null);
        if (session is not null && factory is not null)
        {
            await factory.CloseAsync(session.Id).ConfigureAwait(false);
        }

        if (factory is not null)
        {
            await factory.DisposeAsync().ConfigureAwait(false);
        }

        await _model.DisposeAsync().ConfigureAwait(false);
        _calls.Dispose();
    }

    public ValueTask DisposeAsync() => CloseAsync();

    private sealed class RootSession(NativeTodoController owner) : IRootSession
    {
        private int _disposed;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.CloseAsync()
                : ValueTask.CompletedTask;
    }
}
