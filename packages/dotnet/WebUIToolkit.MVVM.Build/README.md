# WebUIToolkit.MVVM.Build

`WebUIToolkit.MVVM.Build` is the deterministic compiler and transitive build
integration for WebUIToolkit MVVM binding declarations. It parses and validates
`.wutmvvm` files before C# compilation, then emits closed, reflection-free dispatch
contracts for the `webuitoolkit.mvvm/1` runtime protocol. It does not provide a
runtime binding interpreter.

## Install and build integration

Reference the build package from an SDK-style project:

```xml
<ItemGroup>
  <PackageReference Include="WebUIToolkit.MVVM.Build" Version="1.0.0"
                    PrivateAssets="all" />
</ItemGroup>
```

The package's `buildTransitive` assets are imported automatically. By default they:

1. Treat `None` items whose extension is `.wutmvvm` as binding inputs and also add
   them to `AdditionalFiles`.
2. Write the compiler input list beneath
   `$(IntermediateOutputPath)WebUIToolkit.MVVM.Bindings`.
3. Run the packaged `net10.0` compiler host before `CoreCompile`.
4. Add the resulting `*.g.cs` files to `Compile` and track them as build outputs.
5. Reconcile obsolete compiler-owned basenames without recursive deletion, validate
   the exact 64-hex artifact inventory before use, and register current artifacts
   with normal MSBuild clean tracking.

Binding files can also be selected explicitly:

```xml
<ItemGroup>
  <WebUIToolkitMvvmBinding Include="Bindings\Customer.wutmvvm" />
</ItemGroup>
```

The following MSBuild properties customize the integration:

| Property | Default | Purpose |
| --- | --- | --- |
| `WebUIToolkitMvvmBindingEnabled` | `true` | Enables discovery, compilation, and generated-file collection. |
| `WebUIToolkitMvvmBindingExtension` | `.wutmvvm` | Changes the extension used for automatic discovery. |
| `WebUIToolkitMvvmBindingGeneratedDirectory` | `$(IntermediateOutputPath)WebUIToolkit.MVVM.Bindings` | Selects the intermediate output directory. |
| `WebUIToolkitMvvmBindingHostPath` | Packaged `tools/net10.0/any/WebUIToolkit.MVVM.Build.dll` | Overrides the compiler host assembly. |
| `WebUIToolkitMvvmDotNetHost` | `$(DotNetHostPath)`, otherwise `dotnet` | Overrides the .NET host used to execute the compiler. |

Set `WebUIToolkitMvvmBindingEnabled` to `false` to opt out completely. Generated
files and `bindings.stamp` are intermediates; they should not be committed.

## Compiler and generated contracts

The compiler exposes a binding-language parser and semantic model under the parent
`WebUIToolkit.MVVM` namespace. Binding language version 1 requires the
`webuitoolkit.mvvm/1` protocol identity and describes contracts, properties,
commands, collection members, and validation targets using explicit stable member
identifiers. Semantic validation rejects unsupported protocol identities, invalid
CLR or wire names, duplicate contracts or members, invalid member options, and
invalid validation targets before generation.

Generation is canonical: logical input paths and declarations are processed with
ordinal ordering, generated identifiers are stable, and output excludes absolute
paths, timestamps, machine state, and other environment-dependent values. The
result is closed dispatch code suitable for trimming and Native AOT; unknown
contract or member identifiers follow generated failure paths rather than dynamic
lookup or reflection.

The build host revalidates its bounded inputs and inventories on every real
`CoreCompile`, which repairs missing or corrupted intermediates instead of trusting
an old stamp. Atomic content comparison leaves unchanged generated sources,
manifests, and inventories untouched, so downstream C# compilation retains normal
timestamp-based incrementality. `bindings.stamp` records the current deterministic
artifact fingerprint; it is bookkeeping, not trusted cache state.

The emitted dispatcher closes and validates mutation kind/member-ID routing, then
delegates accepted operations to caller-supplied callbacks. It is not yet a generated
`IMvvmBindingAdapter`: typed ViewModel access, snapshots, subscriptions, activation,
and generated JSON metadata remain responsibilities of consumer glue or a future
symbol-aware host.

`WebUIToolkit.MVVM.Build.Symbols.GeneratedMemberContractCompiler` is the narrow
symbol-aware hook used by the Wave C CommunityToolkit proof. It reads only compiled
PE metadata through `PEReader`/`MetadataReader`; it does not load the producer
assembly, inspect generated source, or probe `obj`. Given public generated property
and command requirements, it emits a deterministic direct-access adapter source and
manifest. Metadata-derived C# type/member spellings are accepted only when they can
be emitted safely, and manifests use the platform JSON writer so control characters
remain escaped.

