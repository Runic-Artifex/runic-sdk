# RunicToolkit Application Bridge benchmarks

This dependency-free Node harness records two paths:

- `transport-returned-batch` verifies that a correlated native batch remains
  one transport frame, independent of its envelope count;
- `effect-returned-batch` measures the complete owned-frame, JSON decode,
  Effect Schema validation, PubSub/Fiber delivery, and receipt-correlation path.

Run the quick matrix after building the npm package:

```console
npm run benchmark:application-bridge
```

For a larger observational baseline:

```console
npm --workspace @runic-artifex/application-bridge run build
node --expose-gc benchmarks/Runic.Application.Bridge.Benchmarks/benchmark.mjs --full
```

The CSV records elapsed microseconds and retained heap deltas. Both values are
host- and runtime-specific observations, not release promises. Garbage
collection before each measurement reduces first-order noise but does not turn
V8 heap deltas into precise allocation counts.

The initial full-matrix observation is committed as [baseline-v1.csv](baseline-v1.csv).
It was recorded on Node 24.18.0 under Linux 7.1.3 x64 on an Intel Core i9-12900K,
from the hardening branch based on `ac93b2c`.

CI uses `--gate`. Its deterministic checks assert one returned transport frame
per native call and exact validated-event delivery through Effect. Wall-clock
and heap observations never fail CI. See [performance-gate-v1.md](performance-gate-v1.md).
