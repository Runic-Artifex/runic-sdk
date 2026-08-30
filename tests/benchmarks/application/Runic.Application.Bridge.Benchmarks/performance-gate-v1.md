# Application Bridge performance gate v1

The gate protects structural properties that directly control allocations and
boundedness without relying on machine-specific timing thresholds.

For batch sizes 1, 256, and 1,024 with two repetitions it requires:

- exactly one transport `Frame` per returned native batch;
- exact delivery of every schema-validated host event through the Effect
  Stream;
- exactly one initialization call plus one native call per measured dispatch.

The ordinary package tests separately enforce configured frame and batch
limits, typed overflow recovery, bounded pending commands, and the bounded
per-session command ledger.

Elapsed time and retained V8 heap are emitted as CSV evidence only. They are not
gated because CPU scheduling, V8 version, garbage collection, power policy, and
virtualization make portable thresholds misleading. Changing the structural
matrix or its assertions requires a new versioned gate.
