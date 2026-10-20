using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

/// <summary>Bridged ReactiveUI commands without a ThrownExceptions subscription (W250-042).</summary>
public sealed class CommandLoggingViewModel : ReactiveObject
{
    private int _failures;

    public CommandLoggingViewModel(IRunicModelContext modelContext)
    {
        var scheduler = ReactiveSchedulers.For(modelContext);
        FailCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException($"fail {Interlocked.Increment(ref _failures)}");
        }, scheduler);
        DeclinedCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(async _ =>
        {
            await Task.Yield();
            throw new RunicFailureException(new NoteTitleRequired());
        }, scheduler);
    }

    public ReactiveCommand<RxVoid, RxVoid> FailCommand { get; }

    [RunicFailure(typeof(NoteSaveFailure))]
    public ReactiveCommand<RxVoid, RxVoid> DeclinedCommand { get; }
}

public sealed partial class CommandLoggingWindow : RunicWindow<CommandLoggingViewModel>;
