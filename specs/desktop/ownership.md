# Product ownership map

Runic Desktop is a presentation product, not a second application framework.
Every integration follows one direction: an owning product adapts its contract
to Desktop, while the Desktop core remains independently usable.

| Concern | Authority | Desktop relationship |
| --- | --- | --- |
| Application composition, backend lifecycle, capabilities, commands, events, revisions, and Application Bridge schemas | Runic Application | An Application-owned adapter selects a Desktop presentation host and supplies its low-level channel to the existing Bridge session. |
| Browser/WebView surfaces, windows, presentation sessions, local transport, request streaming, navigation, and platform presentation capabilities | Runic Desktop | Core product authority. |
| Asset identity, manifests, archives, ranges, cache and delivery policy | Runic Assets | An Assets-owned adapter maps canonical content sources and delivery policy onto Desktop request streaming. |
| Locale packs, MF2 compilation, locale selection, generated runtimes, and localization HMR | Runic Translations | Localized artifacts are ordinary owned content; locale changes travel through existing Application or development seams. |
| Build, development server, proxying, HMR, and development diagnostics | Runic Vite | A Vite-owned adapter consumes bounded Desktop development and navigation hooks. |
| Svelte and Angular state projection and component lifecycle | Runic Svelte and the Angular adapter | Framework packages consume the existing neutral controller and may receive a Desktop channel from composition. |
| Upstream C API, ABI, examples, and compatibility behavior | CS-WebUI | Independent compatibility product and differential oracle; never a Desktop production dependency. |

## Dependency rule

```text
Application Desktop integration  -> Runic Application + Runic Desktop
Assets Desktop adapter            -> Runic Assets + Runic Desktop
Translations integration          -> Runic Translations + owning Application seam
Vite Desktop adapter              -> Runic Vite + Desktop frontend package
Svelte / Angular composition      -> Application Bridge + Desktop frontend package

Runic Desktop core                -> no other Runic product package
CS-WebUI                           -> no Runic Desktop package
```

An adapter may live with its capability owner or in an explicitly named
integration package. Removing it must leave both product cores usable and must
not create a dependency cycle.

## Current seam evidence

These source locations are evidence for the ownership assignment, not
normative contract sources:

- `packages/dotnet/Runic.Application/RunicApplication.cs` owns application
  host selection and lifecycle.
- `packages/web/application-bridge/src/transport.ts` owns the
  frontend `FrameChannel`; `runtime.ts` owns its controller behavior.
- `packages/dotnet/Runic.Assets/AssetContracts.cs` and `AssetArchive.cs` own
  asset identity and archives. Existing ASP.NET Core and CS-WebUI adapters show
  that delivery integration belongs with Assets.
- `packages/dotnet/Runic.Translations/Runtime/Management/TranslationManager.cs`
  owns runtime locale state.
- `packages/web/vite-plugin-runic/src/index.ts` and `src/client.ts` own Vite and HMR integration.
- `packages/web/svelte/src/bridge.svelte.ts` and
  `effect-bridge.svelte.ts` own Svelte projection and teardown.
- `packages/web/angular/src/index.ts` owns Angular projection.
- The external `CsWebUi` dependency supplies the browser/native window boundary
  for `Runic.Application.CsWebUi`.

The `Runic.Application.CsWebUi` host, templates using `UseCsWebUi`, and the
Translations Editor compose these capabilities. Shared application composition
belongs in Application; Desktop owns presentation.
