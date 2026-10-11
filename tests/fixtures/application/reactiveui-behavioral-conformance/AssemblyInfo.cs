using TUnit.Core;

// The cases share ReactiveUI's process-wide state: AmbientMainThreadSchedulerSemanticsAsync installs the
// model-context main-thread scheduler for the cases after it, and BridgeExceptionSemanticsAsync replaces the
// global default exception handler, so it runs last. Run them one at a time, in the former runner's order
// (NotInParallel Order on each case).
[assembly: NotInParallel]
