using TUnit.Core.Executors;

// The scenarios share one WPF Application and its dispatcher thread: run them one at a time, in order, on
// that dispatcher. The shared NotInParallel key makes TUnit honour Order.
[assembly: NotInParallel]
[assembly: TestExecutor<Runic.Navigation.Examples.Notes.Tests.WpfDispatcherExecutor>]
