using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace StatusIconSample;

public sealed class CounterViewModel : ReactiveObject, IDisposable
{
    private int _count;
    private static int _disposals;

    public static int Disposals => Volatile.Read(ref _disposals);

    // No scheduler argument: AddRunicReactiveModelContext makes ReactiveUI deliver the
    // command's notifications on the window's model context.
    public CounterViewModel() => IncrementCommand = ReactiveCommand.Create(() => { Count++; });

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
