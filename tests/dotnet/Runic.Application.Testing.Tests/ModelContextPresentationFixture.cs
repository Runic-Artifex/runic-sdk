using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// The template's shape without its former manual model-context Bind (#132): the
// window binds the root and every page it presents, including a page the shell
// creates in a command.
public sealed partial class ContextShellWindow
    : RunicWindow<ContextShellViewModel>;

public sealed partial class ContextPageView : ReactiveRunicView<ContextPageViewModel>;

public sealed class ContextShellViewModel : ReactiveObject, IDisposable
{
    private ContextPageViewModel? _main;

    public ContextShellViewModel(ContextPageViewModel welcome, ISequencer scheduler)
    {
        _main = welcome;
        OpenCommand = ReactiveCommand.Create(() => { Main = new ContextPageViewModel(scheduler); }, scheduler);
    }

    public ContextPageViewModel? Main
    {
        get => _main;
        private set => this.RaiseAndSetIfChanged(ref _main, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    public void Dispose() => OpenCommand.Dispose();
}

public sealed class ContextPageViewModel : ReactiveObject, IDisposable
{
    private int _count;

    public ContextPageViewModel(ISequencer scheduler) =>
        IncrementCommand = ReactiveCommand.Create(() => { Count++; }, scheduler);

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public void Dispose() => IncrementCommand.Dispose();
}

internal sealed class ContextPageViewLocator : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>() where TView : class, IRunicView where TViewModel : class =>
        typeof(TView) == typeof(ContextPageView)
            ? (TView)(object)new ContextPageView()
            : throw new InvalidOperationException($"Unexpected View {typeof(TView).Name}.");
}
