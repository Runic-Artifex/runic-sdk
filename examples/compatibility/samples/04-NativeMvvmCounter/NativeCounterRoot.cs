using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.Hosting.CsWebUi.Mvvm;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.MVVM;
using RunicToolkit.MVVM.CommunityToolkit;

namespace RunicToolkit.Samples.NativeMvvmCounter;

/// <summary>
/// Joins the WebUiModeRunner lifecycle to one retained MVVM session and native bridge.
/// </summary>
internal sealed class NativeCounterRoot : IRootSessionFactory, IAsyncDisposable
{
    internal static readonly MvvmContract Contract = new("samples.native-mvvm-counter");

    private readonly IMvvmSessionFactory sessions;
    private WebUiWindow? window;

    internal NativeCounterRoot()
    {
        var registry = new MvvmSessionRegistry();
        registry.Map(Contract, static _ =>
        {
            var model = new CounterViewModel();
            CommunityToolkitMvvmBindingAdapter<CounterViewModel> adapter =
                new CommunityToolkitMvvmAdapterBuilder<CounterViewModel>(model)
                    .BindProperty(
                        1,
                        nameof(CounterViewModel.Count),
                        static state => state.Count,
                        static (state, value) => state.Count = value,
                        CounterJsonContext.Default.Int32)
                    .BindCommand(
                        2,
                        nameof(CounterViewModel.IncrementCommand),
                        static state => state.IncrementCommand)
                    .Build();
            return ValueTask.FromResult(new MvvmSessionActivation(adapter));
        });
        sessions = registry.Build();
    }

    internal void ConfigureWindow(WebUiWindow nativeWindow)
    {
        ArgumentNullException.ThrowIfNull(nativeWindow);
        if (Interlocked.CompareExchange(ref window, nativeWindow, null) is not null)
        {
            throw new InvalidOperationException("The sample supports one native window.");
        }
    }

    public async ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        WebUiWindow selectedWindow = window ??
            throw new InvalidOperationException("CsWebUi must create the window before the root session opens.");
        IMvvmSession session = await sessions.OpenAsync(Contract, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new RootSession(CsWebUiMvvmBridge.Attach(selectedWindow, session));
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        await using IMvvmSession session = await sessions.OpenAsync(Contract);
        MvvmResponse initial = await session.DispatchAsync(
            new MvvmSnapshotRequest(NewRequest()));
        using JsonDocument nullArgument = JsonDocument.Parse("null");
        MvvmResponse incremented = await session.DispatchAsync(
            new MvvmMutationRequest(
                NewRequest(),
                MvvmMutationKind.ExecuteCommand,
                session.Revision,
                2,
                nullArgument.RootElement));
        bool passed =
            initial.Succeeded &&
            incremented.Succeeded &&
            incremented.Patches.Count != 0 &&
            incremented.Revision == 1;
        Console.WriteLine(passed
            ? "Native MVVM counter smoke test passed."
            : "Native MVVM counter smoke test failed.");
        return passed ? 0 : 1;
    }

    public ValueTask DisposeAsync() => sessions.DisposeAsync();

    private static MvvmRequestId NewRequest() => new(Guid.NewGuid());

    private sealed class RootSession(CsWebUiMvvmBridge bridge) : IRootSession
    {
        private CsWebUiMvvmBridge? bridge = bridge;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            CsWebUiMvvmBridge? owned = Interlocked.Exchange(ref bridge, null);
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
