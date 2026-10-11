using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace FirstWindowDesktop;

public sealed class CounterViewModel : ReactiveObject, IDisposable
{
    private int _count;
    private static int _disposals;

    public static int Disposals => Volatile.Read(ref _disposals);

    public CounterViewModel(ISequencer scheduler)
    {
        IncrementCommand = ReactiveCommand.Create(() => { Count++; }, scheduler);
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
        Interlocked.Increment(ref _disposals);
    }
}
