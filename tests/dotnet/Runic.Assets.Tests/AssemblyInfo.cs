using TUnit.Core;

// The development-directory cases depend on file-system watcher timing (debounce windows, fixed delays before
// checking that no refresh happened), and the packer cases start child processes. Under parallel load those
// timings would no longer hold. Run the cases one at a time, as the former runner did.
[assembly: NotInParallel]
