using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using Runic.Application.Views.ReactiveUI.Reactive;

namespace ReactiveUiReactiveFlavorProof
{
    public sealed partial class FlavorModel : ReactiveObject
    {
        private readonly BehaviorSubject<bool> _disableAvailability = new(true);

        [Reactive]
        public partial string Name { get; set; }

        public FlavorModel(IScheduler commandScheduler)
        {
            CountCommand = ReactiveCommand.Create(() => 42, outputScheduler: commandScheduler);
            DisableCommand = ReactiveCommand.Create(
                () =>
                {
                    _disableAvailability.OnNext(false);
                    return 42;
                },
                _disableAvailability,
                commandScheduler);
        }

        public ReactiveCommand<Unit, int> CountCommand { get; }
        public ReactiveCommand<Unit, int> DisableCommand { get; }
    }

    public sealed partial class FlavorView : ReactiveRunicView<FlavorModel>
    {
    }

    // Creates and presents a child in its own command, without binding either
    // model to a context: the window does that (#132).
    public sealed partial class FlavorShellModel : ReactiveObject
    {
        private FlavorModel? _main;

        public FlavorShellModel(IScheduler commandScheduler) =>
            OpenCommand = ReactiveCommand.Create(() => { Main = new FlavorModel(commandScheduler) { Name = "page" }; },
                outputScheduler: commandScheduler);

        public FlavorModel? Main
        {
            get => _main;
            private set => this.RaiseAndSetIfChanged(ref _main, value);
        }

        public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    }

    public sealed partial class FlavorShellWindow(FlavorShellModel model) : ReactiveRunicWindow<FlavorShellModel>(model)
    {
    }
}
