# In-page menu bar routes

A window whose `DesktopWindowOptions.MenuBarPresentation` is `Page` leaves the menu bar
of `DesktopHost.MenuBar` to the page, which renders it with its own components
(`createMenuBarController` in `@runic-artifex/views`). The page reads the bar and runs
its items through four reserved Bridge routes that `Runic.Application.Views.Desktop`
binds on every Runic Window's transport. They are not Views protocol messages: Views
protocol 3 and generated clients are unchanged, and a host without these routes leaves
the page's controller `unavailable`.

The shared cases are in [fixtures/menu-bar-page.json](fixtures/menu-bar-page.json):
a declared bar, the commands that cannot run, and the state's `menus` on Linux.

## Routes

Each route takes at most one argument, a JSON object as a string, and answers a JSON
object as a string. Every answer has a `kind`.

| Route | Request | Answer |
| --- | --- | --- |
| `__runicMenuBarState` | none | the state |
| `__runicMenuBarWait` | `{ "after": revision }` | the state once its revision differs from `after`; at once when it already does |
| `__runicMenuBarInvoke` | `{ "revision": revision, "key": key }` | `{ "kind": "executed" }`, `"disabled"` or `"stale"` |
| `__runicMenuBarControl` | `{ "shortcuts": "app" }` or `{ "shortcuts": "runic" }` | the state for this connection |

Other answers:

- `{ "kind": "busy" }`: the connection already has 4 pending waits (64 for the window).
  The page waits about a second before it asks again.
- `{ "kind": "released" }`: the window closed.
- `{ "kind": "invalid-request" }`: the request is not one of the shapes above.
- An empty answer (Runic Desktop) or a Views `released` envelope (CS-WebUI) from a host
  that does not serve the route. The page reports `unavailable`.

## State

```json
{
  "kind": "state",
  "protocol": 1,
  "revision": 4,
  "placement": "page",
  "shortcuts": "host",
  "platform": "linux",
  "menus": []
}
```

- `revision` increases with each change of the bar, of an item's availability, of the
  placement or of the shortcut owner.
- `placement` is where the bar is shown now: `page`, `window`, `global` or `none`. It is
  `DesktopWindow.MenuBarPlacement`, except that a `Page` window whose host reports no
  placement, such as an installed browser or a window host without a native bar, is
  `page`. On macOS the application menu bar keeps the declared menus and a `Page` window
  is `global`. The page renders a bar only for `page`.
- `shortcuts` is who matches the items' shortcuts for this connection: `host` on its
  native key path (`DesktopWindowCapabilities.MenuBar`), `page` the page's controller,
  or `app` while this connection holds the lease and the placement is `page`.
- `platform` is `windows`, `linux` or `macos`; it selects the page's shortcut rule and
  whether the page answers Alt and F10.
- `menus` are the planned menus in the platform's order (on Windows and Linux File first
  and Help last; the Application menu is a plain menu there).

A later host may add members and values; a page ignores unknown members, skips entries
of unknown kinds and reads an unknown `placement` as `none`, `shortcuts` as `host` and
`role` as `none`.

### Entries

| Member | Menu | Item | Separator |
| --- | --- | --- | --- |
| `kind` | `menu` | `item` | `separator` |
| `key` | yes | yes | yes |
| `label`, `accessKeyIndex` | yes | yes | |
| `accessKey` | when declared | when declared | |
| `role` | `none`, `application`, `file`, `help` | | |
| `enabled` | the declaration, with its parents' | also the command's `CanExecute` | |
| `entries` | yes | | |
| `commandId`, `commandParameter` | | yes, the parameter when declared | |
| `checked`, `radioGroup` | | when declared | |
| `shortcut`, `shortcutText`, `ariaKeyShortcuts` | | when declared and not reserved | |

- `key` is stable while the declaration keeps the entry at its position with the same
  kind (a check or radio item has another key than a plain one).
- `label` is the title; when the title does not contain the access key, it ends with the
  key in parentheses, as `About (Q)`. `accessKeyIndex` is the key's UTF-16 index in
  `label`, or -1, and `accessKey` the key in upper case.
- `shortcut` is the canonical `DesktopShortcut` text, `shortcutText` the platform's
  display text (`Ctrl+Shift+S` on Windows, `Shift+Ctrl+S` on Linux, `⇧⌘S` on macOS) and
  `ariaKeyShortcuts` the `aria-keyshortcuts` value (`Control+Shift+S`, `Meta+Shift+S`).
  Shortcuts the platform reserves are left out.

## Invoke

The host looks the key up in the bar of the request's revision and runs the item with
`DesktopWindow.TryExecuteMenuItemAsync`, the native bar's command path: the declaration,
the window's `MenuCommands` (a Runic Window's root ViewModel) and then
`DesktopHost.MenuCommands`, each asked `CanExecute` before `Execute` on the window's UI
thread. A ViewModel command runs in its model turn.

- `stale`: the bar changed since `revision` (`SetMenuBar`), the revision is newer than the
  host's, or the key names no item.
- `disabled`: the item is disabled or no target can run it now.
- `executed`: a target ran it.

The page can already run any of its generated commands, so the routes add no power: a
page invokes only declared items, with the checks of a click on the native bar.

## Shortcut lease

By default Runic matches the items' shortcuts: the host on its key path after the page
saw the key, or the page's controller in a bubbling `keydown` listener that skips
`defaultPrevented` and composing events. A page that matches them itself sends
`{ "shortcuts": "app" }`; while the connection holds that lease and the placement is
`page`, the host's own menu path runs nothing. `{ "shortcuts": "runic" }` returns one
lease; a closed connection, such as a reloaded page, returns all of its leases.
`TryExecuteMenuItemAsync` and the invoke route keep working under a lease.
