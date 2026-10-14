using TUnit.Core;

// The native cases set a variable in this process's environment that the helpers must not see, start helpers that
// share the installed smoke, and the stand-in runs flip ElevatedHelper's static test switches in their own process
// only. Run all cases one at a time.
[assembly: NotInParallel]
