# Product ownership map

Runic Desktop is a presentation product, not an application framework. Each
integration follows one direction: an owning product adapts its contract to a
presentation host while the Desktop core remains independently usable.

| Concern | Authority | Desktop relationship |
| --- | --- | --- |
| Window and View contracts, .NET ViewModels, generated C# attachments, and TypeScript clients | Runic Application Views | Host-neutral application model. The current first-party host adapter targets CS-WebUI. |
| Browser/WebView surfaces, windows, request streaming, navigation, and native presentation capabilities | Runic Desktop | Independent presentation library; a Runic Application Views Desktop adapter is not part of this preview. |
| Asset identity, manifests, archives, ranges, cache and delivery policy | Runic Assets | Assets-owned adapters can use Desktop request streaming. |
| Locale packs, MF2 compilation, locale selection, generated runtimes, and localization HMR | Runic Translations | Localized artifacts are ordinary owned content; application behavior stays with the application model. |
| Build, development server, proxying, HMR, and development diagnostics | Runic Vite | Vite-owned tooling consumes the explicit Views build and development hooks. |
| Svelte and Angular content rendering | Views Svelte and Views Angular packages | Framework packages consume generated View clients and render their own component tree. |
| Upstream C API, ABI, examples, and compatibility behavior | CS-WebUI | Independent compatibility product and current Views host integration; never a Desktop production dependency. |

## Dependency rule

```text
Runic Application Views CS-WebUI adapter -> Views + upstream CS-WebUI API
Assets Desktop adapter                   -> Runic Assets + Runic Desktop
Runic Vite tooling                        -> Runic Vite + Views build inputs
Views Svelte / Views Angular              -> generated View contracts

Runic Desktop core                       -> no other Runic product package
CS-WebUI                                  -> no Runic Desktop package
```

An adapter may live with its capability owner or in an explicitly named
integration package. Removing it must leave both product cores usable and must
not create a dependency cycle.

## Current seam evidence

These source locations are evidence for the ownership assignment, not
normative contract sources:

- `packages/dotnet/Runic.Application.Views` owns explicit Window and View contracts;
  `Runic.Application.Views.CsWebUi.DependencyInjection` owns the current host
  composition and scoped Window lifetime.
- `packages/dotnet/Runic.Desktop/DesktopSurface.cs` and `DesktopHost.cs` own
  standalone Desktop presentation and native dispatch.
- `packages/dotnet/Runic.Assets/AssetContracts.cs` and `AssetArchive.cs` own
  asset identity and archives. `Runic.Assets.Desktop` owns its delivery adapter.
- `packages/dotnet/Runic.Translations/Runtime/Management/TranslationManager.cs`
  owns runtime locale state.
- `packages/web/vite-plugin-runic` owns the Views development integration.
- `packages/web/views-svelte` and `packages/web/views-angular` own frontend
  content outlets and View lifecycle projection.
- The external `CsWebUi` dependency supplies the browser/native window boundary
  for the Views CS-WebUI adapter.
