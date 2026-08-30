# Application Bridge transport conformance fixtures

These envelope fixtures deliberately contain no CS-WebUI or browser binding
details. Transport adapters use them to verify JSON framing, contract
fingerprint propagation, and reconnect-epoch echoing at the bridge boundary.

The sequence-zero admission errors reuse the epoch-1 command ID to verify that
late epoch-0 errors are discarded and future-epoch errors trigger recovery
before correlation.
