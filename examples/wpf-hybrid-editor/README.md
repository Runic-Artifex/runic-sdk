# Migrate one WPF editor to the web

This companion to [WPF navigation](../wpf-navigation/README.md) keeps the shell,
notes list and confirmation dialogs in WPF. Only the note editor can switch to
an embedded Runic web View. Open a note, edit it, and select **Use web editor**
or **Use native editor**: the title, body, dirty state, validation and save command
and current navigation entry belong to the same `EditorViewModel` throughout.
**Reload web editor** reconnects that model through a fresh web session.

The frontend uses the same plain TypeScript client approach as
[CommunityToolkit Notes](../notes-view-first/README.md), without introducing a
frontend framework or a second command/state layer. This example stores notes
in memory; its delayed save makes pending work and cancellation visible.

## Run it

On Windows with the WebView2 Runtime installed, from the SDK root:

```sh
bun run bootstrap
bun run --cwd packages/web/views --bun build
dotnet run --project examples/wpf-hybrid-editor/App/HybridNotes.Wpf.csproj
```

WPF runs on Windows. The projects also compile on Linux and macOS with Windows
targeting enabled. The web host and navigation APIs are experimental; the
example suppresses `RUNICWPF001` and `RUNICNAV001` explicitly.

## Reuse the editor in an existing app

Keep application behavior in your existing Toolkit model. In
[EditorViewModel.cs](Model/EditorViewModel.cs), both native bindings and generated
web clients use `Title`, `Body`, `SaveCommand` and the save cancellation command.
The model owns title validation, the saved baseline, dirty comparisons, error
messages and storage. A save captures a draft before awaiting storage; changes
made during that wait remain dirty. Cancelled or failed saves keep the draft.
Both presentations receive the same status and error properties.

Declare a logical View for the model:

```csharp
public sealed partial class EditorWebView : RunicView<EditorViewModel>;
```

This is enough to generate the client and bridge registrations. The application
keeps its WPF `Window`; it does not need a Runic `Window`. Register the generated
`AddRunicViews()` with your application services and build the frontend with the
Views targets, as [HybridNotes.Model.csproj](Model/HybridNotes.Model.csproj) does.
The logical View describes the web presentation contract; WPF's
`MapView<EditorViewModel, EditorView>()` independently selects its native page.

[EditorView.xaml.cs](App/EditorView.xaml.cs) adds a `RunicWebView` to the existing
WPF page before opening its embedded presentation. It passes the existing
model, application services and dispatcher model context to
`CreateWpfViewAsync`. The resulting binding owns its bridge, content session and
desktop surface, and borrows those application objects. The frontend connects
with generated `connectEditor()`. Its field writes run before Save; Cancel runs
immediately so it can interrupt a pending Save.

The WPF page serializes presentation changes, closes the previous binding
before removing its native control, and closes it again on unload. Reload and
native/web switching create fresh presentation sessions. They do not push,
replace or reset a navigation region, initialize the editor again, run a
departure guard or dispose the model. The example falls back to the native
editor if the embedded host fails to open, preserving the same draft.

A web session owns the operations it starts. Closing it cancels an unfinished
web Save while keeping the model and draft alive; a Save started natively
continues through presentation changes. The model's single cancellation token
and saved baseline govern both cases.

## Keep one lifetime owner

[App.xaml.cs](App/App.xaml.cs) owns one DI scope for the WPF window. The scoped
navigator uses the same dispatcher context as its web editor. Navigation creates
and owns each note editor on `PushAsync<EditorViewModel, int>(note.Id)`; the
typed initialization loads that note once for that entry. Back resumes the
native notes list, which refreshes its rows from the same store.

The editor's `LeaveConfirmation.InDialog` asks the native dialog region before
discarding unsaved work. The discard runs when the departure commits; rejecting
or cancelling the question preserves the draft. Back and closing the WPF window
use the same departure guard. A running save rejects departure until it finishes
or the user cancels it, so saving cannot race disposal of the note editor.

If your app retains or borrows a model across fresh navigation entries, keep its
initialization separate from loading/resetting its draft. Navigation initializes
each new entry; that is not authorization to erase an existing model's edits.
This example instead creates a new owned model for each visit and never creates
a navigation entry when changing its presentation.

The portable model project has no WPF dependency. A standalone browser or another
native host can present the same `EditorWebView`, model and services. That host
owns the presentation session; the application still owns the model and
navigator. Do not introduce a second model, storage service or dirty-state
tracker for the browser.

## Verify it

[EditorTests.cs](Tests/EditorTests.cs) drives the real generated bridge with
`RunicWindowTestHost` and the real navigator. It checks native/web field parity,
validation, saving, edits during save, cancellation, retry after a storage error,
session recreation, and accepted, rejected and cancelled departures.

```sh
dotnet test examples/wpf-hybrid-editor/Tests/HybridNotes.Tests.csproj -c Release
dotnet build examples/wpf-hybrid-editor/App/HybridNotes.Wpf.csproj -c Release
dotnet run --project examples/wpf-hybrid-editor/WindowsSmoke/HybridNotes.WindowsSmoke.csproj -c Release
```

On Linux with Nix, prefix these commands with `direnv exec .`. The first check
runs without WPF or a browser. Embedded HWND/WebView2 behavior runs in the
[WPF host tests](../../tests/dotnet/Runic.Application.Wpf.Windows.Tests/NativeChecks.cs) on
Windows. On a Windows desktop, open a note, switch both ways with unsaved text,
reload the web editor, cancel a pending save, and answer **Keep editing** then
**Discard** to exercise the visible migration flow.

[WindowsSmoke](WindowsSmoke/Program.cs) starts the actual WPF shell and embedded
frontend. It types through a native TextBox, switches presentation, edits and
saves through browser DOM events, then switches back, reloads and unloads the
editor. It checks model and navigation identity, the shared store, fresh web
sessions and completed cleanup. CI runs it on Windows; other platforms build
the Windows target but cannot execute WPF.
