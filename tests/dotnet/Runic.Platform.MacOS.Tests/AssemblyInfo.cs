using TUnit.Core;

// The native case writes the system pasteboard, which every case in the process (and pbcopy/pbpaste) shares.
// Run cases one at a time, as the former runner did.
[assembly: NotInParallel]
