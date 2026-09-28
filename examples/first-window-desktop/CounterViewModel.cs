using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace FirstWindowDesktop;

public sealed class CounterViewModel : ReactiveObject, IDisposable
{
    private readonly IRunicModelContextLease _modelContextLease;
    private int _count;
    private static int _disposals;

    public static int Disposals => Volatile.Read(ref _disposals);

    public CounterViewModel(IRunicModelContext modelContext)
    {
        _modelContextLease = RunicModelContextRegistry.Shared.Bind(modelContext, this);
        IncrementCommand = ReactiveCommand.Create(() => { Count++; },
            new RunicReactiveSchedulerProvider().For(modelContext));
    }

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public void Dispose()
    {
        IncrementCommand.Dispose();
        _modelContextLease.Dispose();
        Interlocked.Increment(ref _disposals);
    }
}
