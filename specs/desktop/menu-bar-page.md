# In-page menu bar routes

A window whose `DesktopWindowOptions.MenuBarPresentation` is `Page` leaves the menu bar
of `DesktopHost.MenuBar` to the page, which renders it with its own components
(`createMenuBarController` in `@runic-artifex/views`). The page reads the bar and runs
its items through four reserved Bridge routes that `Runic.Application.Views.Desktop`
binds on every Runic Window's transport. `Runic.Application.Views.CsWebUi` binds the
same routes, from the same source, for `RunicCsWebUiHostOptions.MenuBar`: a CS-WebUI
window has no native bar, so its `placement` is `page` while the host declares menus
(`none` without), and its `shortcuts` is `page`, because WebUI reports no keys to .NET. They are not Views protocol messages: Views
protocol 3 and generated clients are unchanged, and a host without these routes leaves
the page's controller `unavailable`.

The shared cases are in [fixtures/menu-bar-page.json](fixtures/menu-bar-page.json):
a declared bar, the commands that cannot run, and the state's `menus` on Linux.

The routes below are menu bar page protocol 1, which hosts of Views protocol 3 serve.
With [Views protocol 4](../application/README.md) the menu bar moves onto the Views
message channel as [protocol 2](#protocol-2-views-protocol-4). The state, its entries,
invoking and the shortcut lease keep their meaning.

## Protocol 2 (Views protocol 4)

The page sends requests to the reserved route `runic.menu-bar` on the Views channel
(`__runicBridge.request`), after the connection's `hello`. The host pushes state changes
instead of answering waits.

| Request | Members | Reply |
| --- | --- | --- |
| `menu-bar-watch` | `route` | `{ "ok": true, "state": … }`, the state for this connection. Until `menu-bar-unwatch` or the end of the connection, the host then pushes `{ "kind": "menu-bar", "route": "runic.menu-bar", "state": … }` to this connection each time the state's `revision` changes. |
| `menu-bar-unwatch` | `route` | `{ "ok": true }` |
| `menu-bar-invoke` | `route`, `revision`, `key` | `{ "ok": true, "result": "executed" }`, `"disabled"` or `"stale"` (see [Invoke](#invoke)) |
| `menu-bar-shortcuts` | `route`, `shortcuts` (`"app"` or `"runic"`) | `{ "ok": true, "state": … }`, the state for this connection (see [Shortcut lease](#shortcut-lease)) |

- The state is the object of [State](#state) without `kind`, and its `protocol` is `2`.
- The push is targeted, because `shortcuts` differs per connection. A host whose transport
  cannot push to one connection delivers it through the Views mailbox (`receive`).
- Each push carries the whole state, so a host may skip states a connection has not been sent
  yet and send only the latest. A reply and a push can arrive in either order; the page keeps
  the state with the highest `revision`. The `menu-bar-shortcuts` reply carries the state its
  lease change produced, and the host does not push that state to the same connection again.
- Failures use the Views reply envelope: a malformed request is `rejected` with the reason
  `invalid-request`; a window that closed answers `released`. A host that does not serve the
  route answers `released` as for any route it does not serve, and the page reports the
  controller `unavailable`. `busy` no longer exists, because nothing waits.
- The request kinds above belong to the `runic.menu-bar` route. Sent to another route they
  are `invalid-request`, and other request kinds sent to `runic.menu-bar` are too.

## Routes (protocol 1)

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
- `{ "kind": "invalid-request" }`: the request is not one of the shapes above, for
  example a `revision` or `after` that is not a number.
- An empty answer (Runic Desktop) or a Views `released` envelope (CS-WebUI) from an earlier host
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
