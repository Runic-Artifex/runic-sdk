# Current hosted SvelteKit consumer

This additive exact-local consumer installs the current Runic Svelte and
SvelteKit packages from a loopback npm registry. It proves one conventional
SvelteKit route SSRs a request-scoped, C#-sanitized session and explicit
generated-message locale, persists the URL-authoritative locale cookie, and
starts its browser bridge only after the matching SSR bootstrap marker.

The fixture's session loader is a local C#-response double. It receives only
the opaque `__Host-runic-session` cookie and models the W30-002 C# session
endpoint; it neither validates, mints, nor exposes session credentials. Real
OIDC admission, proxy topology, CORS, service transport, deployment, and the
W20 local bridge transport remain outside this proof.
