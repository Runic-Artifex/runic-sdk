using TUnit.Core;

// The cases are timing-sensitive: they hold a model context's scheduler and wait for command state with short
// polling deadlines, and ReactiveUI keeps process-wide scheduler state. Run them one at a time, in the former
// runner's order (NotInParallel Order on each case).
[assembly: NotInParallel]
