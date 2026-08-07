# Public API

Applications implement a generated contract handler and host it in an
`ApplicationBridgeSession`. Handlers receive `BridgeCommandContext`, which
provides only session metadata, a safe event publisher, and an operation factory.
Raw transport frames and native callbacks never cross the handler boundary.
