# Runic host protocol

Contract identity: `runic-host`, version 1 (`runic-host/1`).

This is the Runic host protocol between a **host**, a Runic Desktop application that shows another application's
windows, and a **guest**, a Runic Desktop application that opted in to being shown that way. The guest serves its
pages as it does in serve-only mode and shows no window of its own; for each of its Windows the host opens a native
window of its own process that loads the guest's page, and the guest controls that window over the **host channel**,
a WebSocket that speaks this protocol.

The protocol and the .NET API that implements it are experimental (`RUNICDESK103`, [experimental APIs](../../docs/experimental.md)):
they may change in a minor release until they graduate, also after 1.0 (D055.8). The design, its use cases and the
decisions behind it are in the S11 design record; this document is the contract. The key words MUST, MUST NOT, SHOULD
and MAY are used as in RFC 2119.

| Document | Content |
| --- | --- |
| This document | Roles, the launch contract (offer, channel and refusal lines), the channel listener, the handshake, messages and errors, features, windows, presentation, appearance and locale, menus, dialogs, window services, lifecycle, limits, versions and security requirements |
| [host-conformance/](host-conformance/README.md) | Conformance scenarios for guests and hosts |
| [Security guide](../../docs/security.md#hosted-application-windows) | What hosting means for an application's security |

## Roles and terms

- **Host.** The process that starts the guest, owns its standard input and standard output, connects to the host
  channel and presents the guest's windows. Runic's host is `IRunicHostedApplications` in
  `Runic.Application.Views.Desktop`; `RunicTestHost` in `Runic.Application.Testing` plays the host in tests.
- **Guest.** The process the host started, in **hosted mode**: Runic's guest is an application that runs
  `RunicDesktopHost.Run` with `RunicDesktopHostOptions.Guest` set. It serves each Window's page on its own surface, as
  in serve-only mode, starts no window toolkit loop and never shows a window of its own.
- **Host session.** One guest process, from its start to its exit. A host session has one host channel. A guest
  process serves one host session (D055.8): it does not claim or forward a single-instance launch.
- **Hosted window.** The host's native window that shows one guest Window. Its frame, placement, menu bar,
  activation and web view profile belong to the host; its page and everything the page does belong to the guest.
- **Window id.** The guest's name for one of its Windows on the channel.

## Launch contract

### Starting the guest

The guest decides in code whether it can be hosted. An application defines its own parameter, such as a hidden
command `hosted` or an option `--hosted`, and runs hosted mode only on that path. Runic defines no well-known flag and
no environment variable for it, so nothing is inherited by the guest's own child processes and an application without
that code path cannot be hosted.

The host:

1. starts the guest's executable directly, without a shell, with the file name and arguments the host application
   chose (`RunicGuestStartInfo`);
2. pipes the guest's standard input and standard output, and reads standard error as diagnostic text;
3. removes `RUNIC_APPLICATION_SERVE_ONLY` and `RUNIC_APPLICATION_CLOSE_AFTER_OPEN` from the guest's environment and
   passes no secret in its arguments or environment, which other users can read on Linux and macOS
   (`/proc/<pid>/cmdline`, `/proc/<pid>/environ`, `ps`);
4. ties the guest's lifetime to its own: on Windows a job object that kills its processes when the host's handle
   closes, elsewhere a process group of its own that the host ends.

A guest in hosted mode ignores `RUNIC_APPLICATION_SERVE_ONLY` and `RUNIC_APPLICATION_CLOSE_AFTER_OPEN`.

### Lines

The launch contract exchanges three kinds of line over the pipes. Each is one line of UTF-8 text without a byte order
mark, ended by LF or CR LF, at most 4,096 bytes before its end, that starts with its prefix at the start of the line:

| Line | Pipe | Written by | Content |
| --- | --- | --- | --- |
| `RUNIC_HOST_OFFER=<json>` | standard input | host | The [offer](#offer) |
| `RUNIC_HOST_CHANNEL=<json>` | standard output | guest | The [channel](#channel-line) |
| `RUNIC_HOST_REFUSED=<json>` | standard output | guest | A [refusal](#refusal-line) |

`<json>` is one JSON object (I-JSON, UTF-8) on the rest of the line. Both sides ignore members of it they do not know.

- The host writes the offer as the first line of the guest's standard input, and then writes nothing more to it. It
  keeps standard input open for the whole host session: the guest reads its end as the host going away
  ([host gone](#host-gone)).
- The guest writes exactly one channel line or one refusal line to standard output, as one write. Its standard output
  is also its console, so other lines, such as its own logging, may come before and after; the host reads every line,
  acts on the first line with one of the two prefixes and treats the others as diagnostic text. A guest in hosted mode
  writes no `RUNIC_APPLICATION_URL=` or `RUNIC_APPLICATION_WINDOW=` lines: its Windows' addresses travel over the
  channel.
- The host MUST NOT log the offer or the channel line, which carry secrets, and SHOULD log the guest's other output
  lines and standard error at a diagnostic level.

### Offer

```text
RUNIC_HOST_OFFER={"protocols":[1],"nonce":"q2Tz0bB7m5PpY4m1yq3HnN4vL3dQK8bS0w2a6e9JtXc","host":{"name":"Runic Control Center","version":"2.1.0"}}
```

| Member | Contract |
| --- | --- |
| `protocols` | The protocol versions the host implements, an array of 1 to 16 distinct positive integers. |
| `nonce` | 32 bytes from a cryptographically secure random generator, new for each guest process, in base64url without padding (43 characters). It never crosses the channel; both sides prove that they know it ([proofs](#proofs)). |
| `host` | `{name, version}`: the host application's name (1 to 128 characters) and version (1 to 64 characters). Informational, and a filter for `AcceptHost`, not a credential. |

The guest reads the offer before it opens its listener. It waits for it up to its start timeout (30 seconds by
default): when standard input ends or the time passes first, the guest writes no line and exits with a failure. An
offer that is not a JSON object with these members in range is refused with `offer-invalid`.

### Channel line

The guest chooses the protocol version, the highest version of the offer's `protocols` it implements, opens its
[channel listener](#channel-listener) and writes:

```text
RUNIC_HOST_CHANNEL={"url":"ws://127.0.0.1:41234/_runic_host","key":"Jm8pXcQ0yB4rT6wZ1aN3dF5hK7lP9sV2uE4iO6gH8jA","protocol":1}
```

| Member | Contract |
| --- | --- |
| `url` | The listener's address: `ws://`, an IPv4 or IPv6 loopback literal (`127.0.0.1`, `[::1]`), its port, and the path `/_runic_host`. |
| `key` | The channel key: 32 bytes from a cryptographically secure random generator, new for each guest process, in base64url without padding (43 characters). |
| `protocol` | The protocol version the guest chose from the offer. |

The host MUST refuse a channel line whose `url` is not of that form, whose `key` is not 43 base64url characters, or
whose `protocol` was not in its offer: the guest is not hostable.

### Refusal line

A guest that cannot be hosted with this offer writes a refusal instead of a channel line, opens no listener and exits
with a non-zero exit code:

```text
RUNIC_HOST_REFUSED={"code":"protocol-unsupported","message":"The host offers runic-host versions 2; this application implements 1.","protocols":[1]}
```

| Member | Contract |
| --- | --- |
| `code` | Why, a kebab-case token from the table below. Open: a host treats a code it does not know as the guest not being hostable. |
| `message` | A human-readable explanation for the host's log, at most 1,024 characters. |
| `protocols` | With `protocol-unsupported`: the versions the guest implements. |

| `code` | The guest refuses because |
| --- | --- |
| `offer-invalid` | The offer is not a JSON object with valid `protocols`, `nonce` and `host`. |
| `protocol-unsupported` | It implements none of the offer's `protocols`. |
| `host-not-accepted` | The application's own filter (`RunicGuestOptions.AcceptHost`) declined the offer's `host`. |
| `guest-elevated` | It runs elevated: as root on Linux or macOS, or with an elevated token on Windows. |
| `guest-other-user` | It runs as another user than the process that started it, where the platform can tell. |
| `network-exposure` | Its Desktop host is configured to listen on other interfaces than loopback (`DesktopHostOptions.NetworkExposure`). |

## Channel listener

The guest opens the host channel on a listener of its own, separate from the listeners of its Windows' surfaces.

- It listens on one loopback literal, `127.0.0.1` or `::1`, on a port the operating system chooses, and never on
  another interface.
- It answers only an HTTP/1.1 WebSocket upgrade (RFC 6455) of `GET /_runic_host`: a request without a body,
  `Upgrade: websocket`, `Connection: Upgrade`, `Sec-WebSocket-Version: 13` and a valid `Sec-WebSocket-Key`. It answers
  another path with 404, another method with 405, and a malformed upgrade with 400, and closes the connection.
- It refuses, with 403, an upgrade that carries an `Origin` header. Browsers send one with every WebSocket upgrade, so
  no web page can open the channel, through DNS rebinding or otherwise. It refuses, with 403, an upgrade whose `Host`
  header is not the listener's own address and port as a loopback literal (`127.0.0.1:41234`, `[::1]:41234`).
- It negotiates no subprotocol and no extension: an upgrade that offers `Sec-WebSocket-Protocol` or
  `Sec-WebSocket-Extensions` is answered without them.
- It reads at most 8 KiB of request headers, and closes a connection whose upgrade has not arrived within 10 seconds.
- It holds at most 8 connections that have not completed the [handshake](#handshake) and closes further ones at once.
- When one connection completes the handshake, the guest stops listening at once and closes every other connection
  that has not completed it. The channel then has exactly one connection, and a later connection to the port is
  refused by the operating system.

## Handshake

The host connects to the channel line's `url` and sends `hello` as the first message. The guest answers it, and the
channel is open.

```json
{"id":1,"kind":"hello","protocol":1,"key":"Jm8pXcQ0yB4rT6wZ1aN3dF5hK7lP9sV2uE4iO6gH8jA","proof":"0Wb7oJ2c8F3sQ1nV5yK9xT4mL6pR2dH8aE0uG7iZ3cY","host":{"name":"Runic Control Center","version":"2.1.0"},"features":["menus/1","dialogs/1"],"locale":"de-CH","languages":["de-CH","en"],"appearance":{"colorScheme":"dark","contrast":"no-preference","reducedMotion":false,"textScale":1,"accentColor":"#3584e4","systemColors":null}}
{"id":1,"ok":true,"protocol":1,"proof":"rX4kN8bV1cQ7mZ2sT9yH3wJ6pL0dF5gA8eU1iO4nB7k","guest":{"name":"Tool","version":"1.4.0","applicationId":"com.example.tool"},"features":["menus/1"]}
```

`hello` members:

| Member | Contract |
| --- | --- |
| `protocol` | The channel line's `protocol`. |
| `key` | The channel line's `key`. |
| `proof` | The host's [proof](#proofs) that it knows the offer's nonce. |
| `host` | The offer's `host`. |
| `features` | The [features](#features) the host implements: an array of at most 64 strings of at most 64 characters. Absent means none. |
| `locale`, `languages` | The host's application locale, a BCP 47 tag or `null`, and its preferred languages, an array of at most 32 tags: the guest's initial [locale](#appearance-and-locale). |
| `appearance` | The host's appearance, an object with the members of the [`appearance` push](#appearance-and-locale), or `null`. |

Reply members:

| Member | Contract |
| --- | --- |
| `protocol` | The protocol version, as in `hello`. |
| `proof` | The guest's [proof](#proofs) that it knows the offer's nonce. |
| `guest` | `{name, version, applicationId}`: the guest application's name (1 to 128 characters), version (1 to 64 characters) and application id (1 to 255 characters, or `null`), such as its reverse-DNS identity. The host may key what it keeps about a guest, such as saved window placements, by `applicationId`. |
| `features` | Every feature the guest implements, whether or not `hello` listed it. |

### Proofs

The host and the guest each prove that they know the offer's nonce without sending it. A proof is HMAC-SHA256 (RFC
2104) keyed with the nonce's 32 bytes, over the ASCII text of a label, one space and the channel key as it appears in
the channel line, in base64url without padding (43 characters):

| Proof | Label | Sent in |
| --- | --- | --- |
| host proof | `runic-host/1 host` | `hello` |
| guest proof | `runic-host/1 guest` | the successful `hello` reply |

For example, `proof` in `hello` is `base64url(HMAC-SHA256(nonce, "runic-host/1 host " + key))`. With the nonce
bytes 0 to 31 (`AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`) and the key of bytes 32 to 63
(`ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8`), the host proof is
`yimS5xcNM_YD_FItXRTnVjS5J28tOoyGrJDDyVNWtLc` and the guest proof `_GCMgx2rs30ncRjekFxud93sLUfLdj2fZ08dXHOFyOI`.

- The **key** proves to the guest that the connection comes from the process that read its standard output, and the
  **host proof** that it comes from the process that wrote the offer: both are the parent that started it. A local
  process that learned one of the two still cannot open the channel.
- The **guest proof** proves to the host that the listener belongs to the process it started, not to another process
  that took the port after the guest exited: the host's `hello` gives such a process the key and the host proof, but
  neither lets it compute the guest proof.

### Guest rules

- The first message of a connection MUST be a `hello` text message. The guest checks `key` and `proof` first, each in
  constant time. A connection whose first message is not a `hello`, or whose `key` or `proof` does not match, is closed
  with WebSocket status 1008 and no reply: a party that cannot prove itself learns nothing, not even which check
  failed. The listener keeps listening, within its [limits](#channel-listener).
- After the checks, a `hello` whose `protocol` is not the channel line's is answered
  `{"id":1,"ok":false,"protocol":1,"error":{"kind":"failed","reason":"protocol-mismatch","message":"…"}}` and the
  connection is closed with status 1002. A `hello` that misses a required member or has one of the wrong type is
  answered `rejected` with `invalid-request` and closed with status 1002. Either way the key is spent: the guest stops
  listening, writes nothing more to standard output and exits with a failure, as for a [host gone](#host-gone).
- A `hello` that passes is answered with the successful reply, which carries the guest proof. The key is then spent:
  the guest stops listening and closes its other connections ([channel listener](#channel-listener)). A later `hello`
  on the channel is answered `rejected` with `invalid-request`.
- The guest waits for an authenticated `hello` up to its start timeout, counted from when it read the offer. When the
  time passes first, it stops listening, writes nothing more and exits with a failure.

### Host rules

- The host sends nothing before `hello`, and nothing but `hello` until its reply arrived.
- It checks the reply's `proof` in constant time before it acts on any other member or message. A reply without the
  matching guest proof, a reply of another `protocol`, or a connection that closes first means that the guest is not
  hostable: the host closes the connection and ends the guest process.
- It waits for the channel line and the reply together up to its start timeout (30 seconds by default). After a
  timeout, a refusal line, an early exit of the guest or a failed check, it ends the guest's process tree and reports
  the failure ([errors](#host-start-failures)).

## Messages

After the handshake both sides send messages: **requests**, which the other side answers with one **reply**, and
**pushes**, which have no reply.

- **Frames.** Every message is one JSON object (I-JSON, UTF-8, nesting depth at most 64) in one WebSocket text message
  of at most 1 MiB (1,048,576 bytes). A side that receives a binary message closes the channel with status 1003, a
  larger message with status 1009, and a text message that is not one JSON object with status 1007.
- **Requests** have an `id` and a `kind`: `{"id":7,"kind":"present","window":"w1"}`. `id` is an integer from 1 to
  2^53−1 that the sender chooses; it MUST NOT reuse the id of one of its requests that has not been answered. The two
  directions' ids are independent. `hello` has the id 1.
- **Replies** have the `id` of their request and `ok`: `{"id":7,"ok":true,…}` or
  `{"id":7,"ok":false,"error":{…}}`. A side answers every request exactly once, in any order. A reply whose `id` names
  no request of the receiver that is waiting is ignored.
- **Pushes** have a `kind` and no `id`: `{"kind":"activity","window":"w1","active":true,"minimized":false,"visible":true}`.
- **Tokens.** `kind`, `reason`, `outcome`, `code` and other enumerated values are kebab-case tokens.
- **Unknown members** of a message are ignored. An unknown request kind, including one of a feature that is not in
  use, is answered `rejected` with `invalid-request`; an unknown push kind is ignored.

### Errors

A failed reply's `error` is an object, as in the [Views protocol](../application/README.md#errors):

| Member | Contract |
| --- | --- |
| `kind` | `rejected` (nothing ran), `failed` (it ran and failed, or the side cannot tell) or `cancelled` (the side gave up the request, such as for a window that closed while a dialog was pending). |
| `reason` | Optional. A token that refines `kind`, from the table below. Open: a receiver keeps a reason it does not know. |
| `message` | A human-readable message, at most 1,024 characters. It never carries a key, a nonce, a proof or a launch code. |

| `reason` | `kind` | Sent when |
| --- | --- | --- |
| `invalid-request` | `rejected` | The request is not one of this protocol: an unknown `kind`, a kind of a feature that is not in use, a missing member, a member of the wrong type or out of range, or a `window` that names no window this channel opened. |
| `refused` | `rejected` | The host's policy refused it (`IRunicHostedWindowPolicy`), such as another window, full screen or a dialog. |
| `busy` | `rejected` | The window already shows a dialog ([dialogs](#dialogs-dialogs1)). |
| `capacity` | `rejected` | A [limit](#limits) is reached. |
| `protocol-mismatch` | `failed` | A `hello` named another protocol version. |

### Platform outcomes

Requests that ask the host's platform for something, such as presenting a window, answer the platform's outcome in a
successful reply, because the request was valid and ran: `ok: false` is reserved for requests that are invalid,
refused or failed in the host itself. The outcome follows `PlatformResult`:

| Reply | Meaning |
| --- | --- |
| `{"id":7,"ok":true,"outcome":"success"}` | The platform did it. |
| `{"id":7,"ok":true,"outcome":"unavailable","reason":"backend-unavailable"}` | The platform cannot do it here. `reason`: `provider-not-configured`, `owner-unavailable`, `owner-closed`, `backend-unavailable`; open. |
| `{"id":7,"ok":true,"outcome":"failed","code":"permission-denied","message":"…"}` | The platform tried and failed. `code`: `unknown`, `permission-denied`, `resource-busy`, `invalid-data`, `too-large`, `io-error`, `conflict`, `user-dismissed`, `locked`, `not-found`, `other`; open. `message` is optional. |

A request for a window that the host closed answers `outcome: "unavailable"` with `owner-closed`, not an error, since
the guest may not have learned of the close yet. A receiver reads an unknown `outcome` as `failed` with `unknown`, an
unknown `reason` as `backend-unavailable` and an unknown `code` as `unknown`.

A guest reports an error reply to such a request as a failure of the platform call: `refused` as `permission-denied`,
`busy` and `capacity` as `resource-busy`, and any other error as `unknown`. A request whose channel closed before the
reply, or that it could not send because the host is gone, reports `unavailable` with `owner-closed`.

## Features

The base protocol is every message of this document outside the feature sections. Additions are **features**, named
and negotiated as in the [Views protocol](../application/README.md#features): `{name}/{version}`; each side lists the
features it implements in the `hello` exchange; a feature is in use on the channel when `hello` and its successful reply
both list it; a side ignores a string it does not implement; a side sends a feature's messages and members only when
the feature is in use.

| Feature | Specification |
| --- | --- |
| `menus/1` | The guest's menu bar and context menus as native menus of the hosted window: [menus](#menus-menus1) |
| `dialogs/1` | File dialogs and message boxes owned by the hosted window: [dialogs](#dialogs-dialogs1) |
| `window-services/1` | Clipboard, monitors, opening files and links, dragging files out of and dropping files on a hosted window: [window services](#window-services-window-services1) |

A guest service whose feature is not in use reports `OwnerUnavailable`, as in serve-only mode.

## Windows

### `window-open`

When the guest opens a Window, it creates the Window's surface, starts a one-time browser launch on it and asks the
host for a window:

```json
{"id":4,"kind":"window-open","window":"w1","type":"Tool.MainWindow","url":"http://127.0.0.1:38211/_runic_launch/7sQ2…","title":"Tool","width":1024,"height":768,"minWidth":480,"minHeight":320,"owner":null,"modality":"none","guardClose":true,"permissions":[]}
{"id":4,"ok":true,"features":["present","minimize","visibility","full-screen","color-scheme","activity","owned-windows"],"activity":{"active":true,"minimized":false,"visible":true},"colorScheme":"no-preference"}
```

| Member | Contract |
| --- | --- |
| `window` | The window id: a string of 1 to 128 characters from U+0021 to U+007E, new on the channel. The guest never reuses an id, also not after the window closed. |
| `type` | The Window's type, as the serve-only window line names it (`MyApp.DocumentWindow`1[Report]`), at most 512 characters. Informational: the host's policy and placement may key on it. |
| `url` | The page's one-time launch address: `http://` on a loopback literal and the surface's port, with a path that ends in `/_runic_launch/<code>` (a surface of a shared listener has a path of its own before it), at most 2,048 characters. The first request with it binds the surface to the hosted window's web view ([page admission](#page-admission)). |
| `title` | Optional. The title the window shows until the page sets `document.title`, at most 1,024 characters. |
| `width`, `height` | Optional. The requested size of the window's content in device-independent pixels, integers from 1 to 32,767. |
| `minWidth`, `minHeight` | Optional. The smallest size of the window's content, as above. |
| `owner` | Optional. The id of an open window of this channel that owns the new one, or `null`. A window can only be owned by one of the guest's own windows. |
| `modality` | Optional. `"none"` (the default) or `"window"`: the new window is modal to its `owner`. Requires `owner`. |
| `guardClose` | Optional, `false` by default. Whether the Window decides about its close: a user close of the hosted window then asks the guest with [`close-request`](#close-request) first. |
| `permissions` | Optional. The permissions the page asks for: an array of tokens, `media-capture` (camera and microphone). The host's policy grants or refuses each; an unknown token is ignored. |

The host's policy decides (`IRunicHostedWindowPolicy`; by default every window is allowed, D055.8). The host opens a
native window, navigates it to `url` and answers:

| Member | Contract |
| --- | --- |
| `features` | What the hosted window can do, the presentation [requests](#presentation) the host serves for it: `present`, `minimize`, `visibility`, `full-screen`, `document-edited`, `color-scheme`, `per-window-color-scheme`, `activity`, `owned-windows`. Open. They map to `RunicWindowPresentationFeatures`. |
| `activity` | The window's activity, as in the [`activity` push](#activity-and-placement). |
| `colorScheme` | Optional. The window's colour scheme, as in the [`color-scheme` push](#activity-and-placement). |

A refusal is answered `rejected` with `refused`, and the guest's open fails (`RunicWindowConfigurationException` with
the code `hosted-window-refused`); the guest closes the Window's surface. A `window` already used, an `owner` that is
not an open window of the channel, or a `url` not of the form above is `rejected` with `invalid-request`. The guest
treats the window as open when the reply arrives; when its Desktop options wait for the page's connection, its open
also waits for the hosted window's page to connect to the surface, up to its connection timeout. The host may send
`close-request` or `shutdown` as soon as it sent the reply; a guest whose Window has not finished opening closes it once
it has.

### `window-closed`

When a guest Window closes, for any reason, the guest pushes `{"kind":"window-closed","window":"w1"}` after the Window
finished closing, and the host closes the hosted window. The host MUST close it without asking anyone, and MUST NOT
reuse the id. A guest that closes its Window while its `window-open` is waiting for the reply pushes `window-closed`
after the reply arrived.

### `close-request`

When the user closes a hosted window, or the host's application asks to close it, the host asks the guest:

```json
{"id":9,"kind":"close-request","window":"w1"}
{"id":9,"ok":true,"allow":true}
```

- A Window with `guardClose` runs its close decision, `RunicWindow.CanCloseAsync` with any confirmation it shows
  through its own page or the proxied dialogs, and answers `allow`. A Window without it answers `allow: true` at once.
- With `allow: true` the guest closes the Window, which pushes `window-closed`; the host closes the hosted window when
  that push arrives, not when the reply does, so the page receives its `closing` pushes in the window. With
  `allow: false` the hosted window stays open.
- With `"force": true` the host closes the window whatever the guest decides, for example because its own application
  is quitting without confirmation: the guest skips the close decision, answers `allow: true` and closes the Window.
- A `close-request` for a window that is closing or closed is answered `allow: true`. A second `close-request` for a
  window whose decision is pending waits for the same decision.
- `RunicWindow.RequestCloseAsync` in the guest asks the same decision locally: the guest runs the guard and, when it
  allows, closes the Window, which pushes `window-closed`.

### `shutdown`

The host ends the host session with `{"id":12,"kind":"shutdown","reason":"stop"}`. `reason` is informational:
`stop` (the host application stopped the guest), `host-exit` (the host is exiting) or another token. The guest
answers `{"id":12,"ok":true}` at once and then shuts down as when its desktop asks: it cancels
`DesktopHost.ShutdownRequested`, its Windows close, each with its `window-closed` push, and `Run` returns when its body
does. The host waits for the process to exit up to its stop timeout (10 seconds by default) and then ends its process
tree. A host that wants each Window's guard to decide first sends `close-request` for each window before `shutdown`.

## Presentation

The guest's `IRunicWindowPresentation` of a hosted Window sends the requests of the base protocol; each names its
`window` and is answered with a [platform outcome](#platform-outcomes). A request whose presentation feature the
`window-open` reply did not list is answered `outcome: "unavailable"` with `backend-unavailable`; the guest does not
send it.

| Request | Members | Feature |
| --- | --- | --- |
| `present` | — | `present` |
| `minimize` | — | `minimize` |
| `set-visible` | `visible`: boolean | `visibility` |
| `set-full-screen` | `fullScreen`: boolean | `full-screen` |
| `set-document-edited` | `edited`: boolean | `document-edited` |
| `set-color-scheme` | `colorScheme`: `"light"`, `"dark"` or `"no-preference"` | `color-scheme` |
| `toggle-maximized` | — | — |
| `resize` | `width`, `height`: integers from 1 to 32,767 | — |
| `move` | `x`, `y`: integers from −32,768 to 32,767, in the coordinates of the host's desktop | — |
| `set-represented-file` | `path`: an absolute path or `null` | — |
| `set-layout-direction` | `direction`: `"default"`, `"left-to-right"` or `"right-to-left"` | — |
| `announce` | `text`: 1 to 1,024 characters; `priority`: `"polite"` or `"assertive"` | — |

The host's policy can refuse or clamp each request: a refused one is answered `rejected` with `refused`, a clamped one,
such as a size above the policy's maximum, applies the clamped value and answers `success`. The host presents with its
own activation rights: on Wayland the host, not the guest, holds the activation token, so the hosted window gets focus
as the host's own windows do.

### Activity and placement

The host pushes what the guest's presentation reports:

| Push | Members | When |
| --- | --- | --- |
| `activity` | `window`; `active`, `minimized`, `visible`: booleans | The window's activity changed. Requires the `activity` window feature. |
| `placement` | `window`; `bounds`: `{x, y, width, height}`; `maximized`, `fullScreen`: booleans; `monitor`: a string or `null` | The window moved, was resized, maximized or entered or left full screen. |
| `color-scheme` | `window`; `colorScheme`: `"light"`, `"dark"` or `"no-preference"` | The window's colour scheme changed, also through another window or the host's application. |

A push for a window the guest already closed is ignored. The pushes of one window arrive in the order the host sent
them.

## Appearance and locale

The guest follows the host's appearance and locale by default (D055.8): the host's `hello` carries the initial values,
and the host pushes each change.

| Push | Members |
| --- | --- |
| `appearance` | The members of the Views protocol's [`appearance` push](../application/README.md#appearance): `colorScheme`, `contrast`, `reducedMotion`, `textScale`, `accentColor`, `systemColors`. Every push carries every member; `null` means unknown. |
| `locale` | `locale`: a BCP 47 tag or `null`; `languages`: an array of at most 32 tags. |

The guest's Windows push the host's appearance to their pages (`appearance/1`), since the hosted windows show on the
host's desktop: a guest is not serve-only for the locality rule. The guest's application locale
(`IRunicApplicationLocale`) follows `locale`, unless the guest application set a locale of its own. A guest that does
not implement them yet ignores both pushes and keeps its own.

## Menus (`menus/1`)

With `menus/1`, a hosted window shows the guest's menu bar as the host's native menu bar, and the guest's context
menus as native menus.

| Message | Direction | Members | Reply |
| --- | --- | --- | --- |
| `set-menu-bar` | guest → host request | `window`; `menu`: an array of [menu entries](#menu-entries), or `null` for none | `{}` |
| `update-menu-items` | guest → host request | `window`; `items`: an array of `{id, title?, enabled?, checked?}` | `{}` |
| `show-context-menu` | guest → host request | `window`; `menu`: an array of menu entries; `point`: `{x, y}` in the window's content coordinates, or `null` for the pointer's position | `{itemId}`: the chosen item's id, or `null` when the menu was dismissed |
| `menu-invoke` | host → guest push | `window`; `itemId` | — |

### Menu entries

An entry is one of:

- `{"kind":"item","id":"file.save","title":"Save","accelerator":"CmdOrCtrl+S","enabled":true,"checked":null,"role":null}`.
  `id` is 1 to 128 characters and unique in the menu; `accelerator` follows the
  [shortcut text](fixtures/shortcuts.json); `checked` is `true`, `false` or `null` (not a check item); `role` is a
  `DesktopMenu` role token or `null`.
- `{"kind":"separator"}`.
- `{"kind":"submenu","title":"File","role":null,"items":[…]}`.

A menu is at most 8 levels deep and has at most 1,000 entries in all; a larger one is `rejected` with `capacity`.

- **Placement.** On macOS the host's process owns the one menu bar: while a hosted window is key, the host shows its
  own application menu, whose About and Quit stay the host's, then the guest's menus; a guest `Window` or `Help` menu
  is merged with the host's by its role. When a host window becomes key, the host's own menus return. On Windows and
  Linux each hosted window shows the guest's bar.
- **Shortcuts.** Accelerators belong to the host's native menu. When the user chooses an item, or presses its
  accelerator, the host pushes `menu-invoke`, and the guest runs the item's command as its own menu bar would.
- `itemId` of a `menu-invoke` that names no current item of the window is ignored.

## Dialogs (`dialogs/1`)

With `dialogs/1`, the guest's `IFileDialogs` and `IDesktopMessageBoxes` of a hosted Window ask the host, which shows
each dialog owned by the hosted window, with its own activation rights.

| Request | Members | Reply |
| --- | --- | --- |
| `file-dialog` | `window`; `dialog`: `"open"`, `"open-multiple"`, `"folder"` or `"save"`; `title`, `defaultPath`, `suggestedName`: optional strings; `filters`: optional array of `{name, patterns}` with patterns such as `"*.txt"` | A [platform outcome](#platform-outcomes); with `success`, `paths`: an array of absolute paths (one, except for `open-multiple`). A dismissed dialog is `failed` with `user-dismissed`. |
| `message-box` | `window`; `kind`: `"info"`, `"warning"`, `"error"` or `"question"`; `title`, `message`, `detail`: strings, `message` required; `buttons`: 1 to 8 `{id, label, default?, cancel?}` | A platform outcome; with `success`, `button`: the chosen button's `id`, or the `cancel` button's when dismissed. |

- A window shows one dialog at a time. A dialog request for a window that shows one is `rejected` with `busy`, as
  `PlatformFailureCode.ResourceBusy` on the guest's side.
- When the window closes while its dialog is open, the host closes the dialog and answers the request `cancelled`.
- The guest acts only on the paths a file dialog returns, as with its own dialogs. A host can return any path: a
  guest cannot tell a dialog the user answered from a forged reply ([security](#security-requirements)).

## Window services (`window-services/1`)

With `window-services/1`, the guest's clipboard, monitors, file launcher and drag and drop of a hosted Window ask the
host. Without it they report `OwnerUnavailable`.

| Message | Direction | Members | Reply |
| --- | --- | --- | --- |
| `clipboard-read` | guest → host request | `window`; `format`: `"text"` | Outcome; with `success`, `text`: a string or `null` |
| `clipboard-write` | guest → host request | `window`; `text`: a string of at most 512 KiB | Outcome |
| `monitors` | guest → host request | `window` | Outcome; with `success`, `monitors`: an array of `{id, bounds, workArea, scale, primary}` |
| `launch` | guest → host request | `window`; `uri` (absolute `http`, `https`, `mailto` or `ms-settings`) or `path` (absolute); `operation`: `"open"`, `"reveal"` or `"choose-application"` | Outcome |
| `start-drag` | guest → host request | `window`; `files`: an array of 1 to 256 absolute paths | Outcome; with `success`, `effect`: `"none"`, `"copy"`, `"move"` or `"link"` |
| `files-dropped` | host → guest push | `window`; `paths`: an array of absolute paths; `point`: `{x, y}` in the window's content coordinates | — |

The host launches with its own activation token, applies the same checks as `IDesktopFileLauncher` (link schemes,
`mailto` attachments, executable types) and its policy.

## Lifecycle

### Host gone

The guest treats either of these as the host going away: its standard input reaches its end, or the host channel
closes, for any reason, after the handshake. It then shuts down as for [`shutdown`](#shutdown): it cancels
`DesktopHost.ShutdownRequested`, its Windows close and `Run` returns when its body does. A guest MUST NOT fall back to
showing a window of its own, and a Window that opens after the host went away fails to open. When the body has not
returned within 10 seconds, the guest's process exits. The host's job object or process group is the backstop.

### Guest gone

The host treats either of these as the guest going away: the host channel closes, or the guest process exits. It
closes the guest's hosted windows (an application may show its own error page instead, D055.8), ends the guest's
process tree if it still runs, and reports the exit code (`RunicHostedApplication.Exited`).

### Page reloads and crashes

A page reload or a crash of the web content process is handled inside the hosted window, as for any Runic Desktop
window: the page reconnects to the guest's surface. Nothing crosses the host channel, and the launch binding keeps
admitting the hosted window's web view.

## Page admission

The hosted window's page connects to the guest's surface directly; the host never sees its traffic.

- The guest starts one launch binding for each `window-open`, as for an installed browser: the first request with the
  `url` binds the surface to that client with an `HttpOnly; SameSite=Strict` cookie and redirects it to the page, and
  the surface then admits that client alone. The surface's session credential never crosses the channel.
- The host opens the hosted window with a web view data store of its own for each host session, not persisted, so the
  guest's surface never receives the host's cookies, and two guests never share storage (D055.8).
- The host loads the `url` top-level. It injects no credential into the guest's documents, does not add the guest's
  origin to its own `AdditionalOrigins`, and limits top-level navigation to the guest's origin; links elsewhere open in
  the default browser.

## Limits

| Limit | Value | Over it |
| --- | --- | --- |
| Message size | 1 MiB | Channel closed with status 1009 |
| A side's requests waiting for replies | 64 | `rejected` with `capacity` |
| Open windows of a guest | The host's policy | `window-open` `rejected` with `refused` |
| Requests per window | The host's policy, such as a rate | `rejected` with `capacity` |
| Dialogs per window | 1 | `rejected` with `busy` |
| Menu depth and entries | 8 levels, 1,000 entries | `rejected` with `capacity` |
| Unauthenticated connections | 8 at once, 10 seconds each | Connection closed |
| Start (offer to `hello` reply) | 30 seconds by default, on both sides | Guest exits; host ends the guest |
| Stop (`shutdown` to exit) | 10 seconds by default | Host ends the guest's process tree |
| Host gone to exit | 10 seconds | Guest process exits |

## Host start failures

A host reports a guest that it cannot host with one failure code (`RunicHostedApplicationException.Code` in .NET).
The vocabulary is open.

| Code | Cause |
| --- | --- |
| `guest-start-timeout` | No channel line, or no `hello` reply, within the start timeout. |
| `guest-exited` | The guest exited before the `hello` reply. |
| `guest-not-hostable` | The guest wrote an invalid channel line, the channel refused the connection, or the guest proof did not match. |
| `guest-refused` | The guest wrote a refusal line; the failure names the guest's `code`. |
| `protocol-unsupported` | The guest refused with `protocol-unsupported`, or answered `protocol-mismatch`. |

A guest reports a hosted start that failed with the same exception type, before its application body runs: the code
of its refusal, or `offer-missing` (standard input ended or the start timeout passed before the offer),
`handshake-timeout` (no authenticated `hello` in time), `host-gone` (standard input ended during the handshake),
`protocol-unsupported` (an authenticated `hello` of another version) or `handshake-invalid` (an authenticated `hello`
that is not valid).

## Versions and compatibility

- The `protocol` integer changes only for a change that an implementation of the previous version could misread.
  Additions are features, or members that the other side ignores.
- The offer lists every version the host implements, and the guest chooses the highest one it also implements, so a
  host and a guest of different releases connect as long as they share a version.
- Within a version, both sides ignore members they do not know, answer an unknown request kind with
  `invalid-request` and ignore an unknown push kind. Refusal codes, error reasons, outcome reasons and codes, window
  features and failure codes are open sets.

| Version | Release | Change |
| --- | --- | --- |
| 1 | 0.17 | First version, experimental (`RUNICDESK103`). |

## Security requirements

A Runic host and guest MUST meet these requirements; the [threat model](#threats) explains them. What they mean for an
application is in the [security guide](../../docs/security.md#hosted-application-windows).

1. **Opt-in on both sides.** A guest enters hosted mode only through a code path of its own; a host starts a guest
   only through an explicit call. No environment variable or well-known flag activates either.
2. **Secrets on pipes only.** The nonce travels only on the guest's standard input and the key only on its standard
   output. Neither appears in a command line, an environment variable, a file or a log. Launch codes travel only over
   the authenticated channel.
3. **Loopback only.** The channel listener and the guest's surfaces listen on loopback literals. A guest whose Desktop
   host listens on other interfaces refuses hosted mode.
4. **Fresh secrets.** The nonce and the key are 32 bytes from a cryptographically secure random generator, new for
   each guest process. Keys, proofs and codes are compared in constant time.
5. **Mutual proof.** The guest accepts a channel only with the key and a valid host proof; the host trusts a channel
   only after a valid guest proof.
6. **One channel.** The guest accepts exactly one authenticated connection and stops listening at once. It closes
   unauthenticated connections without a reply, and holds at most 8 of them, each for at most 10 seconds.
7. **No web pages.** The guest refuses an upgrade with an `Origin` header and one whose `Host` is not its loopback
   address and port.
8. **No elevated guests.** A guest refuses hosted mode when it runs elevated, or as another user than its parent where
   the platform can tell, because a host controls what it does with the user's answers.
9. **Least privilege.** The channel gives a guest its own windows only: no list of the host's windows, no script in
   the host's pages, no navigation outside its own origin. Window ids are scoped to the channel, and `owner` names only
   the guest's own windows. The host validates every message and applies its policy and limits.
10. **Separate pages.** A hosted window injects no credential into the guest's page, does not admit the guest's origin
    as one of its own and uses a data store of its own for the host session.
11. **Lifetime.** The guest exits when the host goes away and never shows a window of its own; the host ends the
    guest's process tree when it stops.

### Threats

| # | Threat | Mitigation |
| --- | --- | --- |
| T1 | Another local user connects to the channel or to a guest surface | Loopback listener, 256-bit key and host proof, one connection; the surfaces' launch binding and their refusal of another user's process (Linux) |
| T2 | A web page in any browser targets the channel (DNS rebinding, cross-site WebSocket) | Upgrades with `Origin` and non-loopback `Host` refused |
| T3 | Secrets leak through the command line or the environment | Nonce and key on pipes only; launch codes over the channel |
| T4 | A malicious guest takes the host's credential or cookies | No credential injection; a data store per host session; the guest's origin is not one of the host's |
| T5 | A malicious guest escapes its window (navigates away, opens pop-ups, spoofs other windows) | The host's web content policy; window ids per channel; the host's policy. A guest can still draw convincing UI in its own window, as any application can. |
| T6 | A guest floods the host with windows, dialogs, menus or messages | Message size, waiting requests, menu size, one dialog per window, the host's window and rate limits |
| T7 | A malicious host forges dialog results or close decisions | Inherent to hosting: a guest trusts its host as it trusts its own native dialogs, so elevated guests refuse hosted mode and a guest acts only on what a dialog returned, as always |
| T8 | Orphaned guests | Standard input end or channel loss shut the guest down, which exits within 10 seconds; job object or process group; the host's stop timeout |
| T9 | Another process takes the port after the guest exited, and the host connects to it | The guest proof, which needs the nonce the host never sends |
| T10 | Version skew or downgrade | The offer's `protocols` and the guest's highest common choice; features negotiated; unknown members ignored and unknown kinds `invalid-request` |
| T11 | A local process that read one of the pipes, such as the guest's standard output in a log, opens the channel first | It needs both the key (standard output) and the host proof (standard input) |

Same-user processes are outside what Runic protects ([security guide](../../docs/security.md#what-runic-protects-and-what-it-does-not)):
any process of the user that knows a guest's hosted parameter can host it, see its pages and answer its dialogs.
`RunicGuestOptions.AcceptHost` filters hosts by their name and version for the product's sake; it is not a security
boundary.
