using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using ReactiveUI.Validation.Reactive.Extensions;
using ReactiveUI.Validation.Reactive.Helpers;
using Runic.Application.Views;
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

        // #472 in the System.Reactive flavor: the page sends the key, the Bridge resolves the item.
        [RunicCommandKey(nameof(FindItem))]
        public ReactiveCommand<FlavorItem, Unit> RemoveCommand { get; } = ReactiveCommand.Create<FlavorItem>(item => item.Removed = true);

        public FlavorItem? FindItem(int id) => id == Item.Id ? Item : null;

        [RunicIgnore]
        public FlavorItem Item { get; } = new(7);
    }

    // No wire form: the Stream is not a Bridge value.
    public sealed class FlavorItem(int id)
    {
        public int Id { get; } = id;
        public bool Removed { get; set; }
        public System.IO.Stream? Data { get; init; }
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

    public sealed partial class FlavorShellWindow : RunicWindow<FlavorShellModel>
    {
    }

    // #478 in the System.Reactive flavor: two property rules and one object rule, read through the
    // ValidationContext so that each message appears once in the validation summary.
    public sealed class FlavorValidationModel : ReactiveValidationObject
    {
        public const string TooShort = "Password must be at least 7 characters long.";
        public const string Mismatch = "The confirmation does not match the new password.";
        public const string Common = "Choose a less common password.";

        private readonly BehaviorSubject<bool> _longEnough = new(true);
        private readonly BehaviorSubject<bool> _matches = new(true);
        private readonly BehaviorSubject<bool> _uncommon = new(true);

        public FlavorValidationModel()
        {
            this.ValidationRule(vm => vm.NewPassword, _longEnough, TooShort);
            this.ValidationRule(vm => vm.ConfirmPassword, _matches, Mismatch);
            this.ValidationRule(_uncommon, Common);
        }

        public string NewPassword
        {
            get;
            set
            {
                this.RaiseAndSetIfChanged(ref field, value);
                Revalidate();
            }
        } = string.Empty;

        public string ConfirmPassword
        {
            get;
            set
            {
                this.RaiseAndSetIfChanged(ref field, value);
                Revalidate();
            }
        } = string.Empty;

        private void Revalidate()
        {
            _longEnough.OnNext(NewPassword.Length >= 7);
            _matches.OnNext(NewPassword == ConfirmPassword);
            _uncommon.OnNext(NewPassword != "abc");
        }
    }

    public sealed partial class FlavorValidationWindow : RunicWindow<FlavorValidationModel>
    {
    }

    // #284 in the System.Reactive flavor: retiring the page's navigation entry cancels its operation, and the
    // navigator disposes the page only after the operation ended.
    public sealed class FlavorRetireShellModel : ReactiveObject
    {
        public FlavorRetireShellModel(Runic.Navigation.RunicNavigator navigator) =>
            Main = navigator.CreateRegion<FlavorRetirePageModel>(this);

        public Runic.Navigation.NavigationRegion<FlavorRetirePageModel> Main { get; }
    }

    public sealed partial class FlavorRetireShellWindow : RunicWindow<FlavorRetireShellModel>
    {
    }

    public sealed class FlavorRetirePageModel : ReactiveObject, IDisposable
    {
        private int _ended;

        public FlavorRetirePageModel(IScheduler commandScheduler)
        {
            UploadCommand = ReactiveCommand.CreateFromTask(async token =>
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Volatile.Write(ref _ended, 1); }
            }, outputScheduler: commandScheduler);
            // I-188: no ThrownExceptions subscriber. This work notices its cancellation only after the test released it,
            // so ReactiveUI reports the cancellation after the page's Bridge detached; the Bridge keeps observing the
            // command until then, so ReactiveUI's default handler, which ends the process, does not receive it.
            LateCommand = ReactiveCommand.CreateFromTask(async token =>
            {
                LateStarted.TrySetResult();
                await LateRelease.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }, outputScheduler: commandScheduler);
        }

        [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
        public ReactiveCommand<Unit, Unit> UploadCommand { get; }

        [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
        public ReactiveCommand<Unit, Unit> LateCommand { get; }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposed { get; private set; }
        internal bool EndedBeforeDispose { get; private set; }

        public void Dispose()
        {
            EndedBeforeDispose = Volatile.Read(ref _ended) == 1;
            Disposed++;
        }
    }

    public sealed partial class FlavorRetirePageView : ReactiveRunicView<FlavorRetirePageModel>
    {
    }
}
