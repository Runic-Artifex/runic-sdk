# Integration assessment

The current SDK can use the Primitives fork directly on .NET 10. The main
integration cost was publishing a complete generated snapshot for every
collection notification. Scheduling a cache edit before `ObserveOn` did not
batch the later binding notifications. Unchanged rows also lost their browser
identity after every full hydration.

This example uses a batch at downstream changeset delivery, generated keyed
collection frames, stable client row identity and a presentation-owned
viewport. Immutable leaf DTOs no longer retain subscription graph nodes;
mutable rows retain property subscriptions and serialize replacements. Shared
DTO paths and validation keep the full-snapshot path. Desktop now awaits frame
delivery so its snapshot coalescing cannot discard dependent collection frames.

## Measured backend work

Measured on 2026-10-04, Intel Core i9-12900K, Linux x64, .NET 10.0.12 / SDK
10.0.401, Primitives 9.0.0. Tiered compilation was disabled. Each scenario
uses 10,000 source rows, 50 warmups and 20 samples. The table shows 100 edits
per source batch; the [raw report](benchmark-results.json) also contains 1 and
10 edits.

| Scenario | Presented rows | Median apply/encode | Allocated per batch | Encoded rows per batch |
| --- | ---: | ---: | ---: | ---: |
| Direct collection binding | 10,000 | 1.747 ms | 2.77 MB | 0 |
| Full snapshot per notification | 10,000 | 142.388 ms | 232.29 MB | 1,000,000 |
| One batched full snapshot | 10,000 | 4.243 ms | 5.09 MB | 10,000 |
| Runic incremental | 10,000 | 1.990 ms | 3.04 MB | 100 |
| Direct viewport binding | 100 | 0.875 ms | 0.45 MB | 0 |
| Runic viewport | 100 | 1.050 ms | 0.71 MB | 100 |

Runic adds about 0.24 ms (14%) to the full collection binding boundary and
0.18 ms (20%) to the matched viewport boundary. Those percentages describe
small backend times, not total UI cost. Incremental encoding is about 72 times
faster than per-notification snapshots in this workload and allocates about
76 times less. Twenty incremental batches deliver about 242 KB, compared with
8.16 MB for twenty batched full snapshots. Delivery coalescing makes the
unbatched scenario's delivered frame count variable; encoded-row counts include
all captures regardless of delivery.

The harness excludes asynchronous transport, browser decoding, DOM layout,
paint and native rendering. These are single-machine measurements, not a
statistical claim about every application. Use the README command to reproduce
them on target hardware.

## Verified consumer behavior

The generated headless consumer and its NativeAOT executable pass checks for
two independent viewports sharing a cache, deferred batching into one frame,
and no bridge revision for off-screen edits. Managed and browser tests cover
indexed operations, mutable and shared rows, reset/oversize recovery, ordered
delivery, duplicate frames, gap recovery and unchanged row identity.

The actual Desktop HTTP/WebSocket surface was checked in the collaborative
browser with 100,000 cached rows: updates retain DOM nodes, scrolling to row
995 presents 26 rows, and off-screen updates retain that viewport. Browser
paint timing was not used: the hidden preview throttles animation frames.

## Comparison still needed

End-to-end parity with Avalonia and MAUI is **not established**. The direct
binding baseline measures their common DynamicData collection boundary, not
either framework's native list or its rendering. The current backend overhead
is bounded and small in this workload, which makes a matched native comparison
useful; it does not justify claiming Runic is faster.

Compare the same fork, source cache, row template, scheduler policy and
virtualized visible range on a common supported platform. Include initial
population, visible edits, off-screen edits, filtering/sorting, viewport
scrolling, mutable row refreshes and presentation teardown. Measure input to
paint latency, p95 frame time, allocations and retained memory in Release and
NativeAOT where supported. Keep row count, keys, update frequency and DPI equal.
This remaining comparison is tracked in [SDK issue #36](https://github.com/Runic-Artifex/runic-sdk/issues/36).
