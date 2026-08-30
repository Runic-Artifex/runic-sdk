# Current hosted-service admission declaration

This package-only fixture consumes an exact local `Runic.Application.Hosting`
candidate and proves the initial C#-owned hosted-service declaration: OIDC
authorization-code flow, encrypted host-only cookie session, same-origin CSRF,
trusted reverse proxy, and SvelteKit SSR as a separate frontend process.

It rejects missing or forged declaration fields before a receipt can be trusted.
The local W20 Application Bridge WebSocket proof remains separate and is not a
public service route. This fixture declares a policy only; it does not connect
an identity provider, deploy a service, or exercise SSR/hydration.
