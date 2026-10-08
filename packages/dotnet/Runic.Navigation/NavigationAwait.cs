using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Runic.Navigation;

// The only ways the engine awaits (W240-001 §4.5). A task that user code or the model thread
// completes can resume the engine on the model thread, for example inside a click handler or
// a commit's PropertyChanged handler; continuing there could nest a commit turn inside it.
// Every await in Runic.Navigation therefore goes through AfterUserCode, which continues on the
// thread pool whenever the completing thread is executing in the model context, or through Hop,
// which always continues on the thread pool. NavigationSourceScanTests enforces this.
[Experimental(RunicNavigator.DiagnosticId)]
internal static class NavigationAwait
{
    // Always continues on the thread pool, ignoring the current SynchronizationContext
    // (unlike Task.Yield, which posts back to it).
    public static ConfiguredTaskAwaitable Hop() =>
        Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    public static ConfiguredValueTaskAwaitable AfterUserCode(this RunicNavigator navigator, ValueTask pending) =>
        AfterAsync(navigator.ModelContext, pending).ConfigureAwait(false);

    public static ConfiguredValueTaskAwaitable AfterUserCode(this RunicNavigator navigator, Task pending) =>
        AfterAsync(navigator.ModelContext, new ValueTask(pending)).ConfigureAwait(false);

    public static ConfiguredValueTaskAwaitable<T> AfterUserCode<T>(this RunicNavigator navigator, ValueTask<T> pending) =>
        AfterAsync(navigator.ModelContext, pending).ConfigureAwait(false);

    public static ConfiguredValueTaskAwaitable<T> AfterUserCode<T>(this RunicNavigator navigator, Task<T> pending) =>
        AfterAsync(navigator.ModelContext, new ValueTask<T>(pending)).ConfigureAwait(false);

    private static async ValueTask AfterAsync(IRunicModelContext context, ValueTask pending)
    {
        try { await pending.ConfigureAwait(false); }
        finally
        {
            // Conditional: a pool thread never executes in the context, so contexts without
            // thread affinity (RunicModelContext) pay no extra dispatch.
            if (context.IsExecuting) await Hop();
        }
    }

    private static async ValueTask<T> AfterAsync<T>(IRunicModelContext context, ValueTask<T> pending)
    {
        try { return await pending.ConfigureAwait(false); }
        finally
        {
            if (context.IsExecuting) await Hop();
        }
    }
}
