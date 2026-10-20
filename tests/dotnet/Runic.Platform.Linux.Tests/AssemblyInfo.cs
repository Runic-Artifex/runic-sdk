using TUnit.Core;

// The cases share the process-wide GTK state (gtk_init, the default main context and the CLIPBOARD selection), and
// each native case owns the GTK loop through its own GtkOwner thread. Run them one at a time, as the former runner did.
[assembly: NotInParallel]

// The cases exercise the Linux providers, which are annotated for Linux only.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
