using TUnit.Core;

// The cases share process-wide state: the RUNIC_APPLICATION_CLOSE_AFTER_OPEN environment variable and
// Console.Out/Console.Error (CloseAfterOpenChecks), the static ScopeProbe.Last, and the platform-services
// backend claim (the GTK 4 case claims it for the whole process and runs last). Run them one at a time, in
// the former runner's order (NotInParallel Order on each case).
[assembly: NotInParallel]
