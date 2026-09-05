# Runic application architecture plan: ASP.NET Core on Desktop and Web

## Status

The Linux x64 NativeAOT size gate passed provisionally on 2026-09-03. Proceed
to the application-owned `WebApplication` architecture spike. Repeat the size
comparison on Windows and macOS before making a platform-wide commitment.

This supersedes the proposal to make the member-based Application Bridge the
default way a Runic application communicates with its frontend. Pause further
expansion of that design until the experiments in this plan are reviewed.

## Outcome

A Runic application should be an ordinary ASP.NET Core application that can be
run through either of two hosts:

- **Runic Desktop** starts it on a protected loopback endpoint, displays it in
  a desktop window, and supplies optional native capabilities.
- **ASP.NET Core Web** runs the same application endpoints and frontend as a
  conventional remote web application, with web-specific deployment,
  authentication, authorization, and session policy.

Application code should use familiar web primitives by default: Minimal APIs,
HTTP, OpenAPI, SignalR, Server-Sent Events, and WebSockets. Runic should be a
toolkit and desktop host around those primitives, not a second application
framework with a mandatory private RPC model.

~~~text
                              +-----------------------+
                              | application services  |
                              | and domain model       |
                              +-----------+-----------+
                                          |
                              +-----------v-----------+
                              | ASP.NET Core app       |
                              | APIs, assets, realtime |
                              +-----------+-----------+
                                          |
                    +---------------------+---------------------+
                    |                                           |
          +---------v----------+                      +---------v----------+
          | Runic Desktop host |                      | ASP.NET Core Web   |
          | loopback security  |                      | public web policy  |
          | window + native UI |                      | remote deployment  |
          +--------------------+                      +--------------------+
~~~

Not every desktop application needs to become a server. The goal is that Runic
encourages normal separation of application, delivery, and desktop-only
concerns, so choosing a web host later does not require replacing the
application's fundamental programming model.

## What the WebUI reference teaches us

WebUI does not replace HTTP with WebSocket completely:

- HTTP bootstraps the page and serves assets and the generated `webui.js`.
- One persistent binary WebSocket carries backend calls, JavaScript calls and
  results, browser events, navigation, binary data, and window-control traffic.

That is a sensible design for a small, portable C GUI library with bindings for
many backend languages. WebUI cannot assume that the host language has an HTTP
application framework. Most of its messages are bidirectional RPC and
window-control operations rather than HTTP resources. One connection also
gives it simple session ownership, ordering, and backend-initiated calls, while
the generated script hides all protocol plumbing.

WebUI is deliberately centred on a desktop window and one connected user, not
on being a multi-user application server. A conventional HTTP API,
authentication model, and public deployment surface would add concepts and
dependencies that do not advance that goal.

References:

- <https://github.com/webui-dev/webui>
- <https://github.com/webui-dev/webui/blob/main/bridge/README.md>
- <https://github.com/webui-dev/webui/blob/main/src/webui.c>
- <https://github.com/webui-dev/webui/discussions/434>
- <https://github.com/webui-dev/webui/discussions/553>

Runic should preserve the valuable part of this approach: bootstrap and
desktop-control plumbing must be automatic. It should not infer that all
application traffic belongs in that control protocol. Runic is .NET-specific
and already uses ASP.NET Core and Kestrel, so it can expose the framework's
standard application model instead of recreating routing, binding,
serialization, streaming, and API metadata over a private WebSocket.

## Architecture decisions

### ASP.NET Core is the application model

The application owns a `WebApplication`, service registrations, middleware,
and endpoints. Runic integrates with it rather than constructing a second
hidden application model or root service provider.

The desired shape is approximately:

~~~csharp
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddCounterApplication();
builder.Services.AddRunicDesktop();

WebApplication app = builder.Build();

app.MapCounterApplication();
app.MapRunicAssets();

await app.RunRunicDesktopAsync();
~~~

A web entry point applies the same application registrations and endpoint
mapping, adds its public-server policy, and uses the normal ASP.NET Core
lifetime. One executable with selectable hosting and two small host projects
sharing an application module should both remain possible.

The API names above are illustrative. The spike must find the smallest
integration that preserves normal ASP.NET Core ownership.

### Use each transport for its natural purpose

- HTTP and Minimal APIs are the default for application commands, queries,
  resources, uploads, and downloads.
- SignalR or Server-Sent Events suit common server-push and realtime workflows.
- Application-defined raw WebSockets remain available where appropriate.
- A narrow Runic-owned channel may carry genuinely bidirectional,
  host-specific desktop lifecycle and native-capability traffic.

This architecture is not WebSocket-free. It stops treating a WebSocket as the
universal application API.

### Prefer standard contracts

OpenAPI is the default description for ordinary HTTP endpoints. Frontend client
generation, when useful, should consume OpenAPI or another application-owned
contract instead of a mandatory Runic IR.

