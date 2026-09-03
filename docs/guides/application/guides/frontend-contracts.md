# Frontend contracts

Write the Application Bridge once as `Frontend/src/application.bridge.ts` with
Effect Schema. Use `defineApplicationBridgeContract` and `bridge.command`; a
command points at its receipt schema and applications declare only domain
errors. The frontend executes these exact schema objects—Runic does not
reconstruct or weaken them.

Install `@runic-artifex/application-bridge-tooling` and run
`runic-bridge generate`. It lowers the encoded side of supported Effect schemas
to committed `Contract/bridge.ir.json` and writes only fingerprint glue to
`src/application.bridge.generated.ts`. `check`, `watch`, and
`diff <baseline> <candidate>` use the same compiler. Unsupported or contextual
wire semantics are errors, and failed regeneration retains the last good files.

Vite projects can enable the same lifecycle directly:

```ts
runic({ desktop: true, applicationBridge: true })
```

The plugin generates at startup and build, watches imported schema modules,
reports compiler diagnostics in the Vite overlay and Runic DevTools, and uses a
full reload boundary for protocol changes. Angular and direct builds invoke the
same CLI from their build scripts.

Reference `Runic.Application.Bridge.Generators` as an analyzer and pass only the
IR as an `AdditionalFile`:

```xml
<AdditionalFiles Include="Contract/bridge.ir.json" />
```

C# compilation recomputes the wire fingerprint and emits closed DTOs, typed
handlers, exhaustive reflection-free dispatch, event helpers, error helpers,
and codecs. A handler returns its generated receipt on success. To return a
declared domain error, throw the generated `<ContractName>BridgeErrors.<Tag>(value)`
exception; the session validates and re-encodes that value with the generated
codec before placing it on the wire. Arbitrary exception details never cross
the bridge.
Application handlers remain handwritten because navigation, authorization,
operations, persistence, and destructive actions are domain policy.

Type-only brands remain frontend-only types. Total standard normalizations such
as `Schema.Trim` keep using the original Effect transformation in the browser
while lowering their complete encoded input to the IR. Refinements with portable
constraint metadata are enforced by both runtimes. Custom or effectful
transformations, refinements whose accepted encoded values cannot be represented,
and contextual schemas fail generation with the exact schema path; they are
never silently weakened.
