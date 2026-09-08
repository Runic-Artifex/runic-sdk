# Frontend contracts

C# is the default Application Bridge authority. Annotate behavior and immutable
DTOs in the .NET project; import `src/application.bridge.generated.ts` in the
frontend. The facade exports Effect schemas, decoded/encoded types and the
materialized contract. See [Application Bridge](application-bridge.md) for the
member and DI model.

```ts
runic({ desktop: true, applicationBridge: { authority: 'csharp' } });
```

The Vite plugin infers a single project beside the frontend directory, or accepts
`project: "../MyApp.csproj"`. It generates at startup and build, and watches C#
sources and referenced projects outside the frontend root. A failed compile
reports diagnostics and retains the last-good artifacts. Wire fingerprint changes
require a host rebuild; `dotnet runic dev` coordinates the matching host marker
and full browser reload. Binding-only changes do not change the wire fingerprint.

Direct builds, Angular and CI use the same Node orchestrator:

```sh
runic-bridge generate --authority csharp --project ../MyApp.csproj
runic-bridge check --authority csharp --project ../MyApp.csproj
runic-bridge watch --authority csharp --project ../MyApp.csproj
runic-bridge diff old.bridge.ir.json ../Contract/bridge.ir.json
```

Commit `Contract/bridge.ir.json` and `Frontend/src/application.bridge.generated.ts`.
The bridge build targets generate before `CoreCompile`; the .NET SDK and local
`dotnet runic` tool must be installed. Source generators only add compiler output.
Use `--ir` and `--facade` for output overrides.

## Effect authority

For a frontend-owned contract, define all command, receipt, snapshot, event and
error schemas in `application.bridge.ts` with `defineApplicationBridgeContract`.
There is no `initialize` property. Initialization always invokes the generated
C# handler's typed `GetSnapshotAsync(BridgeSnapshotContext, CancellationToken)`.

```ts
runic({
  desktop: true,
  applicationBridge: {
    authority: 'effect',
    source: 'src/application.bridge.ts',
  },
});
```

Set `RunicApplicationBridgeAuthority` to `effect` and
`RunicApplicationBridgeSource` to the source path in MSBuild. Direct CLI calls use
`--authority effect --source src/application.bridge.ts`. Add the IR as an
`AdditionalFiles` item. Implement the generated handler interface in one concrete
class; generated composition resolves its constructor dependencies through DI.
The Translations Editor demonstrates this path.

The facade preserves the exact original Effect schema objects. Portable bounds,
patterns and collection constraints run on both sides. Observable transformations
(including trimming), contextual schemas and unrepresentable refinements fail
compilation. Apply frontend-only brands, normalization and presentation models
after decoding or before constructing a wire request. They do not belong in the
transport contract.