AsyncAPI may become useful for application-owned asynchronous protocols when
its tooling solves a demonstrated need. Runic should not build a replacement
pre-emptively.

Effect Schema remains valuable for frontend validation, transformations, and
domain modelling. It need not be the authority for the host's entire API.
OpenAPI-to-frontend generation must be evaluated for fidelity and developer
experience rather than assumed to replace handwritten frontend models.

### Make desktop capabilities optional

Core frontend behavior uses ordinary browser APIs and relative same-origin
URLs. Desktop-only behavior goes through an explicit, detectable capability
surface. "Unavailable in this host" must be distinguishable from application
failure.

Desktop-only capabilities can include native window operations, file and folder
pickers, operating-system integration, and lifecycle notifications. They must
not leak into application services or make the core frontend depend on the
Desktop host.

### Keep Assets and Translations independent

Runic Assets and Runic Translations solve concerns independent of application
transport. They should integrate through the service collection and endpoint
pipeline and remain useful in both hosts where their semantics fit.

Desktop packaging can embed frontend assets for single-file deployment. A web
host can serve the same assets from the application, a reverse proxy, or a CDN.
Application code in the frontend should not depend on that delivery choice.

### Secure the complete Desktop application

Runic Desktop must protect every route and persistent connection on its
loopback listener, including application-defined Minimal APIs and realtime
endpoints. Securing only a Runic control channel is insufficient.

One surface-session bootstrap must cover HTTP, SignalR/SSE/WebSocket
connections, Runic-owned capabilities, origin and host validation, expiration,
and shutdown. Credentials must not leak through URLs or normal logs. The
mechanism should compose with ASP.NET Core middleware and endpoint authorization
instead of relying on every endpoint author to remember a Runic-specific check.

A public Web host independently owns authentication, authorization,
antiforgery, tenancy, rate limits, TLS, public sessions, and scale-out. A local
Desktop credential must never become the default public-server security model.

## Frontend portability rules

A frontend intended for both hosts should:

- call application endpoints through relative same-origin URLs;
- keep application state independent of desktop window state;
- use standard HTTP and realtime clients for application communication;
- discover optional desktop capabilities during bootstrap;
- isolate desktop-only UI and behavior behind those capabilities; and
- avoid importing Runic transport types into ordinary application models.

The decisive test is one production frontend build running in both a Runic
Desktop window and a normal browser. Host-specific bootstrap should be small
and owned by Runic.

## Disposition of the Application Bridge

The member-based Application Bridge, shared IR, strict codecs, snapshot model,
revisions, reconnection rules, and generated Effect facade are no longer the
default V1 application architecture.

After the spikes, choose one explicit outcome:

1. Remove the bridge if standard ASP.NET Core and realtime facilities cover the
   demonstrated requirements.
2. Retain it as an optional package for applications that specifically benefit
   from a stateful, generated local protocol.
3. Narrow it into a desktop-capability protocol that does not model ordinary
   application commands and queries.

Do not retain it merely because implementation exists. Do not remove it before
the spikes identify whether strict codecs, transactional event ordering,
snapshots/reconnection, or its testing support solve requirements that standard
alternatives do not.

## First decision gate: NativeAOT size comparison

Before committing ASP.NET Core as the centre of every Runic Desktop
application, compare the real deployment cost of the old and new approaches.

The first Linux x64 result is recorded in
[`experiments/native-aot-size/results/linux-x64-2026-09-03.md`](../../experiments/native-aot-size/results/linux-x64-2026-09-03.md).
The matched runtime payload was 1.79 MiB for cs-webui and 8.25 MiB for Runic
Desktop, a 4.61-times ratio and 6.46 MiB absolute difference. This passes the
size gate provisionally; the remaining platform measurements are follow-up
evidence rather than a blocker for the architecture spike.

### Primary comparison

Build two minimal Release NativeAOT desktop applications for the same target
RID and with identical publish settings:

1. **cs-webui baseline:** the smallest cs-webui application that opens a window,
   serves an embedded static page, proves one frontend/backend interaction, and
   shuts down cleanly.
2. **Runic Desktop + ASP.NET Core:** the smallest Runic Desktop application that
   provides the same observable behavior through its ASP.NET Core/Kestrel host.

The comparison is cs-webui versus Runic Desktop plus ASP.NET Core. A bare
NativeAOT executable or standalone ASP.NET Core application may be published
later only as a diagnostic to attribute an unexpected delta; neither is a
headline baseline.

Match the behavior closely enough that the result does not charge one side for
security, embedded assets, WebSocket interaction, or lifecycle behavior that
the other side omits. Document any unavoidable difference.

### Measurements

Record:

