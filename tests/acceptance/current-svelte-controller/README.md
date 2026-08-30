# Current Svelte controller fixture

This package-only consumer installs exact local Application Bridge and Svelte
candidates from a loopback registry with an isolated npm cache. Its headless
DOM provider starts and initializes once, projects an event, sends UI-ready and
rendered once, and releases only its frontend subscription on teardown.

It does not create a host, FrameChannel, WebSocket, reconnect protocol, or
Effect runtime. Authentication, remote transport, SSR, hydration, and rollout
remain outside this fixture.
