# Current hosted deployment consumer

This disposable, exact-local package consumer ejects one generated Runic
application into a concrete W30 hosted topology. It publishes the C# service,
builds a separate SvelteKit SSR output, emits non-secret topology/configuration
files, then starts both ejected outputs from clean directories.

The committed topology fixes one HTTPS proxy origin, explicit proxy addresses,
C# service and SvelteKit upstream ownership, SvelteKit static assets,
health/readiness routing, and C# OIDC configuration. The OIDC client secret is
provided only as an environment variable during the journey and is proved absent
from the ejected artifact. Missing secret and unsafe public-origin inputs fail
closed before the C# service starts.

This is an ejectability proof, not cloud or vendor infrastructure, production
credentials, managed deployment, service rollout, publication, signing, W70,
or an extension of W20's local WebSocket boundary.
