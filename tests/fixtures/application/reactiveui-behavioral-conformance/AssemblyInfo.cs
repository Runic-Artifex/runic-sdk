using TUnit.Core;

// The cases share ReactiveUI's process-wide state: AmbientMainThreadSchedulerSemanticsAsync installs the
// model-context main-thread scheduler for the cases after it, and BridgeExceptionSemanticsAsync replaces the
// global default exception handler and builds ReactiveUI, so the builder cases (W260-015) that reset the builder
// state run after it. Run them one at a time, in order (NotInParallel Order on each case).
[assembly: NotInParallel]
