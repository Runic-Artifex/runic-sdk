# Hosted service admission

W30's first hosted profile uses one concrete, same-origin topology:

- A trusted reverse proxy terminates TLS for one HTTPS public origin.
- It routes `/runic/service/*` and `/signin-oidc` to the C# Runic Application
  service, and frontend routes to a separate SvelteKit SSR process.
- C# accepts forwarded host and scheme headers only from configured proxy IP
  addresses. The proxy is not an identity authority.

C# performs the `oidc-authorization-code` OpenID Connect flow and creates its
own encrypted, host-only `__Host-runic-session` cookie. That cookie is the only
initial browser session carrier. The browser and SvelteKit server never use a
Runic bearer-token carrier; SvelteKit can only forward the opaque cookie to C#
when it needs a sanitized session projection for rendering.

C# owns session creation, rotation, expiration, authorization, service routes,
and all admission failures. Same-origin unsafe service requests require the
ASP.NET Core antiforgery cookie/request-token pair with `X-Runic-CSRF` and an
exact configured `Origin`. The initial profile has no CORS policy. Missing,
forged, expired, unauthorized, CSRF-invalid, or untrusted-proxy inputs fail
closed before application code receives them.

This is a W30 service declaration, not an implementation of identity-provider
connectivity, SSR/hydration, deployment automation, or rollout. It does not
change the W20 local Application Bridge WebSocket frame boundary: that endpoint
remains a local package-consumer proof and is never exposed as this service
surface.
