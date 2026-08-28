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

- `runic-toolkit/src/Runic.Application/RunicApplication.cs` owns application
  host selection and lifecycle.
- `runic-toolkit/web/packages/application-bridge/src/transport.ts` owns the
  frontend `FrameChannel`; `runtime.ts` owns its controller behavior.
- `runic-assets/src/RunicAssets/AssetContracts.cs` and `AssetArchive.cs` own
  asset identity and archives. Existing ASP.NET Core and CS-WebUI adapters show
  that delivery integration belongs with Assets.
- `runic-translations/dotnet/src/RunicTranslations/Runtime/Management/TranslationManager.cs`
  owns runtime locale state.
- `runic-vite/src/index.ts` and `src/client.ts` own Vite and HMR integration.
- `runic-svelte/packages/svelte/src/bridge.svelte.ts` and
  `effect-bridge.svelte.ts` own Svelte projection and teardown.
- `runic-toolkit/web/packages/angular/src/index.ts` owns Angular projection.
- `cs-webui/src/CsWebUi/WebUiWindow.cs` remains the upstream-compatible native
  window boundary.

The existing `Runic.Application.CsWebUi` host, templates using `UseCsWebUi`,
and the Translations Editor host are migration targets. Their composition logic
must not be copied into Desktop.
