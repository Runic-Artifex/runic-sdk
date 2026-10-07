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
}
