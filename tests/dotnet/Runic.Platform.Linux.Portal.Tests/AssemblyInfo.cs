using TUnit.Core;

// The cases share the session bus, its well-known test names, the document portal mount and the portal-parent export of
// the fake window owners. Run them one at a time, as the former runner did.
[assembly: NotInParallel]

// The cases exercise the Linux providers, which are annotated for Linux only.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
