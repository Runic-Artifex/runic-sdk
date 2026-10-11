using TUnit.Core;

// The cases share process-wide state: RunicModelContextRegistry.Shared, Trace.Listeners, the race pages'
// static RacePage.Calm switch, and model threads that tests block on purpose. Several are timing-sensitive
// race and cancellation cases. Run them one at a time, as the former runner did.
[assembly: NotInParallel]
