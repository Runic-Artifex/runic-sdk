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
    // command's notifications on the window's model context. IRunicWindowPresentation is this Window as it appears on
    // the desktop, on every host: hiding it leaves the application in the status area, and the icon presents it again.
    public CounterViewModel(IRunicWindowPresentation window)
    {
        IncrementCommand = ReactiveCommand.Create(() => { Count++; });
        HideCommand = ReactiveCommand.CreateFromTask(async token => { await window.SetVisibleAsync(false, token); });
    }

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> HideCommand { get; }

    public void Dispose()
    {
        IncrementCommand.Dispose();
        HideCommand.Dispose();
        Interlocked.Increment(ref _disposals);
    }
}
