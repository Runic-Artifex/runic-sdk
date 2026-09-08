# Linux native dependency patches

The development flake builds the locked AT-SPI package with
`at-spi2-core-release-embedded-message.patch` and places its libraries first in
the shell's native library search path. GTK and WebKit remain at their locked
versions. This applies to applications launched through that environment; it
does not modify the system installation or bundle AT-SPI into NuGet packages.

## AT-SPI embedded accessibility message ownership

In AT-SPI 2.60.6, `atk-adaptor/bridge.c`'s `socket_embed_hook` creates a D-Bus
`Embedded` message, sends it, and fails to release its owned reference. Repeated
WebKit window lifetimes expose steady native allocation growth even after the
GTK windows, WebKit contexts, and WebView children have been finalized.

The patch releases the sender's message reference after sending. Accessibility
remains enabled. It does not change Runic's managed lifetime logic, disable
compositing or accessibility, or force garbage collection/allocator trimming.

Upstream source:
[AT-SPI 2.60.6 bridge.c](https://github.com/GNOME/at-spi2-core/blob/2.60.6/atk-adaptor/bridge.c).
Remove the patch when the locked upstream dependency includes the correction.

The initial investigation retained a failed two-hour uninstrumented soak and a
separate allocation-tracing diagnostic. Neither is a passing fix receipt.
Verification of this patch requires an uninstrumented 30-minute soak, including
loaded-library paths/hashes, application hashes, patch identity, memory trends,
resource counters, and natural child-process shutdown. Keep the original failed
receipts intact.

Consumers using distribution-provided GTK/WebKit libraries need the equivalent
AT-SPI correction in their native runtime. A result from the patched Nix shell
does not establish that an unpatched distribution is free of this upstream leak.
Native dependency limitations must remain explicit in release documentation.

## Preview acceptance and remaining memory growth

The patched, uninstrumented Linux run on September 8 completed 5,556 native
window/reconnect/cancellation cycles in 1,800,112 milliseconds. Process-tree
memory quarter medians were 522.852, 523.633, 524.688, and 525.059 MiB. Tracked
resource checks passed and all helper processes exited naturally within
100.36 milliseconds. The strict memory-trend check failed; its raw result is
retained as failed.

The AT-SPI reference ownership defect is confirmed, but the remaining roughly
2.2 MiB growth after patching is not attributed. These measurements alone do
not distinguish another leak from caching or allocator behavior. The preview
accepts this small Linux trend as a known issue; it does not establish a
complete leak fix or waive resource cleanup, process shutdown, or other
platform checks. Final release evidence must refer to the actual candidate
binaries, rather than reuse this earlier run's result for changed artifacts.
