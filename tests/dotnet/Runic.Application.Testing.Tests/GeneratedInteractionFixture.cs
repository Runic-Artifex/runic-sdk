using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// This public shape is intentionally inspected by BridgeCodegen. It proves an
/// interaction is not merely usable through the hand-authored adapter API.
/// </summary>
public sealed record GeneratedInteractionRequest(string Title, int Attempt);

public sealed class GeneratedInteractionViewModel : ReactiveObject, IDisposable
{
    private int _attempt;
    private int _acceptedCount;

    public GeneratedInteractionViewModel()
    {
        AskCommand = ReactiveCommand.CreateFromTask(AskAsync);
        AskWithoutDeadlineCommand = ReactiveCommand.CreateFromTask(async () => { if (await ConfirmWithoutDeadline.Handle(new("Without a deadline", ++Attempt))) AcceptedCount++; });
        AskSlowlyCommand = ReactiveCommand.CreateFromTask(async () => { if (await ConfirmSlowly.Handle(new("Fifteen minutes", ++Attempt))) AcceptedCount++; });
    }

    public Interaction<GeneratedInteractionRequest, bool> Confirm { get; } = new();

    // sdk#1146: the generator passes these lifetimes to ReactiveInteractionDescriptor.Create.
    [RunicInteractionTimeout(Timeout.Infinite)]
    public Interaction<GeneratedInteractionRequest, bool> ConfirmWithoutDeadline { get; } = new();

    [RunicInteractionTimeout(15 * 60 * 1000)]
    public Interaction<GeneratedInteractionRequest, bool> ConfirmSlowly { get; } = new();

    public int Attempt { get => _attempt; private set => this.RaiseAndSetIfChanged(ref _attempt, value); }
    public int AcceptedCount { get => _acceptedCount; private set => this.RaiseAndSetIfChanged(ref _acceptedCount, value); }
    public ReactiveCommand<RxVoid, RxVoid> AskCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AskWithoutDeadlineCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AskSlowlyCommand { get; }

    private async Task AskAsync(CancellationToken cancellationToken)
    {
        var request = new GeneratedInteractionRequest("Generated request", ++Attempt);
        if (await Confirm.Handle(request)) AcceptedCount++;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        AskCommand.Dispose();
        AskWithoutDeadlineCommand.Dispose();
        AskSlowlyCommand.Dispose();
    }
}

public sealed partial class GeneratedInteractionWindow
    : RunicWindow<GeneratedInteractionViewModel>;
