using TUnit.Core;

// The cases share process-wide state: RunicModelContextRegistry.Shared and the ReactiveUI main-thread scheduler,
// Trace.Listeners and the Views ActivitySource and Meter listeners, ChildView's
// static lifetime counters, files under obj/ (generated golden comparisons, the generated-client process lock) and
// timing-sensitive close and cancellation cases. Run them one at a time, as the former runner did.
[assembly: NotInParallel]
