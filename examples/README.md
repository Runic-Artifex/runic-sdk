# Examples

Runnable Runic applications, from the smallest Window to multi-window
applications. Each uses explicit .NET Window and View types and generated
TypeScript clients. To start a new application, use the
[project templates](https://docs.runic-artifex.eu/getting-started/) instead.

- [First Window](first-window/README.md) introduces one Window, ViewModel, and frontend client.
- [First Window on Runic Desktop](first-window-desktop/README.md) hosts the same counter with Runic Desktop and ReactiveUI.
- [Attachments](attachments/README.md) shows an avatar that the ViewModel draws and uploads files from the page, with `RunicBlob` and `RunicUpload` on Runic Desktop.
- [Status icon](status-icon/README.md) starts without a window and opens one from its status icon and menu.
- [Multi-window project](multi-window/README.md) opens Windows from a ReactiveUI ViewModel, shares state across Windows, stores app settings next to the desktop's preferences, and is translated into English, German and Arabic with one Runic Translations catalog for the page, the ViewModels and the menu bar.
- [Hosted windows](hosted-windows/README.md) is a shell that shows another Runic application's windows as its own: the counter it hosts gets its menu bar and dialogs through the shell, follows the shell's language and theme, and is limited by the shell's policy.
- [Hosted region](hosted-region/README.md) is an installer that shows a separate check tool as a step of its wizard, in a region of its own page: Back and Next ask the check first, the page shows content for each state of the region, and the two can run as an elevated pair.
- [Hosted components](hosted-components/README.md) is a dashboard that shows two widget processes as components in its own page (trusted component mode), built with the Vite `component` target and admitted by the page's Content-Security-Policy.
- [Invoice grid](invoice-grid/README.md) edits an address and the cells of an invoice's lines as fields, with drafts, validation messages and conflicts.
- [CommunityToolkit Notes](notes-view-first/README.md) shows nested content, View scopes, and typed writes.
- [Reactive Notes](notes-reactive-views/README.md) navigates ReactiveUI ViewModels with RunicNavigator: master/detail with a result, a leave guard asked through an `Interaction`, and multiple Views over a model.
- [Document tabs](document-tabs/README.md) opens and closes document tabs at run time, each a navigation region with its own back stack, in a React page.
- [DynamicData](dynamicdata/README.md) presents a 100,000-row cache through independent, keyed viewports.
- [Query collections](query-collections/README.md) sorts, filters and scrolls 100,000 rows that stay in .NET, with variable row heights and scroll to a row.
- [WPF navigation](wpf-navigation/README.md) navigates a plain WPF app with Runic.Navigation.Wpf alone, has a ReactiveUI variant on Runic.Navigation.ReactiveUI, and [compares it](wpf-navigation/COMPARISON.md) with Prism, ReactiveUI and CrissCross.
- [Hybrid WPF editor](wpf-hybrid-editor/README.md) replaces one editor with a Runic web View while retaining the native shell, shared model, services and navigation entry.

The examples build the Runic packages from source with `ProjectReference` and
`Import` lines, so they run only inside this repository. To copy one into your
own project, replace those lines with package references; the
[First Window README](first-window/README.md#copy-it-into-your-own-project)
shows how.

Run the focused example checks from the repository root using the commands in
each example README. The template acceptance suite separately packs the
published templates and verifies React, Vue, Svelte, SvelteKit and Angular consumers:

```sh
bun run pack
bun run verify:templates
```