`PostGeneratorSemanticCompiler` is the versioned successor handoff for build-only
framework adapters. Schema identity
`webuitoolkit.mvvm.post-generator-semantics/1` accepts normalized capability flags,
reference PEs, and member requirements after framework source generation has
completed. It verifies the public compiled ViewModel surface and emits:

- strongly typed property get/set accessors;
- synchronous and task-returning command accessors with optional exact parameter
  types, `CanExecute`, cancellation, `IsRunning`, and `CanBeCanceled`;
- direct `HasErrors` and per-property `GetErrors` access through a declared
  validation contract; and
- a deterministic list of exact
  `System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>` obligations for the
  consuming adapter's source-generated serializer context.

The adapter identity, version, capability set, reference enumeration, and member
enumeration are normalized before fingerprinting. Paths, reference order, assembly
MVIDs, culture, timestamps, and machine state are excluded from the artifacts.
Generated accessors never use `object` or `dynamic` as a type fallback. Reference
assemblies are opened only as bounded compiler inputs through `PEReader`; the hook
does not perform runtime reflection discovery. A framework adapter can invoke this
public API from its own post-compile build task, so no framework-specific dependency
or command-line mode is needed in this package.

## Diagnostics

All compiler diagnostics have stable `WUTMVVM` identifiers. Broad groups are:

- `WUTMVVM0001`-`WUTMVVM0003`: source, token, or diagnostic-flood limits.
- `WUTMVVM1001`-`WUTMVVM1006`: lexical and grammar errors.
- `WUTMVVM2001`-`WUTMVVM2013`: protocol and semantic-model errors.
- `WUTMVVM2014`-`WUTMVVM2018`: generated-member assembly, type, member,
  accessibility/type-compatibility, and ambiguity diagnostics.
- `WUTMVVM2019`: unsupported post-generator semantic contract version.
- `WUTMVVM0901`: the configured build-time compiler host could not be found.

Compiler spans use zero-based UTF-16 offsets and line/column positions with an
inclusive start and exclusive end. Command-line and MSBuild reporting converts
them to complete one-based ranges, for example:

```text
Bindings/customer.wutmvvm(3,8,3,21): error WUTMVVM1001: diagnostic message
```

Diagnostics are sorted by logical path, span, identifier, and invariant message.
Duplicate diagnostics are removed, and the per-file diagnostic ceiling prevents a
malformed input from producing unbounded output.

## Hostile-input and reproducibility guarantees

Production defaults bound one source file to 1,048,576 UTF-16 characters,
1,048,576 UTF-8 bytes, 131,072 tokens, 1,024 contracts, 4,096 members per contract,
32 levels of generic type nesting, 65,536 UTF-8 bytes per decoded string, 256
characters per identifier, 1,024 characters per type spelling, and 100 retained
diagnostics. Limit failures are deterministic diagnostics rather than partial
generation. The command-line host additionally enforces bounded file counts and
total input bytes, strict UTF-8, project-contained paths, and duplicate-path
rejection.

Committed NuGet lock files are RID-free. If a Native-AOT smoke publish is run, its
runtime-specific restore must use an ignored `obj/aot.packages.lock.json` rather
than altering a committed lock.

## Versioned runtime edge

The package consumes `WebUIToolkit.MVVM` exactly at version `1.0.0`, which owns the
`webuitoolkit.mvvm/1` protocol and runtime contracts. Repository builds can inject
a local packed feed with `WebUIToolkitLocalPackageSource`; the project does not use
a cross-owner project reference to the protocol/runtime source. The build package,
runtime package, binding compiler tool, and generated contract version must be
advanced deliberately when that edge changes.

## Why this is not a Roslyn source generator

The current implementation is an evidence-backed minimal substitute for a Roslyn
incremental generator. Repository policy keeps this compiler/build kernel free of
Roslyn and MSBuild assembly dependencies, while executable corpus and deterministic
no-op tests cover the observable compiler behavior. Package-transitive MSBuild
targets discover inputs late, revalidate and reconcile them before `CoreCompile`,
and feed deterministic generated C# into `Compile`. This retains reproducible,
no-rewrite builds and a small dependency surface without pretending that
`AdditionalFiles` alone performs source generation. A future Roslyn host can reuse
the same parser, semantic model, diagnostics, and generation contracts without
changing the binding language.

## Package contents

- The `net10.0` compiler API/reference assembly.
- A runnable compiler host and its `.deps.json` and `.runtimeconfig.json` under
  `tools/net10.0/any/`.
- `buildTransitive/WebUIToolkit.MVVM.Build.props` for early opt-out, extension, and
  compiler-host defaults.
- `buildTransitive/WebUIToolkit.MVVM.Build.targets` for late input discovery,
  exact-inventory generated-source collection, safe stale-artifact reconciliation,
  deterministic no-rewrite generation, and normal MSBuild clean tracking.
- This package README.
- An exact NuGet dependency on `WebUIToolkit.MVVM` version `1.0.0`.
