# HTMX vertical slice

This owner-local executable sample uses the neutral encoded `HtmlFragment`
seam, the MVVM session runtime, and the build-proved CommunityToolkit 8.4.2
adapter. All property and command access is through closed direct delegates;
there is no reflection, dynamic invocation, string lookup, handwritten HTTP
endpoint, or Hosting dependency.

The executable demonstrates validation before mutation, generated observable
property assignment, async relay command execution/cancellation, encoded
primary and OOB fragments, stale authoritative recovery, and teardown. The
adjacent `.cwhtml` file records the compiler-facing fragment source; root
solution/build registration remains an orchestrator handoff.
