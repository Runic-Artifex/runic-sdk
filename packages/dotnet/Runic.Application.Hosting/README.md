# Runic.Application.Hosting

Generic Host and local ASP.NET Core integration for a generated
`Runic.Application` composition manifest. Add this package only when the
application deliberately owns a `Microsoft.Extensions.Hosting` lifetime or a
local Application Bridge WebSocket endpoint.

## Host and frontend boundary

The generated application and its C# host own the manifest, generated bridge
contract, `ApplicationBridgeSession`, lifecycle, revisions, cancellation, and
the endpoint. `ApplicationBridgeWebSocketTransport` adapts that caller-owned
session to one bounded binary frame channel; it does not create another
protocol, session, or revision authority. The frontend owns only a controller
over that channel and its rendering state.

Configure `UseWebSockets` before mapping the endpoint and explicitly allow
browser origins that may connect. Each configured origin is an exact
scheme-and-authority match. A request without an `Origin` header is accepted;
an unlisted supplied origin is rejected. Origin checking limits browser
cross-site connections, but is not authentication or authorization.

```csharp
app.UseWebSockets();
app.MapRunicApplicationBridge(
    "/runic/bridge",
    new ApplicationBridgeWebSocketTransport(session, new()
    {
        AllowedOrigins = ["https://app.example.test"],
    }));
```

Frames are binary and subject to the supplied `BridgeLimits`; malformed,
non-binary, and oversized frames close with a policy violation. A replacement
connection must successfully initialize at a strictly higher connection epoch;
the existing session remains authoritative for command ordering and recovery.
Disposing the transport cancels its accepted connections but does not dispose
the caller-owned session.

`ApplicationBridgeRefreshCoordinator` forwards authoritative asset and
translation notifications through that same session. It does not let the
frontend define refresh state. `GenericHostApplicationHost` adapts an explicitly
supplied Generic Host lifetime only; it is not a second frontend host. For one
session, choose either this WebSocket transport or Runic Desktop's native
transport—do not attach both as competing session owners.

This is a local package-consumer frame boundary. Authentication, authorization,
public routing, TLS or reverse-proxy policy, remote session management,
deployment, SSR, hydration, and rollout are outside this package boundary.

## Hosted service admission

The initial hosted-service profile is deliberately separate from the local
Application Bridge endpoint. It uses an OpenID Connect authorization-code flow
terminated by C#, then an encrypted, host-only `__Host-runic-session` cookie
issued and validated only by the C# service. A browser or SvelteKit server does
not send bearer tokens to Runic service routes.

The public topology is one HTTPS origin behind a reverse proxy. The proxy
routes `/runic/service/*` and `/signin-oidc` to C#; it routes all other frontend
requests to the SvelteKit SSR process. C# accepts forwarded scheme and host
headers only from the explicit `HostedServiceAdmissionPolicy` proxy addresses.
The SvelteKit process may forward the opaque cookie to C# to render a sanitized
session projection, but it cannot validate, mint, or authorize that cookie.

C# owns login completion, session rotation and expiration, authorization, and
service-route admission. Unsafe same-origin service requests require the
standard antiforgery cookie/request-token pair and `X-Runic-CSRF` header; their
`Origin` must equal the configured public origin. No CORS policy is part of the
initial profile. Missing, invalid, expired, unauthorized, CSRF-invalid, or
untrusted-proxy requests fail closed before application handlers run.

`HostedServiceAdmissionPolicy.CreateInitial` records the fixed flow, cookie,
routes, and trusted-proxy boundary. It does not map authentication middleware
or connect a production identity provider; those are W30 service implementation
work. `AddRunicHostedServiceAdmission`,
`UseRunicHostedServiceForwardedHeaders`, and `MapRunicHostedService` implement
the bounded cookie/antiforgery admission surface. Configure the application's
OIDC authorization-code handler to issue this C# cookie, call authentication
middleware before mapping the service. For a role-bound unsafe route, keep the
handler in the application so Minimal API's Request Delegate Generator can
analyze it, then apply `RequireRunicServiceRole`:

```csharp
group.MapPost("/command", static (CommandRequest request) => Results.Ok())
    .RequireRunicServiceRole("operator");
```

`Runic.Application.Hosting` enables the Request Delegate Generator and its
public endpoint helpers deliberately do not accept arbitrary `Delegate`
handlers. This makes NativeAOT incompatibilities a compile-time application
error instead of a trimming warning hidden inside the hosting package. The
service exposes only a bounded sanitized session
projection and antiforgery request token; it never exposes the session cookie,
OIDC tokens, or unbounded claims. The W20 WebSocket endpoint remains local-only
and is never this public service surface.

## Ejectable hosted deployment

`HostedDeploymentConfiguration.Load` requires the complete C#-owned deployment
configuration before the application starts: one public HTTPS origin, explicit
trusted proxy IPs, distinct private C# and SvelteKit upstreams, a relative
SvelteKit static-output path, and C# OIDC authority/client/secret values. The
OIDC secret must be injected by the deployment environment under
`Runic__HostedDeployment__OidcClientSecret`; it is never a checked-in artifact.

Use `CreateAdmissionPolicy` to bind the deployment origin and proxy snapshot to
the existing admission surface, and `MapRunicHostedDeploymentHealth` to map the
service-only `/runic/health` and `/runic/ready` responses. The full topology,
ejection boundary, and deferred rollout concerns are documented in
[`hosted-deployment.md`](../../docs/guides/hosted-deployment.md).
