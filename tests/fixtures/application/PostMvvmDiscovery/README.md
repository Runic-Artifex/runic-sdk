# Post-MVVM compiled discovery fixture

This internal fixture proves one build boundary: inspect the assembly after
CommunityToolkit.Mvvm source generation, without loading application code, and
compile deterministic generated artifacts into the same fixture.

The outer build first runs a guarded bootstrap compilation. The inspector then
opens the bootstrap assembly and its closure through `MetadataLoadContext` and
emits, under `obj/runic-post-mvvm-discovery/<selection>/<Configuration>/net10.0`:

1. deterministic experimental IR;
2. a plain ESM TypeScript contract;
3. a typed C# projection and internal route adapter; and
4. a fingerprinted ready manifest, published only after the other files have
   been atomically replaced.

The target is imported by this fixture alone. No shipping SDK target imports
it, and none of its attributes, IR, generated route names, ESM shape, or
selection keys are public authoring API.

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
direnv exec . dotnet restore tests/fixtures/application/PostMvvmDiscovery/PostMvvmDiscovery.csproj
direnv exec . dotnet run --project tests/dotnet/Runic.Application.Bridge.PostMvvmDiscovery.Tests/Runic.Application.Bridge.PostMvvmDiscovery.Tests.csproj -c Debug
direnv exec . ./node_modules/.bin/tsc -p tests/fixtures/application/PostMvvmDiscovery/SmokeFrontend/tsconfig.json
direnv exec . dotnet publish tests/fixtures/application/PostMvvmDiscovery/Smoke/PostMvvmDiscovery.Smoke.csproj -c Release -r linux-x64 -p:PublishAot=true -v:quiet
direnv exec . tests/fixtures/application/PostMvvmDiscovery/Smoke/bin/Release/net10.0/linux-x64/publish/Runic.Application.Bridge.PostMvvmDiscovery.Smoke
```

The generated adapter is an internal fixture artifact that compiles against
the Slice 1 window-session core. This slice does not include a CS-WebUI host,
a browser client, Vite integration, ViewLocation or DI resolution, or a public
Window/View authoring surface.
