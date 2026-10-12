using TUnit.Core;

// The cases share the process-wide platform providers, the temporary directory and, for the opt-in native cases, one
// desktop host and window. Run them one at a time, as the former runner did.
[assembly: NotInParallel]
// The former runner gave each scenario 10 seconds and reported a longer one as a failure. The opt-in native cases have
// their own limit (NativePlatformTests).
[assembly: Timeout(10_000)]
