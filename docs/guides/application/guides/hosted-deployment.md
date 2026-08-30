# Ejectable hosted deployment

The initial hosted deployment is one explicit topology, not a managed platform
or cloud template. A trusted reverse proxy terminates TLS for the one public
HTTPS origin. It sends `/runic/service/*`, `/signin-oidc`, `/runic/health`,
and `/runic/ready` to the C# service process; all frontend routes go to the
separate SvelteKit SSR process. The proxy is the only TLS terminator and is
not an identity authority.

`Runic.Application.Hosting` owns `Runic:HostedDeployment`. Load it before
building the application with `HostedDeploymentConfiguration.Load`, then build
the existing C# admission policy from `CreateAdmissionPolicy`. The following
values are all required:

| Key | Owner | Required value |
| --- | --- | --- |
| `PublicOrigin` | trusted proxy / C# | one HTTPS scheme-and-authority origin |
| `TrustedProxyAddresses` | C# | comma-separated explicit proxy IP addresses |
| `ServiceUpstream` | C# | private HTTP scheme-and-authority for the C# process |
| `FrontendUpstream` | SvelteKit | distinct private HTTP scheme-and-authority for SSR |
| `StaticAssetsPath` | SvelteKit | relative path to the ejected SvelteKit build output |
| `OidcAuthority`, `OidcClientId` | C# | HTTPS authority and client identifier |
| `OidcClientSecret` | C# secret provider | injected value, never committed in an artifact |

The configuration loader rejects missing values, an HTTP public origin, a
wildcard or duplicate proxy address, equal service/frontend upstreams, an
absolute or traversal static-asset path, and non-origin URLs. There are no
developer-machine defaults. The application receives the OIDC secret through
an environment variable or its deployment secret provider, for example
`Runic__HostedDeployment__OidcClientSecret`; it must not be written into the
ejected configuration file, source control, or receipt.

SvelteKit owns the files at `StaticAssetsPath` and its SSR process. C# owns
the service process, the OIDC code flow, encrypted host-only session,
admission policy, and `/runic/health` plus `/runic/ready` responses. The
health and readiness routes are for the trusted proxy or deployment operator;
they are not an alternate public application API. The proxy must not add CORS
or route the W20 Application Bridge WebSocket as a public service.

An ejected application has only published C# output, SvelteKit output, this
non-secret configuration, and an operator-supplied proxy/TLS configuration.
It can therefore be copied to a clean machine and started after its explicit
secret injection. This guide deliberately does not prescribe a cloud vendor,
container runtime, identity-provider credentials, signing, publication, or a
rollout procedure.
