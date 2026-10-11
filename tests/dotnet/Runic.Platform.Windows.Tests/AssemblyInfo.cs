using TUnit.Core;

// The native cases share the desktop clipboard (and start peer processes that hold it), the user's power requests
// and the notification registration. Run all cases one at a time, as the former runner did.
[assembly: NotInParallel]