- total published directory size;
- main executable and separate native-library sizes;
- compressed distributable size;
- absolute size delta and size ratio;
- NativeAOT and trimming warnings;
- cold start to a visible, interactive window; and
- idle working set after startup.

Record the SDK version, RID, operating system, exact publish properties, and
commands. Exclude debug symbols consistently while retaining every platform
file a real deployment must ship. Do not count an OS-provided browser runtime
for one application while bundling an equivalent runtime for the other.

Both ratios and absolute deltas matter. A twenty-times result is a serious
warning and requires explanation, but an extremely small denominator can make
the ratio misleading. The experiment should answer:

- What are the actual shipped sizes of the two minimal applications?
- How many megabytes and what percentage does Runic Desktop add?
- Which managed or native components dominate the delta?
- Is the result appropriate for the desktop applications Runic targets?
- Would avoiding ASP.NET Core save enough to justify recreating web hosting,
  routing, security, and protocol functionality inside Runic?

If the result is unacceptable, stop before redesigning the public application
API. First identify the size contributors and realistic reductions, then
compare with a narrowly scoped alternative host.

## Architecture spike after the size gate

If the size is viable, build the smallest end-to-end application that proves:

1. The application owns one `WebApplication` and one DI container.
2. It maps an endpoint through ordinary Minimal API APIs.
3. The same frontend build calls that endpoint in Desktop and a normal browser.
4. Runic serves embedded assets in Desktop without changing frontend imports.
5. One backend-initiated update uses SignalR, SSE, or an application WebSocket.
6. One desktop-only capability is feature-detected and absent cleanly on Web.
7. Desktop security covers APIs, assets, realtime, and native capabilities.
8. NativeAOT publishing succeeds without reflection introduced by Runic.

Use the spike to decide the public hosting API. Do not generalize every current
package in advance.

## Implementation sequence

1. **Measure deployment cost.** Build the matched cs-webui and Runic Desktop +
   ASP.NET Core NativeAOT demos and record the decision.
2. **Prove host ownership.** Build the end-to-end spike around an
   application-owned `WebApplication` and one root service provider.
3. **Design Desktop security.** Establish one surface-session mechanism for all
   HTTP and persistent endpoints.
4. **Freeze the host API.** Choose the smallest builder/lifetime integration
   that feels like ASP.NET Core and does not duplicate its pipeline.
5. **Prove frontend portability.** Run one production frontend artifact in
   Desktop and Web, including realtime and one optional native capability.
6. **Integrate toolkit services.** Adapt Assets and Translations through normal
   DI and endpoint conventions without coupling them to Desktop.
7. **Decide the bridge.** Remove, isolate, or narrow it based on demonstrated
   gaps and record the result in an ADR.
8. **Migrate templates.** Teach Minimal APIs and explicit capabilities while
   keeping application services independent of both hosts.
9. **Document deployment models.** Cover desktop-only, separate Desktop/Web host
   projects, and an intentionally unified executable.

## Acceptance criteria

The direction is validated when:

- the cs-webui versus Runic Desktop NativeAOT comparison is reproducible and
  its size decision is recorded;
- an application maps Minimal APIs without bridge annotations or generated
  Runic command handlers;
- Runic Desktop consumes the application-owned ASP.NET Core pipeline and root
  provider;
- the same frontend artifact and endpoint definitions work in Desktop and Web;
- application HTTP and realtime code contains no Desktop transport types;
- desktop-only features are explicit optional capabilities;
- loopback authentication protects application and Runic endpoints together;
- Assets supports embedded Desktop and conventional Web delivery;
- NativeAOT succeeds without reflection-based Runic discovery; and
- the Application Bridge has an evidence-based disposition rather than
  remaining a second default architecture.

## Explicit non-goals

- Making every desktop application a secure public service without
  application-specific work.
- Inventing a universal Runic protocol for HTTP, events, and native operations.
- Generating a second API schema when OpenAPI or an application-owned contract
  is sufficient.
- Treating a local desktop credential as public web authentication.
- Hiding ASP.NET Core behind a Runic imitation of its builder, router, or DI
  container.
- Requiring SignalR, SSE, raw WebSockets, Effect Schema, or generated clients
  where ordinary HTTP is enough.
- Preserving current bridge machinery solely for pre-V1 compatibility.

## Questions the spikes must answer

- Is the size and startup difference between cs-webui and Runic Desktop +
  ASP.NET Core acceptable for Runic's target applications?
- Can Runic secure arbitrary application-owned endpoints without distorting the
  ASP.NET Core model?
- Should Runic Desktop extend an existing `WebApplication`, supply a specialized
  lifetime, or use another equally small integration point?
- Should templates default to one selectable host or separate Desktop and Web
  entry points sharing endpoint modules?
- Which native operations truly require a persistent Runic-owned channel?
- Does any remaining Application Bridge feature justify its contract, tooling,
  and maintenance cost?
