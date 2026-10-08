using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

// The life of a PushForResult request. Changed only under the navigator's gate.
internal enum NavigationResultState
{
    // The push has not committed.
    Pending,

    // The push committed; the entry can complete.
    Active,

    // A return from the entry committed with a value; retirement completes the request.
    Completing,

    Completed,
    Dismissed,
}

// Why a request was dismissed (log 1070).
internal enum NavigationResultDismissal
{
    NotCommitted,
    Retired,
    Cancelled,
    Closed,
    // The entry called NavigationEntryContext.DismissAsync.
    Dismissed,
}

// Marks a Back that returns from a result entry: it may leave the region empty, and
// CompleteAsync stages the value that the commit turn accepts.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed record NavigationReturn(NavigationStagedResult? Result);

// The non-generic side of a PushForResult request. The source is completed only
// outside the gate, after the state became terminal.
[Experimental(RunicNavigator.DiagnosticId)]
internal abstract class NavigationResultRequestCore(NavigationRegionCore region, CancellationToken callerToken)
{
    public NavigationRegionCore Region { get; } = region;
    public CancellationToken CallerToken { get; } = callerToken;

    // Guarded by the navigator's gate.
    public NavigationResultState State { get; set; }
    public NavigationEntryCore? Entry { get; set; }
    public CancellationTokenRegistration Registration { get; set; }

    public abstract string ResultTypeName { get; }

    // Stages a value of another static type (a value type, which the contravariant sink can't
    // take) when it is a TResult at run time, or null for a nullable TResult; otherwise null.
    public abstract NavigationStagedResult? TryStageBoxed(object? value);

    // Completes the source with the staged value.
    public abstract void Complete();

    public abstract void Dismiss();
}

// Accepts a TResult, or any reference type assignable to it (contravariant), without reflection.
[Experimental(RunicNavigator.DiagnosticId)]
internal interface INavigationResultSink<in TResult>
{
    NavigationStagedResult Stage(TResult value);
}

// A value CompleteAsync offers. The commit turn of its return accepts it under the gate.
[Experimental(RunicNavigator.DiagnosticId)]
internal abstract class NavigationStagedResult(NavigationResultRequestCore request)
{
    public NavigationResultRequestCore Request { get; } = request;

    // Call under the gate: only an active request takes the value.
    public bool TryAccept()
    {
        if (Request.State != NavigationResultState.Active) return false;
        Store();
        Request.State = NavigationResultState.Completing;
        return true;
    }

    protected abstract void Store();
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationResultRequestCore<TResult>(NavigationRegionCore region, CancellationToken callerToken)
    : NavigationResultRequestCore(region, callerToken), INavigationResultSink<TResult>
{
    private readonly TaskCompletionSource<NavigationCompletion<TResult>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TResult _value = default!;

    public Task<NavigationCompletion<TResult>> Completion => _completion.Task;

    public override string ResultTypeName => typeof(TResult).Name;

    public NavigationStagedResult Stage(TResult value) => new Staged(this, value);

    public override NavigationStagedResult? TryStageBoxed(object? value) => value switch
    {
        TResult typed => Stage(typed),
        null when default(TResult) is null => Stage(default!),
        _ => null,
    };

    public override void Complete() => _completion.TrySetResult(new NavigationCompletion<TResult>.Completed(_value));

    public override void Dismiss() => _completion.TrySetResult(new NavigationCompletion<TResult>.Dismissed());

    private sealed class Staged(NavigationResultRequestCore<TResult> owner, TResult value) : NavigationStagedResult(owner)
    {
        protected override void Store() => owner._value = value;
    }
}
