using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The cases run in parallel (AssemblyInfo.cs), and several block a thread-pool thread until another work item releases
// it: a factory held by a ManualResetEventSlim, a synchronous call inside a model turn. Once the pool's minimum is busy
// it adds about one thread every half second, so on a 4-core runner such a release took longer than the case's limit
// (5 s) in every run of the suite. A minimum of 64 workers covers the cases that run at once.
internal static class ThreadPoolWarmUp
{
    private const int MinimumWorkers = 64;

    [Before(HookType.Assembly)]
    public static void RaiseTheMinimum()
    {
        ThreadPool.GetMinThreads(out var workers, out var completion);
        if (workers < MinimumWorkers) ThreadPool.SetMinThreads(MinimumWorkers, completion);
    }
}
