using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// An interaction whose output is a DTO: the generated client's output encoder
// returns an object literal from an arrow function.
public sealed class DtoInteractionViewModel : ReactiveObject, IDisposable
{
    private DtoListEntry? _picked;

    public DtoInteractionViewModel() =>
        PickCommand = ReactiveCommand.CreateFromTask(async () => { Picked = await ChooseEntry.Handle("Pick an entry"); });

    public Interaction<string, DtoListEntry> ChooseEntry { get; } = new();
    public DtoListEntry? Picked { get => _picked; private set => this.RaiseAndSetIfChanged(ref _picked, value); }
    public ReactiveCommand<RxVoid, RxVoid> PickCommand { get; }

    public void Dispose() => PickCommand.Dispose();
}

public sealed partial class DtoInteractionWindow(DtoInteractionViewModel model) : RunicWindow<DtoInteractionViewModel>(model);
