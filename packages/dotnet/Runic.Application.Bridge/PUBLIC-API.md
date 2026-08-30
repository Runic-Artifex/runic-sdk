# Public API

Applications implement a generated contract handler and host it in an
`ApplicationBridgeSession`. Handlers receive `BridgeCommandContext`, which
provides only session metadata, a safe event publisher, and an operation factory.
Raw transport frames and native callbacks never cross the handler boundary.

Every client and host envelope carries the generated manifest's SHA-256 contract
fingerprint and a reconnect epoch. A reconnect must perform initialization again;
commands admitted before a disconnect are terminally discarded and must not be
replayed against the resynchronized snapshot.

Transport implementations can encode one envelope with
`ApplicationBridgeCodec.EncodeHost`, or write envelopes directly into an owned
bounded `Utf8JsonWriter` with `ApplicationBridgeCodec.WriteHost` to avoid an
intermediate byte array when constructing a correlated batch.
