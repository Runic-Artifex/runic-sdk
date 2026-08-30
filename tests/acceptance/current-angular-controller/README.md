# Current Angular controller fixture

This package-only consumer installs exact local Application Bridge and Angular
candidates from a loopback registry with an isolated npm cache. It verifies the
same generated `runic.artifex.setup` identity and fingerprint used by the
current host-transport fixture, injects one supplied neutral controller, and
projects a validated event into Angular signals.

Injector teardown releases the Angular subscription only; composition retains
and then disposes the controller. The fixture also checks bounded command
rejection and unavailable reconnect behavior. It does not create a host,
FrameChannel, WebSocket, SSR, authentication, or deployment path.
