# Post-MVVM compiled discovery fixture

This internal fixture proves one build boundary: inspect the assembly after
CommunityToolkit.Mvvm source generation, without loading application code, and
compile deterministic generated artifacts into the same fixture.

The outer build first runs a guarded bootstrap compilation. The inspector then
opens the bootstrap assembly and its closure through `MetadataLoadContext` and
emits, under
`<SDK root>/obj/pmd/<selection>/<build-owner>/generated/<Configuration>/net10.0`:

1. deterministic experimental IR;
2. a plain ESM TypeScript contract;
3. a typed C# projection and internal route adapter; and
4. a fingerprinted ready manifest, published only after the other files have
   been atomically replaced.

The target is imported by this fixture alone. No shipping SDK target imports
it, and none of its attributes, IR, generated route names, ESM shape, or
selection keys are public authoring API.

`RunicPostMvvmDiscoveryOutputKey` is always one nonempty ASCII directory
segment (`ordinary` by default). `RunicPostMvvmDiscoveryBuildOwner` is one
private 32-character lowercase hexadecimal token for a restore/build/clean
session. The owner token selects the outer compiler, bootstrap compiler,
inspector, referenced-project, and generated-artifact directories before
restore chooses `project.assets.json`. The fixture rejects an absent or
malformed owner and an empty, traversal, separator, or item-list selection key
before Restore, Build, or Clean performs fixture work.

`eng/build/run-post-mvvm-discovery.sh` creates one owner token and supplies it
unchanged to restore and build. Direct fixture `dotnet restore`, `build`, and
`clean` intentionally fail with `RUNICPM009`; this temporary experiment has no
ambient or generated owner value. A later `runic dev` session must create and
retain the same owner across its evaluation, restore, rebuild, watcher, and
host lifecycle.

The POSIX wrapper has a matching PowerShell entry at
`eng/build/run-post-mvvm-discovery.ps1`. Both are fixture-only tools. The
private owner token activates the repository-level early props policy for all
projects in this one build graph, which gives the fixture and every referenced
tool project disjoint `obj` and `bin` paths.
The wrappers and fixture reject global `OutputPath`, `IntermediateOutputPath`,
`BaseOutputPath`, `BaseIntermediateOutputPath`,
`MSBuildProjectExtensionsPath`, `ProjectAssetsFile`, `OutDir`, and
`RestoreOutputPath` overrides. Any of
these can redirect compiler or restore artifacts outside the owner directory.
This guard covers the standard build and restore roots. MSBuild also permits
direct filenames such as `ProjectAssetsCacheFile`, `GeneratedAssemblyInfoFile`,
`PdbFile`, and `ErrorLog`. This fixture does not promise containment when a
caller injects arbitrary artifact-filename properties. A public build driver
would need a property allowlist or a broader output policy before making that
guarantee.

Both wrappers normally generate their owner. Their
`RUNIC_POST_MVVM_BUILD_OWNER` override exists only for deterministic fixture
regressions such as the checked TypeScript import below; it still accepts only
the private 32-character token shape and must not become a development-session
discovery mechanism.

The ordinary fixture contains an explicit Window/View pair backed by a
CommunityToolkit `NotesViewModel`. The generated projection accesses the
Toolkit `Title` and `SaveCommand` members and a ReactiveUI `RefreshCommand`
with ordinary static types. `POST_MVVM_INTERFACE_VIEW` declares a reusable
`IEditorContract` on the View while the Window chooses `NotesViewModel`; the
inspector retains both the declared View context and the selected concrete
model, without creating a member union. The explicit one- and two-model mapping
scenarios prove that a declared Title-only View contract remains stable when
the application supplies a second concrete model.

The test runner builds Debug and Release variants, verifies byte stability
across rebuilds and configurations, checks that inspection never executes the
fixture module initializer, and verifies deterministic diagnostics for generic,
duplicate, incompatible, external-model, duplicate-key, incompatible-mapping,
and unmapped-model inputs. It also compiles and runs a small typed consumer.

Run the focused checks from the SDK root:

```sh
direnv exec . ./eng/build/run-post-mvvm-discovery.sh -c Debug
direnv exec . dotnet run --project tests/dotnet/Runic.Application.Bridge.PostMvvmDiscovery.Tests/Runic.Application.Bridge.PostMvvmDiscovery.Tests.csproj -c Debug
RUNIC_POST_MVVM_BUILD_OWNER=33333333333333333333333333333333 direnv exec . ./eng/build/run-post-mvvm-discovery.sh -c Debug
direnv exec . ./node_modules/.bin/tsc -p tests/fixtures/application/PostMvvmDiscovery/SmokeFrontend/tsconfig.json
direnv exec . dotnet publish tests/fixtures/application/PostMvvmDiscovery/Smoke/PostMvvmDiscovery.Smoke.csproj -c Release -r linux-x64 -p:PublishAot=true -v:quiet -p:RunicPostMvvmDiscoveryBuildOwner=<owner> -p:RunicPostMvvmDiscoveryOwnerDriver=true
direnv exec . obj/pmd/ordinary/<owner>/dependencies/PostMvvmDiscovery.Smoke/bin/Release/net10.0/linux-x64/publish/Runic.Application.Bridge.PostMvvmDiscovery.Smoke
```

The generated adapter is an internal fixture artifact that compiles against
the Slice 1 window-session core. This slice does not include a CS-WebUI host,
a browser client, Vite integration, ViewLocation or DI resolution, or a public
Window/View authoring surface.
