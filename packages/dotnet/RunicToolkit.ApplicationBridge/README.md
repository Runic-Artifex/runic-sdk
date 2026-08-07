# RunicToolkit.ApplicationBridge

The official .NET application boundary for RunicToolkit browser UIs. Contracts
are named domain commands, receipts, snapshots, events, and public errors
generated from committed Effect Schema artifacts. The runtime is reflection-free
and safe for trimming and NativeAOT.

Create an `ApplicationBridgeSession` around the generated dispatcher. The
session owns its ID, duplicate-command ledger, authoritative revision,
monotonic sequence, bounded admission, operation cancellation sources, and
deterministic teardown. Handlers receive only `BridgeCommandContext` and use
generated typed event-publisher extensions.
