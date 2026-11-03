using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>One message box the application showed and how it was answered.</summary>
/// <param name="MessageBox">The box as the application passed it.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Answer">The button chosen, or <see langword="null"/>.</param>
/// <param name="Scripted">Whether a scripted answer ended it; <see langword="false"/> when the queue was empty.</param>
public sealed record RunicMessageBoxCall(DesktopMessageBox MessageBox, RunicTestOutcome Outcome, DesktopMessageBoxButton? Answer, bool Scripted);

/// <summary>
/// Scripted <see cref="IDesktopMessageBoxes"/> for tests: each box takes the next queued answer and is recorded.
/// </summary>
/// <remarks>
/// <para>
/// Queue the button the user chooses, by its id, with <see cref="EnqueueAnswer"/> before the action that shows the box.
/// A box shown with an empty queue is answered as the user pressing Esc would: with its
/// <see cref="DesktopMessageBoxButtonRole.Cancel"/> button, or <see langword="null"/> when it has none. A scripted id
/// the box does not have throws <see cref="InvalidOperationException"/> from <see cref="ShowAsync"/>.
/// </para>
/// <para>
/// Boxes are checked with the rules of the real ones. When the test window closes, they report
/// <see cref="PlatformUnavailableReason.OwnerClosed"/>. The type is safe to use from any thread.
/// </para>
/// </remarks>
public sealed class RunicTestMessageBoxes : IDesktopMessageBoxes
{
    private static readonly IDesktopMessageBoxes s_checks = DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private readonly object _gate = new();
    private readonly Queue<Answer> _answers = new();
    private readonly List<RunicMessageBoxCall> _calls = [];
    private readonly RunicTestPlatform _platform;

    internal RunicTestMessageBoxes(RunicTestPlatform platform) => _platform = platform;

    /// <summary>The boxes shown so far, oldest first.</summary>
    public IReadOnlyList<RunicMessageBoxCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The number of answers still queued.</summary>
    public int Pending
    {
        get { lock (_gate) return _answers.Count; }
    }

    /// <summary>Queues the button the user chooses in the next box.</summary>
    /// <param name="buttonId">The <see cref="DesktopMessageBoxButton.Id"/>, or <see langword="null"/> for a box closed without a button.</param>
    public void EnqueueAnswer(string? buttonId) => Enqueue(new Answer(RunicTestOutcome.Selected, buttonId, null, null));

    /// <summary>Queues unavailability for the next box.</summary>
    /// <param name="reason">The reason it reports.</param>
    public void EnqueueUnavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Enqueue(new Answer(RunicTestOutcome.Unavailable, null, reason, null));
    }

    /// <summary>Queues a failure for the next box.</summary>
    /// <param name="code">The failure it reports.</param>
    public void EnqueueFailed(PlatformFailureCode code)
    {
        if (code == PlatformFailureCode.Unknown) throw new ArgumentOutOfRangeException(nameof(code), "Runic never reports Unknown.");
        Enqueue(new Answer(RunicTestOutcome.Failed, null, null, code));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        // The rules and the cancellation check of the real boxes.
        await s_checks.ShowAsync(messageBox, cancellationToken).ConfigureAwait(false);
        if (_platform.IsClosed)
            return Record(messageBox, RunicTestOutcome.Unavailable, null, false,
                new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed));
        _platform.Pump();
        bool scripted;
        Answer? answer;
        lock (_gate) scripted = _answers.TryDequeue(out answer);
        var buttons = messageBox.Buttons.IsDefault ? [] : messageBox.Buttons;
        switch (answer)
        {
            case null:
                var cancel = buttons.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Cancel);
                return Record(messageBox, RunicTestOutcome.Dismissed, cancel, false, new PlatformResult<DesktopMessageBoxButton?>.Success(cancel));
            case { Outcome: RunicTestOutcome.Unavailable, Reason: { } reason }:
                return Record(messageBox, RunicTestOutcome.Unavailable, null, scripted, new PlatformResult<DesktopMessageBoxButton?>.Unavailable(reason));
            case { Outcome: RunicTestOutcome.Failed, Code: { } code }:
                return Record(messageBox, RunicTestOutcome.Failed, null, scripted, new PlatformResult<DesktopMessageBoxButton?>.Failed(code));
            case { ButtonId: null }:
                return Record(messageBox, RunicTestOutcome.Dismissed, null, scripted, new PlatformResult<DesktopMessageBoxButton?>.Success(null));
            default:
                var chosen = buttons.FirstOrDefault(button => button.Id == answer.ButtonId)
                    ?? throw new InvalidOperationException(
                        $"The scripted answer '{answer.ButtonId}' is not a button of the message box '{messageBox.Message}', whose buttons are " +
                        $"{(buttons.IsEmpty ? "none" : string.Join(", ", buttons.Select(static button => $"'{button.Id}'")))}.");
                return Record(messageBox, RunicTestOutcome.Selected, chosen, scripted, new PlatformResult<DesktopMessageBoxButton?>.Success(chosen));
        }
    }

    internal void EnqueueScripted(string operation, IReadOnlyDictionary<string, string?> values)
    {
        switch (operation)
        {
            case "answer": EnqueueAnswer(values.GetValueOrDefault("button")); break;
            case "unavailable": EnqueueUnavailable(Enum.Parse<PlatformUnavailableReason>(RunicTestFileDialogs.Required(values, "reason"))); break;
            case "failed": EnqueueFailed(Enum.Parse<PlatformFailureCode>(RunicTestFileDialogs.Required(values, "code"))); break;
            default: throw new InvalidOperationException($"Unknown scripted message box operation '{operation}'.");
        }
    }

    private void Enqueue(Answer answer)
    {
        lock (_gate) _answers.Enqueue(answer);
    }

    private PlatformResult<DesktopMessageBoxButton?> Record(DesktopMessageBox box, RunicTestOutcome outcome, DesktopMessageBoxButton? answer,
        bool scripted, PlatformResult<DesktopMessageBoxButton?> result)
    {
        lock (_gate) _calls.Add(new RunicMessageBoxCall(box, outcome, answer, scripted));
        _platform.Record("messageBoxes", "show", [
            ("message", box.Message), ("title", box.Title), ("detail", box.Detail), ("kind", box.Kind.ToString()),
            ("outcome", outcome.ToString()), ("button", answer?.Id),
        ]);
        return result;
    }

    private sealed record Answer(RunicTestOutcome Outcome, string? ButtonId, PlatformUnavailableReason? Reason, PlatformFailureCode? Code);
}
