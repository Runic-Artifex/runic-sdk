using TUnit.Core;

// The cases share process-wide state: POSIX signal handlers, and child processes (the stop-test stand-ins, the tool
// under SIGTERM) whose stop and grace-period timings are measured. Run them one at a time, as the former runner did.
[assembly: NotInParallel]
