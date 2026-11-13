# Runic host protocol conformance scenarios

Portable scenarios for the [Runic host protocol](../host-protocol.md), `runic-host` version 1. They are normative test
inputs. An implementation checks itself with a runner that plays the other side, and reports, for each scenario id,
its own implementation identity and one outcome: pass, fail, unsupported (an optional feature it does not implement) or
excluded (a role it does not implement).

| Role | The implementation under test | The runner plays |
| --- | --- | --- |
| `guest` | A guest application in hosted mode, with the [test application](#test-application)'s Windows | The host: it starts the guest, writes the offer, reads its lines and connects |
| `host` | A host starting a guest | The guest: it reads the offer, writes lines and accepts the host's connection |

The repository checks the corpus itself in `eng/host-conformance.test.mjs` (`bun run test eng/host-conformance.test.mjs`,
and in the engineering job): every scenario against [`scenario.schema.json`](scenario.schema.json), its id against its
path, and that it is self-contained. The .NET guest runner is `HostConformanceTests` in
`Runic.Application.Views.Desktop.Tests`: it plays the host against `RunicDesktopHost.Run` in hosted mode, in its own
process. The .NET host runner is `HostRoleConformanceTests` in the same suite: it plays the guest against
`IRunicHostedApplications`, through a guest process that relays its standard input and output to the runner, which
listens on its own channel.

## Scenarios

A scenario file follows [`scenario.schema.json`](scenario.schema.json) (format `runic.host.scenario/1`). Its `id` is
`{set}.{file}.v{n}` for the file `scenarios/{set}/{file}.json`. `roles` names the role it checks, `requires` the
features it needs, `arrange` the limits and application of the implementation under test, `ids` every identifier it
uses, and `steps` what happens, in order.

### Arrange

- `limits`: `startTimeout`, `stopTimeout`, `unauthenticatedTimeout` (the upgrade's limit), `helloTimeout` and
  `requestTimeout`, in milliseconds, and `waitingRequests`, a count, that the runner configures the implementation
  under test with; for the `host` role also `menuRequests`, a window's menu requests per second, `guestMenuRequests`,
  the guest's menu requests per second across its windows, `dialogs`, the dialogs a guest shows at once, and
  `dialogRate`, the dialogs a guest shows per ten seconds, `serviceRate`, the guest's clipboard and monitor requests per
  second, `launchRate`, its launches per ten seconds, `presentRate`, its requests that can activate a window
  (`present`, `minimize`, `set-visible`, `set-full-screen`, `toggle-maximized`, `maximize`, `restore` and
  `set-placement`) per second, and `announcementRate`,
  its announcements per second (each rate also its burst), which are the host's defaults when absent. Also for the
  `host` role, `guestActivation`: how long after the guest's last request that can activate one of its windows the host
  counts an activation as the guest's; a host that fixes it, as Runic's does at 2,000, is not configured with it, and its
  runner waits at least the host's own value. Rates are the host's policy, not the protocol's: a host may choose others, and a scenario
  that checks one names it. The protocol's own values are in its [limits](../host-protocol.md#limits). A scenario that waits for
  a limit names it in an [`advance`](#steps) step; a runner waits the configured time, so scenarios use small values.
- `application`: for the `guest` role, the guest application's `name`, `version` and `applicationId`, which its
  successful `hello` reply carries.
- `host`: for the `host` role, the host's `name` and `version`, which its offer carries, and optionally its `policy`:
  `"fails-services"`, a window policy that fails (in .NET, throws) for every window service request, or
  `"prompts-launches"`, a policy that asks the user in a prompt of its own (dialog kind `host-prompt`) before each
  launch, and follows the defaults when the user presses `allow`, or `"limits-placement"`, a policy that refuses
  `maximize` and clamps the size of a `set-placement` to at most 1,200 by 900. With `windowPlacement: true`, the host's
  windows maximize, restore and take a placement, and report their placement, at 100, 100 with 800 by 600 when they
  open: the host lists `maximize` and `placement` for them. With `userStarted: true`, the host starts the guest
  as one the user started directly (in .NET, `RunicGuestStartInfo.UserStarted`).

### Steps

Each step has one action. `connection` names the connection of a `send`, `sendRaw`, `receive`, `receiveAll`, `closed`
or `disconnect` step; `note` is a comment.

| Step | Guest runner (plays the host) | Host runner (plays the guest) |
| --- | --- | --- |
| `start` | Starts the guest with `offer` written to its standard input as the offer line, `offerLine` as raw text, or nothing (`"none"`) | Asks the host to start the guest |
| `line` | Requires the guest's next line with a launch prefix: `channel` (and its members), `refused` (and its members), or `"none"`, no such line before the guest exits | Writes the line: the channel line for its own listener, a refusal, or raw text |
| `offer` | — | Requires the host's offer line to match |
| `connect` | Opens a WebSocket to the channel line's `url`, with `headers` added and the `Host` header or the path replaced by `host` or `path` (`{port}` in them is the listener's port), and requires the upgrade's `status` (101 by default), or with `refused`, that the connection is refused or closed before an upgrade response | — |
| `accept` | — | Accepts the host's connection on its listener |
| `send` | Sends the message to the guest | Sends the message to the host |
| `sendRaw` | Sends a WebSocket message that breaks the profile: `binary`, that many zero bytes in a binary message; `text`, that text in a text message; `textBytes`, a text message of that many bytes, `{}` followed by spaces | The same, to the host |
| `receive` | Requires the guest's next message on the connection to equal it | Requires the host's next message on the connection to equal it |
| `receiveAll` | Requires the guest's next messages, as many as listed, to equal them in any order | The same, for the host |
| `closed` | Requires that the guest closes the connection, with `status` when given, without sending another message | The same, for the host |
| `disconnect` | Closes the connection | Closes the connection |
| `closeInput` | Closes the guest's standard input | — |
| `exit` | — | Ends its own process with `code` |
| `advance` | Waits for the named limit of `arrange.limits` to pass | The same |
| `guest` | Makes the test application do one [action](#test-application) | — |
| `user` | — | Makes the host's user do one [action](#the-hosts-user) in a window of the host |
| `expect` | Checks the outcome of the guest application: an action's result, the guest's start failure, its exit | Checks the host's outcome: its start, or its start failure code, a window's menu bar or its close |

A `receive` step waits until a message arrives, up to the runner's own timeout; time in a scenario passes only through
`advance` and the work the implementation does on its own. When the scenario ends, each open connection must have no
unmatched messages left.

### Values in messages

Messages are compared by JSON value, never by bytes or member order.

- **`{"$any": type}`** matches any value of that JSON type (`string`, `number`, `integer`, `boolean`, `object`,
  `array`, `null`), or of one of an array of types. Where the runner writes a message, it writes `default`, which the
  scenario then provides.
- **`{"$id": name}`** is an identifier from `ids`. A `request` id is a request's `id`: the runner binds the value the
  first time a message it receives carries it, and chooses a new value for a message it sends. A `window` id is a
  window id that the guest chooses and the runner binds the same way. An `item` id is a menu item id that the guest
  chooses ([`menus/1`](../host-protocol.md#menu-entries)), such as in `set-menu-bar` or `show-context-menu`: the runner
  binds it from the first message it receives that carries it, and a message the runner sends, such as `menu-invoke`
  or a context menu's reply, carries only an item id it bound. A `wire` id has a fixed `value`. In the `host` role the
  runner is the guest, so its window and item ids are literal values of the scenario.
- **`{"$file": name}`** and **`{"$directory": name}`** are the absolute path of a file or directory `name` (a plain name,
  no separators) in a temporary directory of the runner, which creates it on first use with some content and removes
  it after the scenario: the paths a dialog returns (`dialogs/1`) are real and absolute on every platform. With `then`,
  a list of path segments (`.`, `..`, a plain name, or `""` for a doubled separator), the path continues with them,
  joined by the platform's separator and not normalized, such as `{"$file": "a.txt", "then": ["..", "b.txt"]}`.
- **`{"$secret": name}`** is a secret of the launch, which the runner computes:

| `name` | Value |
| --- | --- |
| `nonce` | The offer's nonce, which the runner chooses at random for each scenario (guest runner) or reads from the offer (host runner) |
| `key` | The channel line's key |
| `other-key` | A random key that is not the channel line's |
| `host-proof` | The host proof for the nonce and the key ([proofs](../host-protocol.md#proofs)) |
| `host-proof-other-nonce` | A host proof computed with another random nonce |
| `guest-proof` | The guest proof for the nonce and the key |
| `guest-proof-other-nonce` | A guest proof computed with another random nonce |
| `launch-url` | Matches a launch address of the [`window-open`](../host-protocol.md#window-open) form |
| `frame-url` | Matches a new frame launch address of the [`region-attach`](../host-protocol.md#region-attach) form, on the loopback family that the embedder of the latest `region-attach` the runner sent does not use, and binds it for `frame` steps |
| `frame-token` | The frame token of the latest `frame` `launch` (only written, as a `frame` step's `token`) |
| `component-module` | Matches the module address of a component attach ([`region-component/1`](../host-protocol.md#component-mode-region-component1)): `http://`, a loopback literal and a port, ending in `/_runic_component/component.js`, on `127.0.0.1`, or on `[::1]` when the latest `region-attach` the runner sent named a `127.0.0.1` embedder without `allowSameSite`, or with `browser: true` named an embedder other than `[::1]`; it binds it for `component` steps |
| `component-url` | Matches the component's WebSocket address, `ws://` on the module's authority ending in `/_webui_ws_connect`, and binds it |
| `component-token` | Matches a new component token, 43 characters of base64url, and binds it; written as a `component` step's `token` |

## Test application

A guest runner starts a guest application with these Windows and actions. The Windows' pages are not loaded: the
runner checks the protocol, not the page. A guest application under test opens no Window on its own.

For [`region-component/1`](../host-protocol.md#component-mode-region-component1), `arrange.application` has two optional
members: `allowComponentEmbedding: true` makes the application allow component embedding for every host, and
`componentBuild: true` gives its content a component build, `_runic_component/runic-component.json`
(`{"version":1,"name":"conformance-guest","module":"component.js","tag":"conformance-guest-00000000"}`) and the module
`_runic_component/component.js`.

| Window type | Guards its close |
| --- | --- |
| `Main` | no |
| `Guarded` | yes: its guard answers what the last `answerGuard` action said, `true` before any; after `holdGuard` it waits for the next `answerGuard` |
| `Drop` | no; its root ViewModel takes dropped files (`IRunicFileDropTarget`) and records their file names |

The test application has an application locale (`IRunicApplicationLocale`), which follows the host's
([appearance and locale](../host-protocol.md#appearance-and-locale)); it sets none of its own until a `setLocale`
action.

| Action | Members | Result for `expect` |
| --- | --- | --- |
| `open` | `window` (a `window` id it binds), `type`, optional `owner` (a `window` id), `modality` | `opened`, `failed` with a code, or `cancelled` |
| `cancelOpen` | `window` | — (it cancels the window's `open` action through its cancellation token) |
| `close` | `window` | — |
| `requestClose` | `window` | `closed`: `true` or `false` |
| `present`, `minimize` | `window` | A platform outcome: `success`, `unavailable` with `reason`, `failed` with `code` |
| `setVisible`, `setFullScreen`, `setDocumentEdited` | `window`, `value` | A platform outcome |
| `setColorScheme` | `window`, `value` | A platform outcome |
| `answerGuard` | `allow` | — |
| `holdGuard` | — | — |
| `activity` | `window` | The Window's `IRunicWindowPresentation.Activity`, `{active, minimized, visible}` |
| `setMenuBar` | `menu`: `"test"`, `"german"` or `"none"` | — (the application declares that [menu bar](#menus-and-dialogs), or none) |
| `setCommandEnabled` | `command`, one of the bar's command ids; `value`: boolean | — (the application's command can run, or no longer) |
| `holdCommand` | `command`, one of the bar's command ids; `value`: `"blocking"` or `"running"` | — (the command's next run holds until `releaseCommand`: `"blocking"` waits in its `Execute`, as a long synchronous command, and is recorded when it returns; `"running"` is recorded and returns at once, and the command cannot run until released, as an asynchronous command that allows one run at a time) |
| `releaseCommand` | `command` | — (the held run ends: a blocking `Execute` returns, a running command can run again) |
| `invoked` | — | `invoked`: the command ids of the bar that the application ran, in order, since the start; the runner waits until they are the expected ones |
| `contextMenu` | `window`, optional `point` `{x, y}` | The Window's context menu (`IWindowPlatformServices.ShowContextMenuAsync`, at `point` or the pointer): `chosen`, the command id of the item the user chose or `null`, or an `outcome` |
| `openFile`, `openFiles`, `saveFile`, `openFolder` | `window` | The Window's file dialog (`IFileDialogs`: `OpenFileAsync`, `OpenFilesAsync`, `SaveFileAsync`, `OpenDirectoryAsync`): `selected`, the file or directory names of the selection, in order; `dismissed: true`; or an `outcome` |
| `messageBox` | `window` | The Window's message box (`IDesktopMessageBoxes`): `button`, the id of the button the user chose or `null`, or an `outcome` |
| `cancelDialog` | `window` | — (it cancels the window's pending `contextMenu` and dialog actions through their cancellation token) |
| `locale` | — | `locale`: the application's locale, a tag or `null`; the runner waits until it is the expected one |
| `setLocale` | `value`: a tag, or `null` | — (the application sets a locale of its own, or gives it up) |
| `appearance` | `window` | `appearance`: the appearance the Window's pages receive, as the members of the [`appearance` push](../host-protocol.md#appearance-and-locale), or `null`; the runner waits until it is the expected one |
| `readClipboard` | `window` | The Window's clipboard's text, read with a bound of 1,024 characters (`ITextClipboard.ReadTextAsync`): `text`, a string or `null`, or an `outcome` |
| `writeClipboard` | `window`, `value`: the text | A platform outcome (`ITextClipboard.WriteTextAsync`) |
| `monitors` | `window` | The Window's monitors (`IDesktopMonitors.ListAsync`): `monitors`, their ids in order, or an `outcome` |
| `launch` | `window`, `value`: an absolute URI, or a `$file` path | A platform outcome: `IDesktopFileLauncher.LaunchUriAsync` for a URI, `LaunchAsync` with `reveal` for a path |
| `drops` | `window` | `dropped`: the file names of the files dropped onto the application's `Drop` Windows, in order; the runner waits until they are the expected ones |
| `setTitle` | `window`, `value`: the title | — (the title of the Window's page changes, as its page reports it in a [region](../host-protocol.md#set-title)) |
| `maximize`, `restore` | `window` | A platform outcome (`IRunicWindowPresentation.MaximizeAsync`, `RestoreAsync`) |
| `setPlacement` | `window`, `value`: a placement, `{x, y, width, height, maximized, fullScreen}` with `x` and `y` optional | A platform outcome (`IRunicWindowPresentation.SetPlacementAsync`) |
| `placement` | `window` | `placement`: the Window's `IRunicWindowPresentation.Placement` as `{x, y, width, height, maximized, fullScreen}`, or `null`; the runner waits until it is the expected one |

`expect` names the action by the step's `as`, or checks `startFailure` (the guest's hosted start, or the host's start
of the guest, failed with a code), `exited` (the guest process exited; a code when given), `running` (the guest still
runs) or `started` (the host's start of the guest succeeded). An action that did not end as expected, such as one still
waiting, fails the step after the runner's own timeout. `cancelled: true` expects an action that ended cancelled, such as
after `cancelOpen` or `cancelDialog`. The `outcome` of a dialog or context menu is the platform's
(`unavailable` with a reason or `failed` with a code, in kebab case): a dialog request that the host refused reports
`failed` with `permission-denied`, a second dialog of a window `failed` with `resource-busy`, and a Window whose host
does not use the feature `unavailable` with `owner-unavailable`.

### Menus and dialogs

The test application's menu bar is declared for the application (`DesktopHost.SetMenuBar`) when it starts, before any
Window opens, and runs the application's commands (`RunicDesktopHost.UseApplicationMenu`): `file.open`, `file.save`,
`file.recent` and `view.grid`, each recorded for `invoked` when it runs and able to run until `setCommandEnabled` says
otherwise. Its Windows' root ViewModels take no part in the bar. `"test"` is the bar in English, `"german"` the same
bar in German (the titles in brackets); the shortcuts use the [shortcut text](../fixtures/shortcuts.json):

| Menu | Entries |
| --- | --- |
| `File` (Datei), role `file` | `Open` (Öffnen), `file.open`, `Primary+O`; `Save` (Sichern), `file.save`, `Primary+S`; a separator; the submenu `Recent` (Zuletzt geöffnet) with `notes.txt`, `file.recent` |
| `View` (Ansicht) | `Grid` (Raster), `view.grid`, a check item, not checked |

Its context menu has the items `Cut` (`edit.cut`), `Copy` (`edit.copy`, `Primary+C`), a separator and `Paste`
(`edit.paste`), which is disabled.

Its dialogs: `openFile` and `openFiles` with the title `Open` and one filter `Text` (`txt`); `saveFile` with the title
`Save` and the suggested name `notes.txt`; `openFolder` with the title `Choose a folder`; `messageBox` a warning with the
message `Save changes?`, the title `Conformance` and the buttons `save` (Save, default), `discard` (Don't Save,
destructive) and `cancel` (Cancel, cancel).

## The host's user

A host runner plays the user of the host application too. Its host shows each window without a screen, as the window
`window-open` named: a `user` step or an `expect` names it by that id. Each context menu and dialog the host shows waits
for a `user` step that answers it, or until the host closes it.

| Action | Members | What the user does |
| --- | --- | --- |
| `chooseMenuItem` | `window`, `titles`: the path of titles to an item of the window's menu bar; optional `bar: "previous"`; optional `by`: `"click"`, the default, or `"shortcut"` | Chooses the item, with a click in its native bar or, with `by: "shortcut"`, with its shortcut, a key the page left unhandled; the step fails when the bar has no such enabled item. With `bar: "previous"`, an item of the bar the window showed before its current one, as a click on the native bar while the guest's new bar arrived: the host decides whether it runs |
| `awaitContextMenu` | — | Waits until the host shows a context menu, and leaves it open |
| `chooseContextItem` | `title`, or `dismiss: true` | Chooses the item of that title in the oldest open context menu, or dismisses it |
| `awaitDialog` | optional `dialog` | Waits until the host shows a dialog (of that kind), and leaves it open |
| `answerDialog` | optional `dialog`; `paths` (`$file` or `$directory` values), `button`, or `dismiss: true` | Answers the oldest open dialog (of that kind): selects the paths, presses the button, or closes the dialog without either |
| `awaitDialogClosed` | optional `dialog` | Waits until the host closes the oldest dialog (of that kind) that an `awaitDialog` step left open |
| `closeWindow` | `window` | Presses the window's close button, which asks the guest (`close-request`) |
| `deactivateWindow`, `activateWindow` | `window` | Gives the keyboard focus to another window, or back to this one, as the user switching windows does; each window is active when it opens, and a window gets the focus when the host presents it |
| `deactivateHost`, `activateHost` | — | Switches to another application, which makes the host's active window inactive, or back to the host application, which activates the window the host asked to activate meanwhile, else the one active before. While the host application is not active, a request to activate a window, also a new window, does not activate it, as on macOS: it becomes the window activated when the user switches back |
| `copyText` | `text`, optional `repeat` | Copies the text, repeated that many times, in another application: the host's clipboard has it |
| `changeMonitors` | — | Changes the host's monitors, such as a monitor unplugged |
| `dropFiles` | `window`, `paths` (`$file` or `$directory` values) | Drops the files onto the window |
| `setLocale` | `locale`: a tag or `null` | Changes the host application's locale (`IRunicApplicationLocale`) |
| `setAppearance` | `appearance`: `{colorScheme, contrast}`, `colorScheme` `light` or `dark`, `contrast` `no-preference`, `more` or `forced-colors` | Changes the host's desktop appearance, with no reduced motion, a text scale of 1 and no accent colour |

The host starts with no application locale and the appearance `{"colorScheme":"light","contrast":"no-preference",
"reducedMotion":false,"textScale":1,"accentColor":null,"systemColors":null}`, and its windows' monitors are the host
runner's: their ids are not part of a scenario.

`dialog` is a kind of `file-dialog` (`open`, `open-multiple`, `save`, `folder`, `folder-multiple`), `message-box`, or
`host-prompt`, the prompt of the `prompts-launches` policy.
`expect` with `window` checks the host's window: `menuBar`, the entries its native menu bar shows, as menu entries
without their `id` members (the host's own items are part of it), waiting until they match; or `closed: true`, that the
window closed. `expect` with `clipboard` checks the text on the host's clipboard, and with `launched` the links and
files the host opened for the guest, in order (a link's absolute URI, a file's path), each waiting until they match.

## Region scenarios

The scenarios of [`region/1`](../host-protocol.md#regions-region1) are in `scenarios/region/`, with `requires:
["region/1"]`. The host role's are named `region.host-*`. A host scenario's `arrange.host.embed` names the region in which
the host's policy shows each window of the guest that can show in a region and has no owner, and `embedOptions` its
`focusOnAttach` and `reattachOnReload`; with `embedComponent: true` the policy decides `EmbedComponent` instead of `Embed`,
also for a guest that does not allow it. The scenarios of component mode (`region-component/1`) are `region.component-*`
and `region.host-component*`, and require it as well, except those that check its absence. `arrange.host.browser`
makes the host a browser (serve-only) host, whose pages no native window shows, and a guest scenario's
`arrange.application.elevated` plays a guest that runs elevated.

| Scenario | Roles | Checks |
| --- | --- | --- |
| `region.open-without-a-page` | guest | A `window-open` answered `presentation: "region"` completes the Window's open without a page connection, and its launch address is revoked |
| `region.attach` | guest | `region-attach` answers a `frame.url` of the frame form on the loopback family the `embedder` does not use, with `sameSite: false` |
| `region.attach-not-a-region` | guest | `region-attach` for a window shown in a window is `rejected` with `invalid-request` |
| `region.embedding-unsupported` | guest | `embedding: "component"` without `region-component/1` is `rejected` with `embedding-unsupported` |
| `region.embedder-invalid` | guest | An `embedder` with a path, another scheme, a host other than a loopback literal or `localhost`, no port, or over 256 characters, is `rejected` with `invalid-request` |
| `region.detach-revokes` | guest | After `region-detach`, the window's code and token are refused, `activity` of the Window reports `visible: false`, and a new `region-attach` answers a new code |
| `region.reattach-replaces` | guest | A `region-attach` for an attached window revokes the earlier code |
| `region.set-title` | guest | The Window's page title changes are sent as `set-title`; a `capacity` rejection is followed by the latest title |
| `region.presentation-mapping` | guest | Each presentation request of a region window gets its reply from [presentation in a region](../host-protocol.md#presentation-in-a-region): `minimize`, `set-visible`, `set-full-screen`, `set-color-scheme` `unavailable` with `backend-unavailable` without being sent, `toggle-maximized`, `move`, `set-represented-file` `unavailable` with `embedded` |
| `region.host-lifecycle` | host | A region window opens without a page, attaches when a page declares its region, detaches with `removed` or `reload` and attaches again by name with a new code; its close empties the region |
| `region.host-presentation-mapping` | host | The host's replies of [presentation in a region](../host-protocol.md#presentation-in-a-region), `resize` while the region's height follows the page included |
| `region.host-content-height` | host | With `sizing: "content-height"`, the region takes the height of the guest's `resize` |
| `region.host-window-mode-title` | host | `set-title` for a window shown in a window is `rejected` with `invalid-request` |
| `region.host-set-title-rate` | host | `set-title` names the region's frame; the 11th within a second after a burst of 10 is `rejected` with `capacity` |
| `region.host-bounds-coalesced` | host | Several size changes of the region within one frame reach the guest as one `region-bounds` with the latest size |
| `region.host-activity` | host | `activity` follows the region: `visible` with the region shown and in view, `active` with the host window active and the focus in the region; Tab and Shift+Tab into it push `region-focus` |
| `region.host-present-keeps-host-focus` | host | `present` while the host page has a focused element answers `success` and leaves the focus; with no focused element it pushes `region-focus` `programmatic` |
| `region.host-menus-embedded` | host | `set-menu-bar` and `update-menu-items` are answered `unavailable` with `embedded`; context menus show while the focus is in the region |
| `region.host-dialog-owned-by-host-window` | host | A region window's dialog is owned by its host window; while no region shows it, it is `unavailable` with `owner-unavailable` |
| `region.host-services-guest-focus` | host | Clipboard reads are refused while the focus entered the region by `present` or script, also after a click into a frame that had it, and allowed after the user's Tab into it |
| `region.host-page-revoked` | host | `region-page` `revoked` makes the host detach the window and attach it again with a new code |
| `region.host-region-ambiguous` | host | A name-only embed fails the open with `region-ambiguous` while two host windows declare the region |
| `region.host-attach-refused` | host | A refused or failed `region-attach` leaves the window open and detached, and the page shows its fallback |
| `region.host-departure` | host | A departure the host decides asks the guest with `close-request` first |
| `region.host-host-window-closes` | host | The host window's close detaches the region window and closes it with a forced `close-request` |
| `region.host-move` | host | The host moves a region window to another host window's region, and detaches it |
| `region.host-embed-options` | host | Focus on attach, and no re-attach on reload |
| `region.browser-attach` | guest | A `region-attach` with `browser: true` and `allowSameSite: false` answers a frame launch address cross-site to the embedder; a `browser` that is not a boolean is `rejected` with `invalid-request` |
| `region.browser-elevated` | guest | An elevated guest refuses a `region-attach` with `browser: true` with `rejected` and `guest-elevated`, and its Window stays open |
| `region.host-browser` | host | A [browser host](../host-protocol.md#browser-hosts) lists only `region/1` and `region-component/1`, opens region windows without `present` and `owned-windows`, attaches them with `browser: true` and `allowSameSite: false`, answers `present` and `announce` `unavailable` with `backend-unavailable`, and refuses a window of the guest's own |
| `region.component-attach` | guest | With both opt-ins, a component attach answers the module, the WebSocket address and a new token, from the guest's own `127.0.0.1` listener for an embedder of that site that allows it; the module is served to the embedder alone with CORS; the WebSocket is admitted only from the embedder's `Origin` with the token, one page at a time; after `region-detach` both are refused |
| `region.component-cross-site` | guest | A component attach that does not allow the embedder's own site is served from `[::1]` with `sameSite: false` |
| `region.component-browser` | guest | A component attach with `browser: true` for a `localhost` host page is served from `[::1]` with `sameSite: false` |
| `region.component-not-allowed` | guest | Without the application's opt-in, the guest does not list `region-component/1` even when the host does, and refuses a component attach with `embedding-unsupported` |
| `region.component-no-build` | guest | A guest without a component build refuses a component attach with `embedding-unsupported`; a frame attach still works |
| `region.host-component` | host | `EmbedComponent` sends `embedding: "component"` and pushes the reply's module, address and token to the page; `region-page` drives the state |
| `region.host-component-invalid-reply` | host | A component reply out of form is a failed attach: the window stays detached and the page shows its fallback |
| `region.host-component-not-allowed` | host | `EmbedComponent` for a guest without `region-component/1` fails its open |

New steps and actions the runners need:

- **Test application (guest runner).** The action `setTitle` ([test application](#test-application)), and the step
  `frame`, which makes one request of the frame surface of the latest bound `frame-url` (with `url: "previous"`, the one
  bound before it) and expects its `status`, or with `refused: true` any 4xx status, so a scenario checks the one-time
  code, the fragment redirect, one page per token and revocation without a browser:

  | `request` | The request |
  | --- | --- |
  | `launch` | `GET` of the frame launch address, without following the redirect or sending a cookie. A `303` must redirect to the frame surface with `#runic-frame=<token>`, which binds `frame-token`, and carry `Content-Security-Policy: frame-ancestors <embedder>`, `Referrer-Policy: no-referrer` and no `X-Frame-Options` or `Set-Cookie` |
  | `bootstrap` | `GET` of the frame surface's `webui.js`, which must carry no session credential |
  | `upgrade` | The Bridge WebSocket upgrade from the frame surface's origin, with `runic-desktop.<token>` as its subprotocol when the step names a `token` (a string, or `{"$secret": "frame-token"}`). After `101` the runner sends the Bridge's token check, so the guest counts the page, and keeps the connection until the scenario ends |

  For [`region-component/1`](../host-protocol.md#component-mode-region-component1), the step `component` makes one
  request of the latest component attach, with `origin` as its `Origin`: `"embedder"`, the default, for the latest
  `region-attach`'s embedder, or another origin. It expects its `status`, or with `refused: true` any 4xx status:

  | `request` | The request |
  | --- | --- |
  | `module` | `GET` of the bound `component-module`, without a cookie. A `200` must carry `Access-Control-Allow-Origin` with exactly the request's `Origin` and no `Set-Cookie` |
  | `upgrade` | The WebSocket upgrade of the bound `component-url`, with `runic-desktop.<token>` as its subprotocol when the step names a `token` (a string, or `{"$secret": "component-token"}`). After `101` the runner sends the token check with a packet token of 0, so the guest counts the page, and keeps the connection until the scenario ends |
- **The host's user and the host application (host runner).** Actions that play the host page's
  [`hosted-regions/1`](../../application/README.md#hosted-regions) reports, with `window` naming a host window, which the
  runner opens with the page's first region: `declareRegion` (`name`, `sizing`), `removeRegion` (`name`, and `reason`
  `reload` for a page that reloads), `resizeRegion` (`name`, `width`, `height`, `scale`, optional `visible`),
  `focusRegion` (`name`, `source`: `tab`, `shift-tab`, `pointer` or `script`) and `focusHost` (`focused: false` for a page
  that holds no focus). The page moves the focus into a region at the host's request only while it holds none, and then
  reports it as the script's. Actions of the host application, with `window` naming the guest's window:
  `departRegion` (the region's departure guard), `detachRegion` and `moveRegion` (`to`: a host window, `name`). `expect`
  with `window` and `region` checks the region's `state`, `title`, `focused` and `documentEdited`; with `window` (a host
  window) and `page` what the host shows in the page's region `name` (`shows`, `src`, `title`, `allow`, `height`, and for
  a component `module` and `token`, with `src` its WebSocket address); with
  `dialogOwner` the host window that owns the latest dialogs; with `departure` whether the oldest departure went ahead.
