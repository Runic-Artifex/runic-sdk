using TUnit.Core;

// The cases run in parallel, except those that use process-wide state, which say why where they are marked:
// - [NotInParallel] runs a case alone: it sets environment variables, an AppContext switch or the process culture,
//   installs ReactiveUI's schedulers, listens on Trace.Listeners or the Views ActivitySource and Meter, measures
//   process-wide allocations or the thread pool, or counts ChildView's static lifetimes;
// - [NotInParallel("GeneratedHarnessProcess")] runs the cases that start generated-client harnesses one at a time, as
//   they share the process lock under obj/ (GeneratedHarnessProcess).
// RunicModelContextRegistry.Shared is not among them: each case binds its own ViewModels.
