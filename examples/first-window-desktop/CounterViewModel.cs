using ReactiveUI;
using ReactiveUI.Primitives;

namespace FirstWindowDesktop;

public sealed class CounterViewModel : ReactiveObject, IDisposable
{
    private int _count;
    private static int _disposals;

    public static int Disposals => Volatile.Read(ref _disposals);

    public CounterViewModel() => IncrementCommand = ReactiveCommand.Create(() => { Count++; });

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public void Dispose() => Interlocked.Increment(ref _disposals);
}
