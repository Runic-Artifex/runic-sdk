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
| This document | Roles, the launch contract (offer, channel and refusal lines), the channel listener, the handshake, messages and errors, features, windows, presentation, appearance and locale, menus, dialogs, window services, regions, lifecycle, page and elevated-peer admission, limits, versions and security requirements |
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
| `elevatedGuest` | Optional. `true` when the host runs elevated and allows an elevated guest (`RunicGuestStartInfo.AllowElevatedGuest`); absent otherwise. Only `true` opts in: another value reads as absent. It is the host's opt-in, not evidence of its elevation, which the guest checks against its parent ([elevated host](#elevated-host)). |

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
| `code` | Why, a kebab-case token from the table below. Open: a host reports a code it does not know as `guest-refused` with that code. |
| `message` | A human-readable explanation for the host's log, at most 1,024 characters. |
| `protocols` | With `protocol-unsupported`: the versions the guest implements. |

| `code` | The guest refuses because |
| --- | --- |
| `offer-invalid` | The offer is not a JSON object with valid `protocols`, `nonce` and `host`. |
| `protocol-unsupported` | It implements none of the offer's `protocols`. |
| `host-not-accepted` | The application's own filter (`RunicGuestOptions.AcceptHost`) declined the offer's `host`. |
| `guest-elevated` | It runs elevated: as root on Linux or macOS, or with an elevated token on Windows; and it does not allow an elevated host, or the offer has no `elevatedGuest` ([elevated host](#elevated-host)). |
| `guest-elevated-host-not-elevated` | It runs elevated and both sides allow an elevated pair, but its parent, of the same user, does not run elevated ([elevated host](#elevated-host)). |
| `guest-other-user` | It runs as another user than the process that started it, or it cannot tell ([same user](#same-user)). |
| `network-exposure` | Its Desktop host is configured to listen on other interfaces than loopback (`DesktopHostOptions.NetworkExposure`). |

### Same user

A guest refuses hosted mode with `guest-other-user` unless it can show that it and its parent process, the host, run
as the same user. The check fails closed: when the guest cannot determine its parent's user, it refuses.

- **Linux.** The guest reads the real and effective uids (`Uid:`, its first two fields) of itself from
  `/proc/self/status` and of its parent (`PPid:`) from `/proc/<ppid>/status`. All four MUST be equal. It refuses when
  its parent id is 0 or 1 (the parent is gone) or the parent's status cannot be read, as under `hidepid=1` or
  `hidepid=2` for another user's process.
- **macOS.** The same four uids: its own from `getuid` and `geteuid`, its parent's (`getppid`) from `proc_pidinfo` with
  `PROC_PIDTBSDINFO` (`pbi_ruid`, `pbi_uid`). It refuses when its parent id is 0 or 1 or `proc_pidinfo` fails.
- **Windows.** The user SID of its process token (`TokenUser`) and of its parent's MUST be equal. It refuses when the
  parent is gone or its token cannot be opened, which is the case for another user's process.

A guest that is setuid to another account fails the check through its effective uid. The check keeps a host from
controlling a guest that has rights the host does not have; it is not a boundary between processes of one user
([security requirements](#security-requirements) 8).

### Elevated host

A guest that runs elevated, as root on Linux and macOS (an effective uid of 0) or with an elevated token on Windows,
refuses hosted mode with `guest-elevated`, unless both sides opted into the one elevated pair: the guest allows an
elevated host (`RunicGuestOptions.AllowElevatedHost`) and the offer has `elevatedGuest: true`
(`RunicGuestStartInfo.AllowElevatedGuest`, which only an elevated host may set). Such a guest is hosted only when its
parent, as the [same user](#same-user) check finds and verifies it, also runs elevated:

- **Linux.** The parent's effective uid (`Uid:`, second field) is 0, from the same read of `/proc/<ppid>/status` as the
  same-user check, which already requires its real and effective uids, and the guest's, to be equal.
- **macOS.** The parent's effective uid (`pbi_uid`) is 0, from the same `proc_pidinfo` call.
- **Windows.** The parent's token, read while the parent is held open and after its creation time showed that it is
  the parent and not a newer process that reused its id, is elevated (`TokenElevation`) or has an integrity level of
  High or above (`TokenIntegrityLevel`). The user SID of an elevated token is that of the unelevated one, so the
  same-user check alone does not tell them apart.

The guest checks in that order: without its own opt-in, or without `elevatedGuest`, it refuses with `guest-elevated`;
a parent that fails the same-user check refuses with `guest-other-user`; a parent of the same user that does not run
elevated refuses with `guest-elevated-host-not-elevated`. Anything it cannot read refuses. The guest never takes the
host's word for its elevation: `elevatedGuest` only opts in. A guest that does not run elevated ignores
`elevatedGuest`. Runic's guest logs the accepted pair as event 2025 and a refusal as event 2010 with its code.

The opt-ins allow what the two processes already share: a host that runs elevated as the user controls the guest's
rights anyway. They do not make same-user processes that do not run elevated part of the pair; what those can still
reach is in the [threat model](#threats) (T18).

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
- It reads at most 8 KiB of request headers, and closes a connection whose upgrade has not arrived within 10 seconds
  of its accept.
- After the upgrade, the first message MUST arrive within 2 seconds (the **hello timeout**); the host sends `hello` at
  once. The guest closes a connection whose first message has not arrived in time.
- Before a connection completes the handshake, the guest reads at most 16 KiB (16,384 bytes) of its first message; a
  valid `hello` is far smaller. A larger message closes the connection with status 1009 without being read to its end.
- It holds at most 8 connections that have not completed the [handshake](#handshake). When a ninth arrives, it closes
  the oldest of them and keeps the new one: a local process that fills the slots cannot keep the host out, because
  the host connects and sends `hello` at once, and [retries](#host-rules) when its connection is closed.
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

`hello` is the host's first request. Its `id` is the host's choice, as for any [request](#messages), and the reply
carries it.

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
| `protocol` | The protocol version the guest chose, the channel line's `protocol`. |
| `proof` | The guest's [proof](#proofs) that it knows the offer's nonce. Every reply to an authenticated `hello` carries it, a failed one too. |
| `guest` | `{name, version, applicationId}`: the guest application's name (1 to 128 characters), version (1 to 64 characters) and application id (1 to 255 characters, or `null`), such as its reverse-DNS identity. The host may key what it keeps about a guest, such as saved window placements, by `applicationId`. |
| `features` | Every feature the guest implements, whether or not `hello` listed it. |

### Proofs

The host and the guest each prove that they know the offer's nonce without sending it. A proof is HMAC-SHA256 (RFC
2104) keyed with the nonce's 32 bytes, over the ASCII text of a label, one space and the channel key as it appears in
the channel line, in base64url without padding (43 characters):

| Proof | Label | Sent in |
| --- | --- | --- |
| host proof | `runic-host/1 host` | `hello` |
| guest proof | `runic-host/1 guest` | every reply to an authenticated `hello`, successful or failed |

For example, `proof` in `hello` is `base64url(HMAC-SHA256(nonce, "runic-host/1 host " + key))`. With the nonce
bytes 0 to 31 (`AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`) and the key of bytes 32 to 63
(`ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8`), the host proof is
`yimS5xcNM_YD_FItXRTnVjS5J28tOoyGrJDDyVNWtLc` and the guest proof `_GCMgx2rs30ncRjekFxud93sLUfLdj2fZ08dXHOFyOI`.

- The **key** proves to the guest that the connection comes from the process that read its standard output, and the
  **host proof** that it comes from the process that wrote the offer: both are the parent that started it. A local
  process that learned one of the two still cannot open the channel.
- The **guest proof** proves to the host that the listener belongs to the process it started, not to another process
  that took the port after the guest exited: the host's `hello` gives such a process the key and the host proof, but
  neither lets it compute the guest proof. A failed reply carries it too, so such a process cannot make the host
  report a failure of its choice either.

### Guest rules

- The first message of a connection MUST be a `hello` text message. The guest checks `key` and `proof` first, each in
  constant time. A connection whose first message is not a `hello`, or whose `key` or `proof` does not match, is closed
  with WebSocket status 1008 and no reply: a party that cannot prove itself learns nothing, not even which check
  failed. The listener keeps listening, within its [limits](#channel-listener).
- After the checks, a `hello` whose `protocol` is not the channel line's is answered
  `{"id":1,"ok":false,"protocol":1,"proof":"…","error":{"kind":"failed","reason":"protocol-mismatch","message":"…"}}`
  and the connection is closed with status 1002. A `hello` that misses a required member or has one of the wrong type
  is answered `{"id":1,"ok":false,"protocol":1,"proof":"…","error":{"kind":"rejected","reason":"invalid-request","message":"…"}}`
  and closed with status 1002; without a valid `id`, its reply's `id` is `null`. Both replies carry the guest proof.
  Either way the key is spent: the guest stops listening, writes nothing more to standard output and exits with a
  failure, as for a [host gone](#host-gone).
- A `hello` that passes is answered with the successful reply, which carries the guest proof. The key is then spent:
  the guest stops listening and closes its other connections ([channel listener](#channel-listener)). A later `hello`
  on the channel is answered `rejected` with `invalid-request`.
- The guest waits for an authenticated `hello` up to its start timeout, counted from when it read the offer. When the
  time passes first, it stops listening, writes nothing more and exits with a failure.

### Host rules

- The host sends nothing before `hello`, and sends `hello` as soon as the upgrade completed. It sends nothing but
  `hello` until its reply arrived.
- It checks the reply's `proof` in constant time before it reads `ok`, `error` or any other member, or acts on another
  message. A reply without the matching guest proof means that the guest is not hostable (`guest-not-hostable`),
  whatever else it says.
- A proven reply with `ok: false` ends the start: with the reason `protocol-mismatch` as `protocol-unsupported`, with
  any other error as `guest-not-hostable`. A proven successful reply of another `protocol` is `guest-not-hostable`.
- When the upgrade is refused or the connection closes before a reply arrived, as when the guest's listener closed it
  to make room for a newer one, the host connects again, within its start timeout, after a short pause that grows from
  100 milliseconds to at most 1 second. It does not retry after a reply, with or without a proof.
- It waits for the channel line and the reply together up to its start timeout (30 seconds by default). After a
  timeout, a refusal line, an early exit of the guest or a failed check, it closes the connection, ends the guest's
  process tree and reports the failure ([errors](#host-start-failures)).

## Messages

After the handshake both sides send messages: **requests**, which the other side answers with one **reply**, and
**pushes**, which have no reply.

- **Frames.** Every message is one JSON object (I-JSON, UTF-8, nesting depth at most 64) in one WebSocket text message
  of at most 1 MiB (1,048,576 bytes). A side that receives a binary message closes the channel with status 1003, a
  larger message with status 1009, and a text message that is not one JSON object with status 1007.
- **Requests** have an `id` and a `kind`: `{"id":7,"kind":"present","window":"w1"}`. `id` is an integer from 1 to
  2^53−1 that the sender chooses; it MUST NOT reuse the id of one of its requests that has not been answered. The two
  directions' ids are independent; the host's `hello` counts as one of its requests.
- **Replies** have the `id` of their request and `ok`: `{"id":7,"ok":true,…}` or
  `{"id":7,"ok":false,"error":{…}}`. A side answers every request exactly once, in any order. A reply whose `id` names
  no request of the receiver that is waiting is ignored.
- **Deadline.** A side SHOULD answer a request within 30 seconds (the **request timeout**). A requester that has had no
  reply by then gives up: the request counts as `failed` on its side, a [platform outcome](#platform-outcomes) as
  `failed` with `unknown`, and its reply, if it comes later, is ignored. Requests that wait for the user are exempt:
  `close-request`, whose guard may ask the user, and the requests of `dialogs/1` and `show-context-menu`.
  `window-open` has the deadline, but the guest keeps waiting for its reply after it gave up
  ([window-open](#window-open)).
- **Reading.** A side keeps reading the channel while it handles what it read: a handler that waits, for a reply, for
  the user or for application code, MUST NOT stop it from reading the messages that follow. It applies one window's
  pushes in the order they arrived.
- **Sending.** A side that cannot send a message within 10 seconds, because the other side stopped reading, closes the
  connection without waiting further; for the guest that is the [host gone](#host-gone).
- **Pushes** have a `kind` and no `id`: `{"kind":"activity","window":"w1","active":true,"minimized":false,"visible":true}`.
- **Tokens.** `kind`, `reason`, `outcome`, `code` and other enumerated values are kebab-case tokens.
- **Unknown members** of a message are ignored. An unknown request kind, including one of a feature that is not in
  use, is answered `rejected` with `invalid-request`; an unknown push kind is ignored.

### Errors

A failed reply's `error` is an object, as in the [Views protocol](../application/README.md#errors):

| Member | Contract |
| --- | --- |
| `kind` | `rejected` (nothing ran), `failed` (it ran and failed, or the side cannot tell) or `cancelled` (the requester gave the request up, such as a dialog its [`cancel` push](#giving-up-a-request) closed). |
| `reason` | Optional. A token that refines `kind`, from the table below. Open: a receiver keeps a reason it does not know. |
| `message` | A human-readable message, at most 1,024 characters. It never carries a key, a nonce, a proof or a launch code. |

| `reason` | `kind` | Sent when |
| --- | --- | --- |
| `invalid-request` | `rejected` | The request is not one of this protocol: an unknown `kind`, a kind of a feature that is not in use, a missing member, a member of the wrong type or out of range, or a `window` that names no window this channel opened. |
| `refused` | `rejected` | The host's policy refused it (`IRunicHostedWindowPolicy`), such as another window, full screen or a dialog. |
| `busy` | `rejected` | The window already shows a dialog, or the guest shows as many as the host allows at once or for now ([dialogs](#dialogs-dialogs1)). |
| `capacity` | `rejected` | A [limit](#limits) is reached, such as a menu larger than [menus](#menu-entries) allow. |
| `embedding-unsupported` | `rejected` | A `region-attach` names an `embedding` the guest does not implement ([regions](#region-attach)). |
| `cross-site-unavailable` | `failed` | The guest cannot serve a region's frame cross-site to the host page, and the host did not allow it to fall back ([guest origin](#guest-origin)). |
| `protocol-mismatch` | `failed` | A `hello` named another protocol version. |

### Platform outcomes

Requests that ask the host's platform for something, such as presenting a window, answer the platform's outcome in a
successful reply, because the request was valid and ran: `ok: false` is reserved for requests that are invalid,
refused or failed in the host itself. The outcome follows `PlatformResult`:

| Reply | Meaning |
| --- | --- |
| `{"id":7,"ok":true,"outcome":"success"}` | The platform did it. |
| `{"id":7,"ok":true,"outcome":"unavailable","reason":"backend-unavailable"}` | The platform cannot do it here. `reason`: `provider-not-configured`, `owner-unavailable`, `owner-closed`, `backend-unavailable`, `embedded` (the window shows in a [region](#regions-region1), where the request does not apply); open. |
| `{"id":7,"ok":true,"outcome":"failed","code":"permission-denied","message":"…"}` | The platform tried and failed. `code`: `unknown`, `permission-denied`, `resource-busy`, `invalid-data`, `too-large`, `io-error`, `conflict`, `user-dismissed`, `locked`, `not-found`, `other`; open. `message` is optional. |

A request for a window that the host closed answers `outcome: "unavailable"` with `owner-closed`, not an error, since
the guest may not have learned of the close yet. A receiver reads an unknown `outcome` as `failed` with `unknown`, an
unknown `reason` as `backend-unavailable` and an unknown `code` as `unknown`.

A guest reports an error reply to such a request as a failure of the platform call: `refused` as `permission-denied`,
`busy` and `capacity` as `resource-busy`, and any other error as `unknown`. A request whose channel closed before the
reply, or that it could not send because the host is gone, reports `unavailable` with `owner-closed`. A request without
a reply within the [request timeout](#messages) reports `failed` with `unknown`.

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
| `window-services/1` | The host's clipboard, its monitors, opening files and links, and files dropped onto a hosted window: [window services](#window-services-window-services1) |
| `region/1` | A guest Window shown in a region of a host page, in a frame, instead of in a window of its own: [regions](#regions-region1) |
| `region-component/1` | A guest Window shown in a region as a component in the host page's own document, for applications of the same vendor; a guest lists it only when its application allows it: [component mode](#component-mode-region-component1) |

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
| `locale` | Optional. The Window's locale, a BCP 47 tag of 1 to 64 letters, digits and `-` that starts with a letter. The host gives the window the locale's direction (right to left for Arabic, Hebrew and the other right-to-left scripts) unless its own window options fix one; [appearance and locale](#appearance-and-locale). |
| `fileDrops` | Optional, `false` by default. Whether the Window takes files the user drops onto it: with [`window-services/1`](#window-services-window-services1) in use, the host then forwards each drop with `files-dropped`. |

The host's policy decides (`IRunicHostedWindowPolicy`; by default every window is allowed, D055.8). The host opens a
native window, navigates it to `url` and answers:

| Member | Contract |
| --- | --- |
| `features` | What the hosted window can do, the presentation [requests](#presentation) the host serves for it: `present`, `minimize`, `visibility`, `full-screen`, `document-edited`, `color-scheme`, `per-window-color-scheme`, `activity`, `owned-windows`, `maximize`, `placement`. Open. They map to `RunicWindowPresentationFeatures`. |
| `activity` | The window's activity, as in the [`activity` push](#activity-and-placement). |
| `placement` | Optional, with the `placement` feature. The window's placement when it opened: `restored`, `maximized` and `fullScreen`, as in the [`placement` push](#activity-and-placement). The guest's `Placement` starts with it. |
| `colorScheme` | Optional. The window's colour scheme, as in the [`color-scheme` push](#activity-and-placement). |
| `presentation` | Optional, with [`region/1`](#regions-region1) in use. `"window"`, the default, or `"region"`: the window shows in a region of a host page ([presentation of a window](#presentation-of-a-window)). |
| `region` | With `presentation: "region"`: the region's size, or `null` ([presentation of a window](#presentation-of-a-window)). |

A refusal is answered `rejected` with `refused`, and the guest's open fails (`RunicWindowConfigurationException` with
the code `hosted-window-refused`); the guest closes the Window's surface. A `window` already used, an `owner` that is
not an open window of the channel, or a `url` not of the form above is `rejected` with `invalid-request`. The guest
treats the window as open when the reply arrives; when its Desktop options wait for the page's connection, its open
also waits for the hosted window's page to connect to the surface, up to its connection timeout. The host may send
`close-request` or `shutdown` as soon as it sent the reply; a guest whose Window has not finished opening closes it once
it has.

The guest's open can end before the reply arrives: the application cancelled it, the [request timeout](#messages)
passed (`hosted-window-failed`), or the host went away (`hosted-host-gone`). The guest then closes the Window's surface
at once, but keeps waiting for the reply while the channel is open: a successful reply that arrives later is followed
by `window-closed` for that window, so the host never keeps a window whose Window is gone. A host that sees
`window-closed` right after its successful reply closes the window as for any `window-closed`.

### `window-closed`

When a guest Window closes, for any reason, the guest pushes `{"kind":"window-closed","window":"w1"}` after the Window
finished closing, and the host closes the hosted window. The host MUST close it without asking anyone, and MUST NOT
reuse the id. A guest that closes its Window, or gives up its open, while its `window-open` is waiting for the reply
pushes `window-closed` after a successful reply arrived, and nothing after a failed one.

After `window-closed` the window id names a closed window: a `close-request` for it is answered `allow: true`, and a
push for it is ignored.

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
- A Window with a close drain (`RunicWindowOptions.CloseDrain`, experimental) answers `allow: true` once its guard
  agreed, then stays open while its accepted work drains: its page receives `closing` with `draining: true` and stays
  connected. It pushes `window-closed` after the drain, which may take as long as the work does; the host keeps the
  hosted window, or the region, until then, and may show it as closing. `"force": true` skips the decision, not the
  drain. A `close-request` during the drain is answered `allow: true` and changes nothing. No message is added for it.

### `shutdown`

The host ends the host session with `{"id":12,"kind":"shutdown","reason":"stop"}`. `reason` is informational:
`stop` (the host application stopped the guest), `host-exit` (the host is exiting) or another token. The guest
answers `{"id":12,"ok":true}` at once and then shuts down as when its desktop asks: it cancels
`DesktopHost.ShutdownRequested`, its Windows close, each with its `window-closed` push, and `Run` returns when its body
does. The host waits for the process to exit up to its stop timeout (10 seconds by default) and then ends its process
tree. A host that wants each Window's guard to decide first sends `close-request` for each window before `shutdown`.
A guest whose Windows have a close drain cancels `DesktopHost.ShutdownStarting` first and lets them drain, within its
own `DesktopHostOptions.ShutdownDrainTimeout` (30 seconds by default), before `ShutdownRequested`; each pushes
`window-closed` after its drain. The host's stop timeout still ends the process tree, so a host that hosts such a
guest sets its stop timeout above the guest's drain.

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
| `maximize` | — | `maximize` |
| `restore` | — | `maximize` |
| `set-placement` | `x`, `y`: integers from −32,768 to 32,767, both or neither (absent or `null`, which keeps the position); `width`, `height`: integers from 0 to 32,767, where 0 keeps the dimension; `maximized`, `fullScreen`: booleans | `placement` |
| `resize` | `width`, `height`: integers from 1 to 32,767 | — |
| `move` | `x`, `y`: integers from −32,768 to 32,767, in the coordinates of the host's desktop | — |
| `set-represented-file` | `path`: an absolute path or `null` | — |
| `set-layout-direction` | `direction`: `"default"`, `"left-to-right"` or `"right-to-left"` | — |
| `announce` | `text`: 1 to 1,024 characters; `priority`: `"polite"` or `"assertive"` | — |

`maximize` maximizes the window and leaves it maximized when it already is; `restore` returns it to its normal bounds
from maximized, minimized or full screen, also a window minimized while it was maximized. `set-placement` gives the open
window a placement as the host gives a window that opens one it saved: the bounds it restores to, in the units of the
`restored` member of the [`placement` push](#activity-and-placement), and whether it is maximized or full-screen; a
maximized, minimized or hidden window takes the bounds as the ones it restores to. The host may fit the bounds to its
monitors. A host that cannot position its windows, such as on Wayland, answers a `set-placement` with `x` and `y`
`unavailable` with `backend-unavailable` and changes nothing. A `set-placement` out of form is `rejected` with
`invalid-request`. Each is answered when the window was asked; the `placement` push reports the result.

The host's policy can refuse or clamp each request: a refused one is answered `rejected` with `refused`, a clamped one,
such as a size above the policy's maximum, applies the clamped value and answers `success`. The host presents with its
own activation rights: on Wayland the host, not the guest, holds the activation token, so the hosted window gets focus
as the host's own windows do. A window that any request of the guest that can activate one of its windows made active
is active because of the guest, not the user, which the [window services](#window-services-window-services1) take
into account. Besides `window-open`, `present` and `set-visible` that shows a window, desktops activate windows for
other requests: on Windows `toggle-maximized`, `maximize`, leaving full screen, and a `restore` or `set-placement` that
maximizes the window or brings it back from minimized show the window with activation, and a window that
`minimize`, `set-visible` hiding it or `window-closed` takes from the user activates another one, such as its owner, as
does the end of a [dialog or context menu](#dialogs-dialogs1).

- **Rates.** A host bounds how often a guest makes the requests that can activate its windows (`present`,
  `minimize`, `set-visible`, `set-full-screen`, `toggle-maximized`, `maximize`, `restore` and `set-placement`) and how
  often it announces; a request over the
  rate is `rejected` with `capacity`, which Runic's guest reports as `ResourceBusy` ([limits](#limits)).
- **Announcements.** An announcement speaks through the host's window, so the user's screen reader attributes it to
  the host application. An `assertive` one interrupts the screen reader, so Runic's host makes it `polite` unless the
  user works in the window: it is active, and not because of the guest (see the clipboard rule in
  [window services](#window-services-window-services1)). The host's policy decides the announcement as it will be
  made.

### Activity and placement

The host pushes what the guest's presentation reports:

| Push | Members | When |
| --- | --- | --- |
| `activity` | `window`; `active`, `minimized`, `visible`: booleans | The window's activity changed. Requires the `activity` window feature. |
| `placement` | `window`; `bounds`: `{x, y, width, height}`; `maximized`, `fullScreen`: booleans; `monitor`: a string or `null`; `restored`: `{x, y, width, height}`, with `x` and `y` `null` where the host has no position | The window moved, was resized, maximized or entered or left full screen. `bounds` is the current frame; `restored` the bounds the window restores to, which `set-placement` takes and the guest's `Placement` reports. A guest of a host that sends no `restored` reads `bounds`. |
| `color-scheme` | `window`; `colorScheme`: `"light"`, `"dark"` or `"no-preference"` | The window's colour scheme changed, also through another window or the host's application. |

A push for a window the guest already closed is ignored. The pushes of one window arrive in the order the host sent
them.

## Appearance and locale

The guest follows the host's appearance and locale (D055.8): the host's `hello` carries the initial values, and the host
pushes each change.

| Push | Members |
| --- | --- |
| `appearance` | The members of the Views protocol's [`appearance` push](../application/README.md#appearance): `colorScheme`, `contrast`, `reducedMotion`, `textScale`, `accentColor`, `systemColors`. Every push carries every member; `null` means unknown. |
| `locale` | `locale`: the host application's locale, a BCP 47 tag or `null` for none; `languages`: the languages the host's user prefers, most preferred first, an array of at most 32 tags. |

- **Appearance.** The appearance is the one the host's own windows show: Runic's host pushes what its pages receive
  (`appearance/1`), from its desktop's settings. The guest's Windows push the host's appearance to their pages, never
  the guest process's own desktop settings, since the hosted windows show on the host's desktop: a guest is not
  serve-only for the locality rule. Before the host reported one (`null` in `hello`), the pages' appearance is unknown.
  A member a receiver cannot read, such as a colour that is not `#rrggbb`, is unknown; an `appearance` that is not an
  object is unknown as a whole. The window's colour scheme override stays the [`color-scheme`](#activity-and-placement)
  push's, as the window's web view applies it.
- **Locale.** The guest's application locale (`IRunicApplicationLocale`) follows `locale`, unless the guest application
  set a locale of its own; an application that sets none again (`null`) follows the host's again. A `locale` of `null`
  changes the guest's locale only when it followed a host locale before. A tag that is not one, and a language that is
  not one, are ignored. Runic's guest applies `hello`'s locale before its application's body runs, so its first Window
  opens in it, and offers the values as `RunicGuestSession.Locale`, `Languages` and `Appearance`.
- **Direction.** Each `window-open` names its Window's `locale`, and the host gives the window that locale's direction.
  When a Window's locale changes, such as when it follows the host's, the guest sends
  [`set-layout-direction`](#presentation) with the new locale's direction, or `"default"` for no locale; a guest
  whose own window options fix a direction sends that one once the window opened.

A guest that does not implement them ignores both pushes and keeps its own appearance and locale.

## Menus (`menus/1`)

With `menus/1`, a hosted window shows the guest's menu bar as the host's native menu bar, and the guest's context
menus as native menus of the hosted window. The guest's application keeps its commands: the host runs none of them,
it tells the guest which item the user chose.

| Message | Direction | Members | Reply |
| --- | --- | --- | --- |
| `set-menu-bar` | guest → host request | `window`; `menu`: an array of submenu [entries](#menu-entries), the bar's menus in the guest's order, or `null` for none | A [platform outcome](#platform-outcomes) |
| `update-menu-items` | guest → host request | `window`; `items`: an array of 1 to 1,000 `{id, title?, enabled?, checked?}`; a member that is absent or `null` keeps the item's value | A platform outcome |
| `show-context-menu` | guest → host request | `window`; `menu`: an array of 1 or more entries; `point`: `{x, y}` in CSS pixels of the page's viewport, or `null` for the pointer's position | A platform outcome; with `success`, `itemId`: the chosen item's id, or `null` when the menu was dismissed |
| `menu-invoke` | host → guest push | `window`; `itemId` | — |

### Menu entries

An entry is one of:

- `{"kind":"item","id":"i3","title":"Save","shortcut":"Primary+S","enabled":true,"checked":null}`. `id` is an
  [identifier](#windows), unique in the menu, and in a menu bar unique for the window's lifetime
  ([item ids](#item-ids)); `title` is 1 to 128 characters, not blank, without NUL, literal text in
  the guest's language; `shortcut` is a [shortcut text](fixtures/shortcuts.json) such as `Primary+Shift+S`, or `null`
  for none; `enabled` is `true` (the default) or `false`; `checked` is `null` (the default) for a plain item, or
  `true` or `false` for a check item. Optional: `radioGroup`, the radio group's name (1 to 64 ASCII letters, digits,
  dots, underscores or hyphens), which needs `checked`; `accessKey`, one letter or digit.
- `{"kind":"separator"}`.
- `{"kind":"submenu","title":"File","role":null,"enabled":true,"items":[…]}`. `role` places a menu of the bar:
  `"application"`, `"file"`, `"help"` or `null`; an open set, which a receiver reads as `null` when it does not know a
  value, and ignores in a context menu and below the bar's top level. A bar has at most one menu of each role.
  Optional: `accessKey`.

The bar's top level holds submenus only. A bar nests at most 8 levels of entry arrays and has at most 1,000 entries in
all; a context menu nests at most 5 levels (four submenus) and has at most 256 entries. A larger menu is `rejected`
with `capacity`; another entry that breaks these rules is `rejected` with `invalid-request`. The guest's command ids
and command parameters do not cross the channel: the guest chooses the item ids and keeps the item of each.

### Item ids

The host shows a new bar some time after the guest sent it, and until then the user still chooses items of the bar
the window shows, or presses their shortcuts. An item id of a window's menu bar therefore names one item for as long
as the channel is open:

- A guest MUST NOT give an id that an item of one of the window's bars had to another item of the same window while
  the channel is open. An item of the new bar that was in the bar before MAY keep its id; any other item gets an id
  no item of the window had. Runic's guest numbers the window's items `i1`, `i2` and on across its bars, and keeps the
  id of an item, the same object, that stays.
- The host pushes `menu-invoke` only for an id of the bar it applied last, and drops a choice or a shortcut that
  carries another id, such as one of the bar the window showed while the guest's new bar arrived.
- The guest ignores a `menu-invoke` whose `itemId` names no item of the bar it sent last.

So a choice made during a change runs the item the user chose, when the new bar still has it, or nothing; never
another item. A context menu's ids need to be unique in that menu only: its reply names the chosen item.

### The guest's menu bar

- **What the guest sends.** Runic's guest sends its application's menu bar (`DesktopHost.MenuBar`) for each of its
  hosted windows, with each item's state from the Window's command path, as a window of its own would show it: an item
  is `enabled` when it is declared enabled and the Window's root ViewModel (`IRunicMenuCommandSource`) or the
  application's menu commands (`DesktopHost.MenuCommands`) can run it now, and `checked` is the first answer of that
  path (`IRunicMenuCheckedSource`) or the declaration.
- **When.** After a successful `window-open` reply, the guest sends `set-menu-bar` when its bar has a menu, and again
  each time the application declares another bar, such as one with titles in another language; a bar that became empty
  is sent as `null`. When an item's state changes, the guest sends `update-menu-items` with the items that changed. A
  guest sends one window's menu requests one at a time, each after the reply to the one before, so the host applies
  them in order; it MAY coalesce changes that happen meanwhile. A request the host's rate turned away
  (`rejected` with `capacity`, [limits](#limits)) changed nothing: Runic's guest sends the bar, or the states, again a
  little later, with what changed meanwhile.
- **The host's bar.** The host shows the window's latest bar with its updates. An `update-menu-items` id that names no
  item of the current bar is ignored, since the bar may have changed meanwhile; `checked` applies to check and radio
  items only. A host MAY coalesce the bars and updates that arrive while it rebuilds the window's native bar, and
  answer each request once a rebuild that shows it is done: Runic's host rebuilds a window's bar at most every 50
  milliseconds, however many requests arrive. The host's policy decides each bar it would show (`IRunicHostedWindowPolicy.Menu`): it may show it, show
  a changed bar, such as one without an item or with an item of the host's own, or refuse it, which is answered
  `rejected` with `refused`, and the window then shows no menus of the guest. A hosted window never shows the host
  application's own menu bar. A request for a window the host closed is answered `unavailable` with `owner-closed`.
- **Placement.** On Windows and Linux each hosted window shows the guest's bar as its own, in the window or in the
  desktop's global menu. On macOS the host's process owns the one menu bar: while a hosted window is key, the bar shows
  the host's application menu, whose About, Settings and Quit stay the host's, with the items of the host's
  Application-role menu, then the guest's menus: the guest's File menu takes the File menu's place, its Help menu is
  last, and its other menus come before the Window menu, as the host's own would. A guest menu with the `application`
  role is shown as an ordinary menu under its own title. The standard Edit, View and Window menus stay, in the host's
  language; the host's other declared menus give way. When a window of the host becomes key, or the hosted window
  closes, the host's own menus return.
- **Shortcuts.** Shortcuts belong to the host's native menu: the host matches them as for its own menus, after the
  guest's page saw the key and left it alone, and leaves out the shortcuts the platform reserves. A shortcut that both
  the host's application menu and the guest's menus use on macOS is the host's.
- **Choosing an item.** When the user chooses an enabled item of the guest's, or presses its shortcut, the host pushes
  `menu-invoke` with its id. The guest runs the item through the Window's command path, as its own menu bar would: the
  Window's root ViewModel first, then the application's commands, each asked whether it can run the item now. A
  `menu-invoke` whose `itemId` names no item of the window's current bar, such as after the bar changed, is ignored
  ([item ids](#item-ids)). An item a host policy added runs the host application's command (`DesktopHost.MenuCommands`)
  and is not pushed.
- **Order.** The guest runs one window's `menu-invoke` pushes one at a time, in the order it received them, as a native
  bar runs the clicks and shortcuts of its window: each item runs once the command of the push before it returned from
  its `Execute`, such as at the first `await` of an asynchronous command. Each item asks whether it can run when its
  turn comes, not when the push arrived: an item whose command cannot run then is ignored, not kept for later. A command
  that cannot run while it runs, such as an asynchronous command that allows one run at a time, thus ignores a second
  choice made while the first still runs, as the native bar would, where the item shows disabled meanwhile. The guest
  reads pushes off its receive loop, so a command that runs long delays the window's next items, never its other
  messages.

### Context menus

The guest's `IWindowPlatformServices.ShowContextMenuAsync` of a hosted Window sends `show-context-menu`; the host shows
the entries as a native context menu of the hosted window, at `point` or at the pointer, and answers the chosen item's
id. The guest returns its own item of that id, which its caller runs. Items are shown with the `enabled` and `checked`
they carry, shortcuts as hints only. The host's policy may show a changed menu or refuse it (`rejected` with
`refused`). An item a policy added runs the host application's command, and the guest receives `itemId: null`. A new
context menu of the window dismisses the open one, which is answered `itemId: null`. When the window closes while its
menu is open, the request is answered `unavailable` with `owner-closed`.

- **The active window only.** A context menu takes the pointer and keyboard while it is open, so the host shows one
  only for the window the user works in: a `show-context-menu` for a window that is not active (does not have the
  keyboard focus) is `rejected` with `refused` and shows nothing. A page asks for its context menu from its
  `contextmenu` event, which the user's click in the active window raises. Runic's host does not also require recent
  input in the window, which its web views do not report on every platform. On macOS a right-click in a window that is
  not key does not make it key, so such a menu is refused.
- **Inside the window.** The host places the menu inside the window's page: a `point` outside the viewport opens the
  menu at the viewport's nearest edge.

### Giving up a request

`show-context-menu` and the requests of [`dialogs/1`](#dialogs-dialogs1) wait for the user and have no
[request timeout](#messages). The guest MAY give one up, such as when its application cancelled the call, with a
`cancel` push: `{"kind":"cancel","request":<the request's id>}`. The host closes the menu or dialog and answers the
request `{"ok":false,"error":{"kind":"cancelled",…}}`, unless it already answered, after the menu or dialog closed. The guest
ignores the reply's content, but Runic's guest waits for it, within the request timeout, before the window's next dialog,
unless the window is closing: the host answers a closed window's requests after `window-closed`. A
`cancel` that names no waiting request of these kinds is ignored. Either feature in use allows the push.

## Dialogs (`dialogs/1`)

With `dialogs/1`, the guest's `IFileDialogs` and `IDesktopMessageBoxes` of a hosted Window ask the host, which shows
each dialog owned by the hosted window, with its own activation rights. Runic's host shows its platform's own dialogs,
the same that its windows show for itself; a guest that is not Runic's chooses how it asks.

| Request | Members | Reply |
| --- | --- | --- |
| `file-dialog` | `window`; `dialog`: `"open"`, `"open-multiple"`, `"save"`, `"folder"` or `"folder-multiple"`; optional: `title`, at most 1,024 characters without NUL; `initialDirectory`, an absolute path; `suggestedName`, a file name of 1 to 255 characters, not blank, without `/`, `\` or NUL and not `.` or `..`, required for `"save"` and absent otherwise; `filters`, for the open and save dialogs, at most 64 of `{name, extensions, mimeTypes?}` (as `FileDialogFilter`: extensions without the dot, or `"*"`); `showFilterChoice`, a boolean | A [platform outcome](#platform-outcomes); with `success`, `paths`: absolute paths, one for `"open"`, `"save"` and `"folder"`, one or more otherwise; `filter`: the index in `filters` of the filter the user chose, or `null`. A dismissed dialog is `failed` with `user-dismissed`. |
| `message-box` | `window`; `message`: 1 to 4,096 characters, not blank; optional: `title`, at most 1,024 characters; `detail`, at most 4,096 characters; `icon`: `"none"`, `"information"`, `"warning"` or `"error"` (an open set; an unknown one shows none); `buttons`: at most 6 `{id, label, role?, accessKey?}` with unique ids, `role` `"normal"` (the default), `"default"`, `"cancel"` or `"destructive"`, at most one default and one cancel button; none shows one OK button | A platform outcome; with `success`, `button`: the chosen button's `id`, or `null` when the box closed without one of its buttons |

- **Texts.** Titles, messages, details and labels are literal text without NUL. Runic's guest shortens a longer title,
  message or detail to the limit, so its dialogs never fail for their length.
- **One dialog per window, and few per guest.** A window shows one dialog at a time, and a guest at most as many at
  once, across its windows, as the host allows (one by default, [limits](#limits)), and at most as many over time as
  the host's dialog rate allows, so a guest that shows a dialog and cancels it again and again cannot bring dialogs to
  the front one after another. A dialog request for a window that shows one, or beyond the guest's dialogs or their
  rate, is `rejected` with `busy`; Runic's guest reports either as
  `PlatformFailureCode.ResourceBusy`, the first without asking the host.
- **The guest's own access.** The guest opens, reads and writes the paths itself, through the same leases as its own
  dialogs give it (`IFileDialogs`), and acts only on the paths a dialog returned. The host keeps no access to them. A
  host can return any path: a guest cannot tell a dialog the user answered from a forged reply
  ([security](#security-requirements)). Access the operating system grants through its picker is not handed over:
  a sandboxed guest (an App Sandbox on macOS, a Flatpak or Snap on Linux) may receive paths it cannot open, and a
  sandboxed host returns paths only it was granted. The guest then reports the error of its open.
- **Policy.** The host's policy decides each dialog (`IRunicHostedWindowPolicy.DialogAsync`): show it, show it changed
  within its kind, such as with another initial directory, or refuse it, which is answered `rejected` with `refused`
  and which Runic's guest reports as `PermissionDenied`.
- **When the window closes** while its dialog is open, also because the guest closed its Window, the host closes the
  dialog and answers the request `unavailable` with `owner-closed`. The guest's closed Window reports
  `OwnerClosed` for its dialogs in any case.
- **When the guest gives the dialog up** (`cancel`), the host closes it and answers `cancelled`. A guest whose Window
  closes while its dialog is open MAY send `cancel` before `window-closed`. When the guest goes away, the host closes
  its dialogs.
- A platform that cannot show a dialog, such as a host window without a native owner, answers `unavailable`.

## Window services (`window-services/1`)

With `window-services/1`, the guest's clipboard, monitors and file launcher of a hosted Window ask the host, which serves
them for its own window, with its own rights, under its policy (`IRunicHostedWindowPolicy.ServiceAsync`) and its
[limits](#limits); and the host forwards the files the user drops onto the window. Without the feature they report
`OwnerUnavailable`, as in serve-only mode.

| Message | Direction | Members | Reply |
| --- | --- | --- | --- |
| `clipboard-read` | guest → host request | `window`; `format`: `"formats"`, `"text"`, `"html"`, `"rtf"`, `"image"` or `"files"`; `maximum`, for every format but `"formats"`: an integer from 0 to 16,777,216, the most characters of text, HTML or RTF, bytes of the image or entries of the file list | A [platform outcome](#platform-outcomes); with `success`, `value`: for `"formats"` an array of the format tokens the clipboard has; for text, HTML and RTF a string, for an image the PNG file in base64 (RFC 4648, with padding), for files an array of absolute paths, each `null` (an empty array for files) when the clipboard has no such format. More than `maximum` is `failed` with `too-large`. |
| `clipboard-write` | guest → host request | `window`; `content`: an object with at least one of `text`, `html`, `rtf` (strings), `image` (a PNG file in base64) and `files` (1 to 4,096 absolute paths), offered together | A platform outcome; content the platform cannot write, such as text with NUL or an image that is not a PNG, is `failed` with `invalid-data`, a file that does not exist `not-found` |
| `monitors` | guest → host request | `window` | A platform outcome; with `success`, `monitors`: at most 64 `{id, name, bounds, workArea, scale, primary}`, the primary monitor first where there is one, `bounds` and `workArea` as `{x, y, width, height}` integers in the coordinates of the host's desktop; `current`: the `id` of the monitor that shows the window, or `null` |
| `launch` | guest → host request | `window`; either `uri`: an absolute URI of at most 8,192 characters, or `path`: an absolute path of at most 4,096 characters, with `operation`: `"open"` (the default), `"reveal"` or `"choose-application"` | A platform outcome |
| `monitors-changed` | host → guest push | `window` | — |
| `files-dropped` | host → guest push | `window`; `paths`: 1 to 1,024 absolute paths; optional `effect`: `"copy"` (the default), `"move"` or `"link"`; optional `target`: the page's routing label, at most 256 characters | — |

A reply larger than a [message](#messages) allows is `failed` with `too-large`, and so is a request the guest cannot fit
into one, which Runic's guest reports without sending it. A clipboard of the host that has only text, such as a custom
one, reads every other format as `unavailable` with `backend-unavailable`.

**The clipboard.** The host's clipboard is the user's, which holds what they copy in every application, such as a
password from a password manager. Runic's host therefore reads and writes it for a guest only while the user works in
the window the request names:

- The window is active (it has the keyboard focus, as for [context menus](#context-menus)), and
- not because of a guest: it did not become active within two seconds after any guest's request that can
  activate one of its windows, or the user worked in it since. Those requests are `window-open`, `present`, `minimize`,
  `set-visible`, `set-full-screen`, `toggle-maximized`, `maximize`, `restore`, `set-placement` and `window-closed`,
  for any guest's windows, and the end
  of any guest's dialogs and context menus, whether the guest cancelled it or the user answered it: the desktop then
  activates the owner. An activation while one of them shows counts as well, since the desktop activates the owner as
  the user answers, before the host learns of the answer; so does one while the host's policy decides a guest's
  request without answering at once, such as with a prompt of its own, and within two seconds after. An activation of
  any guest's window in that time counts as a guest's, so one request cannot hand a guest a window through another
  window's activation, and one guest, or a second instance of the same one, cannot hand another a window by
  minimizing, hiding or closing its own. Runic's host keeps this time host-wide, across its guests.
- not because of a deferred request of the guest: when the guest asked to activate or show the window (`window-open`,
  `present`, `set-visible` with `visible: true`, `set-full-screen`, `toggle-maximized`, `maximize`, `restore`,
  `set-placement`) and the desktop did not activate
  it right away, the window's next activation is the guest's, however late it comes. macOS activates such a window
  only when the user next switches to the host application, and Windows' and Linux desktops' focus-stealing prevention
  can defer it the same way. The cost is one false refusal when the user later switches to that window on purpose;
  switching away and back ends it.
- A guest's windows start guest-activated, its first one included: a link the user clicks in a guest's fresh window
  is refused until the user switches to it. A host that started the guest at the user's request may count the first
  activation of the guest's first window as the user's (in .NET, `RunicGuestStartInfo.UserStarted`); since the guest
  chooses when it opens that window, a host should not do this by default. The host sees the user's work in the window when
  the user activates it later, chooses an item of its native menu bar, chooses an item of a context menu that the host
  showed while the user worked in the window, or drops files onto it. It does not see the user's input in the guest's
  page, and a shortcut of the guest's menus, a `menu-invoke` that a key the page left unhandled runs, does not count:
  the user may be typing into a window the guest activated, and on Windows the page reports its keys itself. An item of
  a context menu the guest showed in a window it activated is the user's answer to the guest, as a dialog's is, and does
  not count either. Without this rule, a guest that presents its window would take the focus from the window where the
  user just copied a secret, and read it.
- A write is also allowed within one second after such a window stopped being active, for a copy that closes its
  window.

A read or write outside this rule is `rejected` with `refused`. A guest the user is not working in cannot read what the
user copies elsewhere or replace it (such as with a command line or an account number the user is about to paste), and a
guest cannot poll the clipboard in the background. A guest that the user works in can read the clipboard as any
application with the focus can. The page's own copy and paste, such as Ctrl+C and Ctrl+V, use the host's web view and
are not window services. The host's policy may require more, such as no reads at all, writes of plain text only, or
allow more, such as writes from any window of a trusted guest (`RunicHostedServiceRequest.IsGuestActivated` tells it
whether the guest activated the window). A native application does not always have more: on Wayland only an application
with the focus and a recent input event of the user can set the clipboard, and the host would otherwise set it, with
its own focus, for a guest in the background. The clipboard's watch is not proxied: a guest learns of the content only
when it reads it, never when the user copies, and Runic's guest reports its watch `unavailable` with
`backend-unavailable`. Wayland's rule that only a focused application reads the clipboard applies to the host, whose
window has the focus when the guest's has. A host never reads more of the clipboard than a reply can carry: content
over a [message](#messages)'s size is `failed` with `too-large` however large the guest's `maximum`.

**Monitors.** The host's monitors, read-only: the guest cannot move a window to a monitor, which stays the host's
placement. After a `monitors` request, the host pushes `monitors-changed` for the window when its monitors change, and
the guest lists them again; Runic's guest's monitor watch does that.

**Opening files and links.** The host opens them with its own launcher (`IDesktopFileLauncher`), its own activation
rights and its rules: link schemes (`http`, `https`, `mailto`, `ms-settings`), no `mailto` attachments, no programs or
scripts on Windows and macOS. A request outside them is `rejected` with `invalid-request`. Opening a file runs the
application the desktop chose for its type, with the user's rights, so Runic's host allows by default only what the
user chose:

- links with `http`, `https` and `mailto`, as the host's own pages open links in the default browser, and
- files the user gave the guest through this host: the paths of its open and folder dialogs
  ([`dialogs/1`](#dialogs-dialogs1)) and of its drops, compared after `.` and `..` segments are removed, while each is
  still the file it was when the user gave it: the same file identity (device and inode on Unix, volume and file index
  on Windows) behind the same path with symbolic links resolved. A file the guest created itself, at a save dialog's
  destination or anywhere else, or found in a folder the user chose, is not among them, and neither is a given file the
  guest replaced, such as with a link to another file; a file saved by replacing it, as many editors do, is no longer
  given either. Runic's host checks this again as it launches the file, through the file it opened for the launch: on
  Linux it hands the portal's `OpenURI.OpenFile` that descriptor, and on Windows it holds the file open, sharing neither
  delete nor, unless another process writes it, write access, until `ShellExecute` returns. macOS opens files by path
  only, so there the host checks the path right before the launch. The host checks, opens and launches one full path,
  so another spelling of a given file, such as with `//` or `/./`, launches the file it checked. On Linux, where the
  C library has no `fstat` (glibc before 2.33), the descriptor's identity is read through its `/proc/self/fd` entry;
  where no identity can be read, the launch is refused. The check cannot see a change of the file's content
  that keeps its identity, such as a rewrite in place or a new mode, and the application the desktop chose opens the
  path itself, after the launch ([T16](#threats));
- each only while the user works in the window the request names, as for the clipboard, so a guest the user is not
  working in opens nothing, and neither does a guest that made its window active itself.

Everything else, `ms-settings` links included, is `rejected` with `refused` unless the host's policy allows it, and the
host's policy can change a request, such as revealing a file instead of opening it. A guest opens at most as many files
and links as the host's launch rate allows ([limits](#limits)).

**Files dropped onto a window.** When a Window takes dropped files (`window-open`'s `fileDrops`), the host forwards each
drop onto the window with `files-dropped`, after its policy decided, which may forward only some of the paths or none.
The paths come from the host's platform, never from a page; `target` and `effect` come from the page in the host's web
view and are as untrusted as in a native drop. The dropped paths become paths the user gave the guest. Dropped files
without a path, such as macOS file promises, are not forwarded: the host releases them.

**Not proxied.** Dragging files out of a hosted window is not part of `window-services/1`: Runic's guest reports
`IWindowPlatformServices.StartDragAsync` `OwnerUnavailable`, as in serve-only mode, and so do its taskbar badge and
progress (`SetBadgeAsync`, `SetProgressAsync`). A later feature may add them. Application services stay guest-local
under the guest's own identity: settings, the secret store, notifications, the status icon, global shortcuts and power
events; recent documents are unavailable, as in serve-only mode (D055.8). A guest's announcements go to the host's
window with the base protocol's [`announce`](#presentation).

## Regions (`region/1`)

With `region/1`, a host can show a guest Window in a **region** of one of its own pages instead of in a native window
of its own. The host page holds an element, Runic's `<runic-hosted-region name="…">`, and the guest's page loads into
it in an `<iframe>`, from the guest's own loopback origin (**frame mode**). The engine lays the frame out, so the region
follows the host page's size, scrolling, zoom and DPI; the user moves the keyboard focus into the region and out of it
with Tab; and a screen reader reads the region as part of the host's window. The host page and the guest's page cannot
read or script each other: they have different origins, and the guest admits its frame with a credential that the
host page never sees ([frame admission](#frame-admission)). How a host page declares a region and reports it to its own
backend is the Views protocol's [`hosted-regions/1`](../application/README.md#hosted-regions).

- **In use.** A host shows a Window in a region only while `region/1` is in use. Without it, the host's policy cannot
  embed the Window (in .NET, `RunicHostedWindowRequest.CanEmbed` is `false`), and opens the window or refuses it as
  without the feature.
- **The host decides.** The host's policy decides at `window-open` whether a Window shows in a window or in a region
  (in .NET, `RunicHostedWindowDecision.Embed`). The guest does not ask for either.
- **Terms.** A **region window** is a guest Window whose `window-open` reply named `presentation: "region"`. Its **host
  window** is the host's native window whose page shows the region, or for a [browser host](#browser-hosts) the page
  in the user's browser. The region window is **attached** from its `region-attach` reply until its `region-detach`.
- **Open embeddings.** `region-attach` names how the region shows the page (`embedding`). This version defines
  `"frame"`, and [component mode](#component-mode-region-component1) `"component"`. Only frame admission is
  specific to frames.

### Presentation of a window

The `window-open` reply of a region window names its presentation:

```json
{"id":4,"ok":true,"presentation":"region","features":["present","document-edited","activity","owned-windows"],"activity":{"active":false,"minimized":false,"visible":false},"region":null}
```

| Member | Contract |
| --- | --- |
| `presentation` | `"window"`, the default when absent, or `"region"`. An open set: a guest that reads a value it does not know closes the Window, as an open it gave up ([window-open](#window-open)). |
| `region` | With `"region"`: `{width, height, scale}`, as in [`region-bounds`](#region-bounds), when a region of the host page shows the window already, or `null` while the window waits for one. Informational: the guest's page loads only after a `region-attach`. |

- **Features.** A region window's `features` never list `minimize`, `visibility`, `full-screen`, `color-scheme` or
  `per-window-color-scheme`, and a [browser host](#browser-hosts)'s list neither `present` nor `owned-windows`.
- **No window launch.** When the reply names `"region"`, the guest revokes the window's launch code (`url`) at once. A
  region window's page loads only through [frame admission](#frame-admission).
- **Open without a page.** A region window is open when the reply arrives, whether or not a region shows it. Its open
  does not wait for the page's connection, also when the guest's Desktop options wait for it in window mode, because
  the page loads only when the host attaches a region.
- **Pushes.** The host sends no `placement` and no `color-scheme` push for a region window. `activity` means:

  | Member | In a region |
  | --- | --- |
  | `visible` | The window is attached, and its region intersects the host page's viewport and is not hidden. |
  | `active` | The host window is active, and the keyboard focus is in the region. |
  | `minimized` | The host window is minimized. |

### Region messages

| Message | Direction | Members | Reply |
| --- | --- | --- | --- |
| `region-attach` | host → guest request | `window`; `embedding`; `embedder`; `width`, `height`, `scale`; optional `sizing`, `allowSameSite` | `frame: {url, sameSite}` |
| `region-detach` | host → guest push | `window`; `reason` | — |
| `region-bounds` | host → guest push | `window`; `width`, `height`, `scale` | — |
| `region-focus` | host → guest push | `window`; `focus`: `"enter"`; `direction` | — |
| `region-focus` | guest → host request | `window`; `focus`: `"leave"`; `direction` | A [platform outcome](#platform-outcomes) |
| `region-page` | guest → host push | `window`; `page`: `"connected"`, `"disconnected"` or `"revoked"` | — |
| `set-title` | guest → host request | `window`; `title` | A platform outcome |

A region message for a window that is not a region window of the channel is `rejected` with `invalid-request` (a
request) or ignored (a push).

#### `region-attach`

The host attaches a region window when its region is shown, and the guest answers with the address the region loads:

```json
{"id":5,"kind":"region-attach","window":"w1","embedding":"frame","embedder":"http://127.0.0.1:38200","width":800,"height":520,"scale":1.5,"sizing":"fill","allowSameSite":true}
{"id":5,"ok":true,"frame":{"url":"http://[::1]:40117/_runic_frame/Xk2bT9…","sameSite":false}}
```

| Member | Contract |
| --- | --- |
| `embedding` | `"frame"`, or `"component"` with `region-component/1` in use ([component attach](#component-attach)). An open set: a guest answers an embedding it does not implement, or one whose feature is not in use, `rejected` with `embedding-unsupported`, and the host's policy then falls back or refuses. |
| `embedder` | The origin of the host page that frames the guest's page: `http://`, the host `127.0.0.1`, `[::1]` or `localhost`, an explicit port from 1 to 65,535 without leading zeros, and nothing after it (no path, query, fragment or user information), at most 256 characters: the form in which browsers serialize such an origin. It is the origin the host page has in its web view or browser, such as the host's own page, or in development its development server; Runic's host takes the `Origin` with which the page that declared the region connected to it. |
| `width`, `height` | The region's size in CSS pixels of the host page, integers from 0 to 32,767. |
| `scale` | The host page's device pixel ratio, a number from 0.25 to 8. |
| `sizing` | Optional. `"fill"` (the default): the host's layout sets the region's size, and the guest's page fills it and scrolls inside it. `"content-height"`: the region takes the height that the guest asks for with [`resize`](#presentation-in-a-region), within the host's minimum and maximum, and its width follows the host. An open set; a value the guest does not know reads as `"fill"`. |
| `allowSameSite` | Optional, `false` by default. `true` lets the guest serve the frame from the embedder's own loopback family when it cannot listen on the other one ([guest origin](#guest-origin)). |
| `browser` | Optional, `false` by default. `true` when the host page runs in an installed browser rather than the host's own web view: a [browser host](#browser-hosts)'s page, or a host's browser fallback. A guest that runs elevated refuses it. A value that is not a boolean is `rejected` with `invalid-request`. |

Reply members:

| Member | Contract |
| --- | --- |
| `frame.url` | A new one-time **frame launch address**: `http://`, a loopback literal and the frame surface's port, and a path that ends in `/_runic_frame/<code>` (a surface of a shared listener has a path of its own before it), at most 2,048 characters. |
| `frame.sameSite` | `true` when the guest fell back to the embedder's loopback family, `false` otherwise. A host logs a warning when it is `true`. |

- **Before the reply** the guest revokes the frame code and token of the window's earlier attach, if any, sets the
  frame surface's `frame-ancestors` to `embedder`, and mints the new code.
- **Validation.** An `embedder`, size, `scale` or other member out of range is `rejected` with `invalid-request`. The
  guest never frames its page for another origin than the latest `region-attach` named.
- **One at a time.** A host sends at most one `region-attach` per window at a time, and waits for its reply before it
  sends another. To attach an attached window elsewhere, it pushes `region-detach` first; a guest that receives a
  `region-attach` for an attached window treats it as a detach followed by the attach.
- **Failures.** A guest that cannot listen on the cross-site family, and may not fall back, answers `failed` with
  `cross-site-unavailable`. A guest that refuses region mode for its host, such as an elevated guest whose host is not
  an [elevated peer](#elevated-peer-admission), answers `rejected` with `refused`. A guest that runs elevated answers a
  `region-attach` with `browser: true` `rejected` with `guest-elevated` ([browser hosts](#browser-hosts)). The window
  stays open and unattached.

#### `region-detach`

`{"kind":"region-detach","window":"w1","reason":"removed"}`. The region no longer shows the window.

| `reason` | Why |
| --- | --- |
| `removed` | The host page removed the region. |
| `reload` | The host page reloads, navigates or lost its connection to the host's backend. |
| `crashed` | The content process of the frame or of the host page ended. |
| `host` | The host application detached the window, such as to move it to another region. |

An open set; the guest treats every reason alike. It revokes the window's frame code and token, closes the
connections of the page they admitted, and keeps the Window open and unattached: `activity` reports `visible: false`.
A later `region-attach` mints a new code. A `region-detach` for a window that is not attached is ignored.

#### `region-bounds`

`{"kind":"region-bounds","window":"w1","width":800,"height":612,"scale":1.5}`: the region's size or scale changed, in
the ranges of `region-attach`. The host coalesces them per window, latest wins, at most one per animation frame of the
host page. It never sends the region's position, the host page's content or its other elements.

#### `region-focus`

The host pushes `{"kind":"region-focus","window":"w1","focus":"enter","direction":"forward"}` when the keyboard focus
enters the region by the host's doing:

| `direction` | When | The guest's page focuses |
| --- | --- | --- |
| `forward` | The user pressed Tab in the host page | Its first focusable element |
| `backward` | The user pressed Shift+Tab | Its last focusable element |
| `programmatic` | The host moved the focus into the region, for `present` or by its own decision | The element that had the focus last, else its first focusable element |

A page whose document already has a focused element other than its body, such as after the engine's own traversal into
the frame, keeps it. The host sends no `region-focus` when the user clicks into the frame or the guest's page moved
the focus itself, and none when the focus leaves the region: in frame mode, traversal out of the frame is the
engine's.

The guest's request `{"id":8,"kind":"region-focus","window":"w1","focus":"leave","direction":"next"}` asks the host to
move the focus to the element after (`"next"`) or before (`"previous"`) the region. It is for embeddings without native
traversal; in frame mode the host moves the focus and answers `success`, or `unavailable` with `owner-closed` when the
window is not attached.

#### `region-page`

The guest reports the state of the page that the window's frame token admitted:

| `page` | When |
| --- | --- |
| `connected` | A page admitted with the token connected. |
| `disconnected` | That page lost its connection, as in a reload or a crash of the frame's content process. The page may reconnect with its token. |
| `revoked` | The guest revoked the token after a [launch conflict](#frame-admission). The host detaches the window and MAY attach it again. |

The guest pushes it only while the window is attached, in order. The host shows the region as loading from its
`region-attach` until `connected` (in .NET, `RunicHostedRegion.State`).

#### `set-title`

`{"id":9,"kind":"set-title","window":"w1","title":"System check"}`: the title of the guest's page, a string of at most
1,024 characters without NUL. In a window the engine shows the page's `document.title`; a region window's guest sends
it instead, whenever the page's title changes, coalesced. The host passes it to its page, which uses it as the frame's
accessible name unless the host page names the region itself, and answers `success`. `set-title` for a window that is
not a region window is `rejected` with `invalid-request`. The host bounds its rate ([limits](#limits)); Runic's guest
sends the latest title again a little later after a `capacity` rejection.

### Frame admission

The guest serves a region window's page on a **frame surface**: the Window's page and assets, served for a frame, on a
listener of the loopback family that the [guest origin](#guest-origin) rule chooses. Frame admission uses no cookie.

1. **Frame launch code.** Each `region-attach` mints a code of 32 bytes from a cryptographically secure random
   generator, in base64url without padding (43 characters), for the reply's `frame.url`. It is one-time and expires
   30 seconds after the reply if no request used it.
2. **Frame token.** The first request with the code is answered `303 See Other` to the page, with a **frame token** in
   the address's fragment, such as `Location: /index.html#runic-frame=<token>`: 32 random bytes in base64url. The
   response carries `Cache-Control: no-store`. The code is then spent. A fragment is never sent to a server and never appears in
   a `Referer`.
3. **The page keeps it.** The guest's page script reads the token before any other script of the page runs, removes
   it from the address (`history.replaceState`), and keeps it in `sessionStorage` under a key that names its surface's
   path, so a reload of the frame, or of its crashed content process, reconnects. It offers the token only with its
   Bridge WebSocket upgrade, as the subprotocol `Sec-WebSocket-Protocol: runic-desktop.<token>`, never in a URL's path
   or query. A frame surface's bootstrap script carries no credential and needs no token: a `<script src>` cannot send
   a header, and the surface's session credential never leaves the guest process.
4. **One page at a time.** A token admits one connected page at a time: a WebSocket upgrade with the token, while a
   page that it admitted is connected, is refused with 403. A reload, whose page connects after the old connection
   closed, is admitted.
5. **Launch conflict.** When a spent code is presented again, the guest revokes the token: it closes the page's
   connections, refuses the token from then on, answers 403, pushes `region-page` `revoked` and reports
   `frame-launch-conflict` to its diagnostics. Neither client keeps the surface, as with a browser launch conflict.
6. **End.** The token and an unused code die at `region-detach`, at the window's next `region-attach` and when the
   Window closes.

Every frame surface response carries `Content-Security-Policy: frame-ancestors <embedder>`, with `'none'` instead of
an origin before the first attach, `Referrer-Policy: no-referrer`, and no `X-Frame-Options`: CSP 3 makes browsers
ignore `X-Frame-Options` when `frame-ancestors` is present, and leaving it out avoids depending on that. A content
handler's own `Content-Security-Policy` header is sent as a second header, as on any surface. The guest's other
surfaces, and the host's, keep `frame-ancestors 'self'`, so a guest cannot frame its host's pages. The window-mode
launch binding (`/_runic_launch/` and its cookie) does not apply on a frame surface.

The host page holds only the frame's `src`, whose code the frame's own navigation spends. It can read neither the
frame's address nor its storage, which are cross-origin, and a `fetch` of the code returns an opaque or blocked
response but spends the code, so the frame then fails with a launch conflict. A host page can therefore show the
guest's page or keep it from loading, but never act as it. A host that forwards its page's console messages redacts
the value of `#runic-frame=` in them: an engine's message for a frame that `frame-ancestors` blocked can carry the
frame's full address.

### Guest origin

A frame surface listens on the loopback family that the embedder does not use, so the frame is **cross-site** to the
host page: no cookie of the host page's origin reaches the guest's surface, and an engine with site isolation, such
as Chromium and WebView2, runs the frame in a process of its own. A same-site frame, such as `127.0.0.1:B` in a page
of `127.0.0.1:A`, would receive every cookie of the host page's origin, `HttpOnly` and `SameSite=Strict` ones
included, since cookies do not separate ports.

| `embedder`'s host | The frame surface listens on |
| --- | --- |
| `127.0.0.1` | `[::1]` |
| `[::1]` | `127.0.0.1` |
| `localhost` | `[::1]`, else `127.0.0.1`: both are cross-site to `localhost` |

When the guest cannot listen on that family, such as on a system without IPv6:

- With `allowSameSite: true`, it listens on the embedder's family and answers `sameSite: true`. The frame then receives
  the cookies of the host page's origin. Runic's embedded hosts allow it: the web view profile of an embedded host
  holds only the host's own cookies, and Runic's surfaces in embedded windows use none. The host logs a warning.
- Otherwise it answers `failed` with `cross-site-unavailable`. A browser host (serve-only) never allows the fallback,
  since the user's browser holds the cookies of other local applications on the same loopback address.

The frame's storage, `localStorage` and IndexedDB, lives in the host window's web view profile or browser,
partitioned under the host page's site, and can outlive the host session: the data store of its own that a hosted
window gets does not apply to a region. Runic's page runtime keeps only `sessionStorage`, and an application treats
storage in a region as session-scoped. Runic clears no storage per origin.

### Presentation in a region

The [presentation requests](#presentation) of a region window:

| Request | In a region | Reply |
| --- | --- | --- |
| `present` | Brings the host window forward under the usual rules, and moves the focus into the region only as the [focus](#focus-in-a-region) rule allows. A request that can activate a window, for the rate and the window services. | `success`, or `rejected` with `refused` or `capacity` |
| `minimize`, `set-visible`, `set-full-screen`, `maximize`, `restore`, `set-placement` | Not in `features`; the guest does not send them | `unavailable` with `backend-unavailable` |
| `toggle-maximized`, `move`, `set-represented-file` | Not applicable | `unavailable` with `embedded`; a guest that does not know the reason reads it as `backend-unavailable` |
| `resize` | The guest's **preferred size**. With `sizing: "content-height"` the host applies the height within its minimum and maximum, and the region's width keeps following the host page. Runic's guest sends its page's content height. Under the rate of requests that can activate a window. | `success` when the region follows the guest's height, else `unavailable` with `embedded` |
| `set-document-edited` | In `features`; the host may show it, such as a mark on a wizard's step | `success` |
| `set-title` | The page's title, for the region's accessible name | `success` |
| `set-color-scheme` | Not in `features`: the region shows the host window's scheme | `unavailable` with `backend-unavailable` |
| `set-layout-direction` | Applies to the guest's page; the host window keeps its own direction | `success` |
| `announce` | As in a window, through the host window | as in a window |

The `appearance` and `locale` pushes are as for any guest: the guest follows the host. Runic's guest page in a region
also sets `overscroll-behavior: contain` on its root, so scrolling at the frame's end does not scroll the host page, and
registers no `beforeunload` listener: a region window guards its close with `close-request`.

### Features in a region

- **Menus (`menus/1`).** A region shows no menu bar of the guest: `set-menu-bar` and `update-menu-items` are answered
  `unavailable` with `embedded`, unless the host's policy shows the guest's menus in the host window's bar while the
  region is attached, which a later version may define. Context menus are shown as native context menus of the host
  window: `point` is in CSS pixels of the frame's viewport, and the host adds the region's place in its page, which it
  never tells the guest. "The active window only" means the host window is active and the focus is in the region; a
  `point` outside the region opens the menu at the region's nearest edge.
- **Dialogs (`dialogs/1`).** A region window's dialogs are owned by its host window. The one-dialog rule applies to
  each region window; a dialog request for a window that is not attached is answered `unavailable` with
  `owner-unavailable`.
- **Window services (`window-services/1`).** The window the user works in is the host window, active, with the focus
  in the region, which the user moved there: with Tab, Shift+Tab or a click in the frame. Focus that entered the region
  by `present`, by the host's programmatic focus or by the guest page's script without the user's activation is the
  guest's, as an activation by the guest is in a window, until the focus leaves the region and the user brings it back:
  a click into a frame that already has the focus does not give it back, which costs one false refusal. The other
  rules of the [clipboard](#window-services-window-services1) apply as in a window. `monitors` lists the host window's
  monitors, and `current` is the host window's.
- **Drops.** With `fileDrops`, a drop onto the region's frame is forwarded to the guest as `files-dropped`, under the
  host's policy, and never to the host page. A drop elsewhere in the host window is the host's own. Every other frame
  of another origin than the host page's still delivers no drop.
- **Permissions.** The frame's permissions are the ones the host's policy granted the guest's Window (`permissions`,
  `media-capture` for camera and microphone), in the frame's `allow` attribute and in the host's decision on each of
  the frame's permission requests, not the host window's own grants. An engine that does not tell the host which
  frame asks, WebKitGTK, denies the frame's requests, as it denies any frame of another origin's.
- **Keys.** While the frame has the focus, keys go to the guest's page first; the host page's script does not see
  them. The host window's native menu shortcuts work as for its own pages, after the guest's page left the key
  unhandled. On WebView2, where the page reports the keys it left unhandled, Runic's host hears those reports from an
  attached region's frame as from its own page, and from no other frame of another origin.
- **Owned windows.** A window that a region window owns opens in window mode, owned by the host window that shows the
  region, or last showed it; with `modality: "window"`, it is modal to that host window.

### Focus in a region

The guest never takes the keyboard focus from the host page:

- **`present`.** The host brings the host window forward, and moves the focus into the region (`region-focus`
  `programmatic`) only when the host page holds no focus: its focused element is its body or none, or the focus is in
  the region already. Otherwise the focus stays where the user has it, also when the host window was inactive and
  becomes active.
- **On attach.** The host decides whether its page moves the focus into a newly attached region, such as for a wizard
  step that consists of the region. Such focus is programmatic, not the user's, and moves only when the host page holds
  no focus, as for `present`: a focus that stays on the host's own button that started the step stays there.
- **Who decides.** The host page knows whether it holds the focus. In
  [`hosted-regions/1`](../application/README.md#hosted-regions) the host asks its page to focus the region, the page does
  so only while it holds no focus, and reports the focus as the script's; the host pushes `region-focus` `programmatic`
  only after that report, within a second of its request.
- **The guest's script.** A guest page can call `focus()` in its frame without the user's activation, and engines may
  let that move the keyboard focus out of the host page. The host page reports how the focus entered its region
  ([`hosted-regions/1`](../application/README.md#hosted-regions)), and focus that entered without a Tab or a click
  counts as the guest's for the window services. A guest can still read keystrokes that the user meant for the host
  page after such a move, as a process of the same user can anyway ([T20](#threats-in-a-region)).

### Lifecycle in a region

- **The host page removes the region**, reloads, navigates or crashes: the host pushes `region-detach` with `removed`,
  `reload` or `crashed`. The guest's Window stays open and detached. The host attaches it again when a region of the
  same name appears in the same host window (in .NET, `RunicHostedRegionOptions.ReattachOnReload`), or closes it with
  `close-request`.
- **The host decides a departure**, such as a wizard's Back or Next, or a close of the host window: it asks the guest
  with `close-request` first, as for a user's close, and the region window closes only when the guest allows.
- **The guest closes its Window**: `window-closed` empties the region.
- **The guest goes away**: the host empties its regions, and its page shows its own content for the ended state.
- **The frame's content process ends**: the host detaches the window with `crashed` and attaches it again with a new
  code. A host page never loads a frame launch address twice: the second load would be a launch conflict.
- **Runic's hosts** learn of an ended content process from the engine. WebView2 names the frames of a frame render
  process that ended, which then show an error page: their regions detach with `crashed` and attach again. The end of
  the host page's own process, WebView2's render process or WebKit's web content process, ends the frames it rendered
  too, since WebKit runs a page's frames in the page's process: their regions detach with `crashed`, and the page's
  reload declares them again. When the host page's connection to the host's backend closes before the engine reports
  the end, the detach reads `reload`.

### Browser hosts

A **browser host** is a host whose pages run in the user's installed browser, such as a Runic application that runs
serve-only (`RUNIC_APPLICATION_SERVE_ONLY=1`). It has no native window of its own, so it shows a guest's Windows only
in regions of its pages, with fewer features than a host with native windows:

- **Features.** Its `hello` lists `region/1` and `region-component/1` alone. Without `menus/1`, `dialogs/1` and `window-services/1`, a guest's
  menus, dialogs and window services report `OwnerUnavailable`, as in serve-only mode.
- **No windows of their own.** It answers a `window-open` that its policy does not show in a region `rejected` with
  `refused`, so the guest's open fails with `hosted-window-refused`. Owned windows are among them. In .NET,
  `RunicHostedWindowRequest.CanOpenWindow` is `false` and only `RunicHostedWindowDecision.Embed` and `EmbedComponent`
  show a window.
- **Presentation.** A region window's `features` list neither `present` nor `owned-windows`. `present` and `announce`
  are answered `unavailable` with `backend-unavailable`: the host can neither bring the user's browser forward nor speak
  through it. `activity` reports the region `active` while it holds the focus of its page, since a browser reports
  nothing about its window; `minimized` is always `false`. The rest of [presentation in a region](#presentation-in-a-region)
  applies, and `region-focus` `leave` is answered `success`.
- **Cross-site only.** Its `region-attach` carries `browser: true` and never `allowSameSite: true`: the user's browser
  holds the cookies of other local applications on the same loopback names, which a same-site frame would receive. A
  guest that cannot listen on the other loopback family answers `failed` with `cross-site-unavailable`, and the region
  shows its fallback content.
- **Components.** A guest serves a component for a browser host's page from `[::1]`, or from `127.0.0.1` for a page on
  `[::1]`, never from `127.0.0.1` for a page on `localhost`: the user's browser sends the `127.0.0.1` cookies of other
  local applications with the component's connection ([component origin](#component-origin)). Without `[::1]` it
  answers `failed` with `cross-site-unavailable`. The page's Content-Security-Policy can admit `[::1]` only with `http:`
  and `ws:`.
- **The embedder** is the origin with which the host page connected to the host, as its browser sent it, so a page
  opened through `localhost` names `http://localhost:<port>` and the guest serves its frame from `[::1]`, else
  `127.0.0.1` ([guest origin](#guest-origin)). Runic's surfaces admit a page's connection only from their own origin or
  one of their additional origins.
- **Elevated guests.** A guest that runs elevated refuses every `region-attach` with `browser: true` (`rejected`,
  `guest-elevated`): its page would run in a process that does not run elevated. The window stays open and unattached.
  [Elevated-peer admission](#elevated-peer-admission) refuses such a browser's connections in any case.

A host with native windows whose page runs in a browser fallback, rather than its embedded web view, attaches with
`browser: true` too, and never allows the same site.

### Component mode (`region-component/1`)

With `region-component/1`, which requires `region/1`, a region shows a guest Window as a **component**: the guest
serves an ES module that defines a custom element, and the host page loads it into its own document. The guest's Views
render in the host page, with its theme (CSS custom properties and `prefers-*` media inherit into the component) and its
layout, and without a frame boundary for focus, drops and accessibility. Several components can share one page.

- **Trust.** Host page and component share one JavaScript realm; a shadow root is no boundary. The guest's code can read
  and change the host page's document, use its Bridge as the host page does and patch its globals, and has every
  permission of the host page. The host page holds the guest's component token and can act as the guest's page. The two
  trust domains merge at the page; the backends stay separate processes, each with its own channel and policy.
  Component mode is for applications of the same vendor.
- **Both sides opt in.** The guest lists `region-component/1` in its `hello` reply only when its application allows
  component embedding for the host the offer names (in .NET, `RunicGuestOptions.AllowComponentEmbedding`, a filter like
  `AcceptHost`, not a security boundary). The host lists it when it implements it, and shows a window as a component
  only when its policy decides so (in .NET, `RunicHostedWindowDecision.EmbedComponent`, which
  `RunicHostedWindowRequest.CanEmbedComponent` allows) and its page's Content-Security-Policy admits the guest's origin
  ([the host page](#the-host-page)). Without the feature in use, the host's policy cannot decide it, and a guest answers
  `embedding: "component"` with `embedding-unsupported`; frame mode still applies.

#### Component attach

```json
{"id":5,"kind":"region-attach","window":"w1","embedding":"component","embedder":"http://127.0.0.1:38200","width":800,"height":520,"scale":1.5,"allowSameSite":true}
{"id":5,"ok":true,"component":{"module":"http://127.0.0.1:40117/w1/_runic_component/component.js","url":"ws://127.0.0.1:40117/w1/_webui_ws_connect","token":"q3VtYm…","sameSite":true}}
```

`embedder`, `width`, `height`, `scale` and `allowSameSite` are those of [`region-attach`](#region-attach); `sizing` does
not apply, since the component takes part in the host page's layout. Reply members:

| Member | Contract |
| --- | --- |
| `component.module` | The address of the component build's module: `http://`, a loopback literal and the component surface's port, and a path that ends in `/_runic_component/` and the module the build's manifest names, ending in `.js`; at most 2,048 characters, without a query or fragment |
| `component.url` | The WebSocket address the component connects to: `ws://` on the module's authority, with a path that ends in `/_webui_ws_connect`; at most 2,048 characters |
| `component.token` | A new **component token**: 32 bytes from a cryptographically secure random generator, in base64url without padding (43 characters) |
| `component.sameSite` | `true` when the component surface is of the embedder's own site ([component origin](#component-origin)), `false` otherwise |

- **Before the reply** the guest ends the window's earlier attachment, frame or component: it revokes its code or token
  and closes the connections of the page they admitted. It then mints the new token and grants the embedder its
  component build ([component build](#component-build)).
- **No tag.** The reply names no tag: the module defines its custom element when it loads and exports the name it
  defined as `tag`. The same build loaded from a second origin into one page, such as two instances of one application,
  defines a name of its own, so the page takes the name from the module.
- **Failures.** A guest whose content has no component build answers `rejected` with `embedding-unsupported`. One that
  cannot serve the component cross-site and may not use the embedder's site answers `failed` with
  `cross-site-unavailable`. An elevated guest without [elevated-peer admission](#elevated-peer-admission) answers
  `rejected` with `refused`. The window stays open and unattached.
- `region-detach`, `region-bounds`, `region-page` and the [lifecycle](#lifecycle-in-a-region) are as in frame mode.
  `region-page` reports the page that the component token admitted. A `region-focus` `enter` has no effect: the
  component is part of the host page's focus order. A component has no document title of its own, so the guest sends
  no `set-title` for it.

#### Component origin

The guest serves a component from its Window's surface on `127.0.0.1`, an origin that a host page's
Content-Security-Policy can name as `http://127.0.0.1:*` and `ws://127.0.0.1:*`: CSP host sources cannot name an IPv6
literal, and engines ignore such a source as invalid (verified in Chromium 154).

| `embedder`'s host | The component surface listens on |
| --- | --- |
| `127.0.0.1`, with `allowSameSite: true` | `127.0.0.1`: the embedder's own site, `sameSite: true` |
| `127.0.0.1`, without it | `[::1]`, else `failed` with `cross-site-unavailable` |
| `[::1]` or `localhost` | `127.0.0.1`, another site |
| any, with `browser: true` ([browser hosts](#browser-hosts)) | `[::1]`, else `failed` with `cross-site-unavailable`; for a `[::1]` embedder `127.0.0.1` |

A same-site component surface receives the cookies of the host page's origin with its WebSocket upgrade and the
host page's blob requests, since cookies do not separate ports. Unlike a frame's, this exposes nothing new: the
component's code already runs in the host page's realm, the web view profile of an embedded host holds only the host's
own cookies, and Runic's surfaces in embedded windows use none. Runic's embedded hosts allow it. A browser host
(serve-only) never does, since the user's browser holds the cookies of other local applications on the same loopback
address: its components come from `[::1]` (D059.18), which its page's Content-Security-Policy can admit only with the
scheme sources `http:` and `ws:`.

#### Component build

A guest's surface serves its **component build** below `_runic_component/`, as the `component` build target of
`@runic-artifex/vite-plugin-runic` writes it: the manifest `runic-component.json`
(`{"version": 1, "name", "module", "tag"}`), the module and its chunks and assets.

- It answers `GET` and `HEAD` only with an `Origin` that is the embedder of the window's current component attach,
  exactly as the browser serialized it, with `Access-Control-Allow-Origin` repeating that origin, `Vary: Origin` and
  `Cross-Origin-Resource-Policy: cross-origin`. Any other origin, a request without one, and every request while no
  component is attached get `403` (diagnostic `component-origin-not-allowed`). It serves no pages (`.html`, folders)
  there, sets no cookie, and the files carry no credential.
- The guest reads the manifest through its own surface, with the embedder's `Origin`, to answer the attach. `module` is
  a relative path below `_runic_component/` that ends in `.js`.

#### Component admission

The component's Bridge WebSocket comes from the host page's document, so neither a cookie nor a frame token can admit
it. Instead:

1. **Token.** Each component attach mints a component token, which travels only in the `region-attach` reply over the
   authenticated channel. The host passes it only to the page that declared the region, in its
   [`hosted-region-content`](../application/README.md#hosted-regions).
2. **Upgrade.** The guest admits a WebSocket upgrade of `component.url` only on the listener that serves the component,
   only with `Origin` equal to `embedder` (otherwise `403`, `origin-not-allowed`), and only with the subprotocol
   `runic-desktop.<token>` (otherwise `401`, `credential-required`). The token is compared in constant time. No cookie
   takes part. The token is never in a URL.
3. **One page at a time.** An upgrade with the token while a page that it admitted is connected is refused with `403`.
   A component that lost its connection connects again with the same token while the window stays attached.
4. **Packets.** The component's packets carry no window token, which only the guest's served bootstrap holds; on a
   socket that the component token admitted, the guest accepts the token check with any packet token.
5. **End.** The token dies at `region-detach`, at the window's next `region-attach` and when the Window closes. The
   component build's grant ends with it.

Each check covers a requester that the other does not. A browser cannot forge `Origin`, so no other page, such as
another local application's page in the user's browser, connects as the component, even with a leaked token. A process
that is not a browser can send any `Origin`, so it needs the token, which only the host's backend and its page hold.
Processes of the same user that read the host's memory are outside what Runic protects.

A component never runs script that its guest sends: a request to evaluate script is answered with a failure. It never
navigates the host page and ignores the page wire's navigation, click and binding traffic.

#### Blobs and uploads of a component

While a component is attached, the guest's [blob and upload](../application/README.md#blobs-and-uploads) addresses are
on the component surface, and the component, whose page is the host page, reaches them cross-origin:

- **Blobs.** A blob request that carries an `Origin` must name the embedder, and is answered with
  `Access-Control-Allow-Origin` repeating it, `Vary: Origin` and `Cross-Origin-Resource-Policy: cross-origin`; another
  origin gets `403` (`blob-not-admitted`). A no-cors request of the host page, such as an `<img>`, carries no `Origin`
  and may be cross-site, so the guest admits it, and any other no-cors request, by the blob URL alone, which is a
  capability of at least 128 random bits.
- **Uploads.** The upload's custom headers need a CORS preflight. The guest answers an `OPTIONS` of the upload address
  from the embedder with `204`, `Access-Control-Allow-Origin` repeating it, `Access-Control-Allow-Credentials: true`,
  `Access-Control-Allow-Methods: POST` and `Access-Control-Allow-Headers: Content-Type, X-Runic-Upload-Token,
  X-Runic-Upload-Name`, and any other origin with `403`. The `POST` must come from the embedder and carry the
  connection's upload token, as on any surface; its answer carries the same `Access-Control-Allow-Origin` and
  `-Credentials`.
- After the detach, the frame and window rules of `blobs/1` apply again.

#### The host page

- The host pushes the reply's `module`, `url` and `token` to the page that declared the region, as
  [`hosted-region-content`](../application/README.md#hosted-regions) with `embedding: "component"`. The page imports the
  module, creates the element whose name the module exports as `tag`, and connects it with `url` and `token`
  (`<runic-hosted-region>` does this). A component that is removed closes its connection.
- **Content-Security-Policy.** A host page with a policy must admit the component surface's origin in `script-src` and
  `connect-src`. Its port changes with each run, so a host page of an embedded host names `http://127.0.0.1:*` and
  `ws://127.0.0.1:*`; a browser host's page needs `http:` and `ws:` ([component origin](#component-origin)). A module
  that the policy blocks does not load, and the region shows its fallback content.
- **Local Network Access.** Engines that gate requests from public pages to loopback addresses, such as Chromium,
  admit a host page that is on a loopback address itself, as Runic's host pages are.
- **Permissions.** A component has the host page's permissions and its drops, keys and focus; the frame's permissions
  policy (`RunicHostedRegionOptions.Permissions`) does not apply.

#### Elevated guests

A component of an elevated guest runs in the host page's realm with the guest's token, so with the token any process
that renders the host page acts as the elevated guest's page. An elevated guest allows component mode only for an
elevated host, and its component surface admits only [elevated peers](#elevated-peer-admission). Where the guest
cannot check its peers (macOS), it does not list `region-component/1` and answers a component attach `rejected` with
`refused`. A browser host, and a host that does not run elevated, cannot show an elevated guest as a component.

## Lifecycle

### Host gone

The guest treats either of these as the host going away: its standard input reaches its end, or the host channel
closes, for any reason, after the handshake. It then shuts down as for [`shutdown`](#shutdown): it cancels
`DesktopHost.ShutdownRequested`, its Windows close and `Run` returns when its body does. A guest MUST NOT fall back to
showing a window of its own and MUST NOT send another `window-open`:

- A Window whose open is waiting for the `window-open` reply fails to open at once, with `hosted-host-gone`; the guest
  pushes `window-closed` if a successful reply still arrives ([window-open](#window-open)).
- A Window that opens after the host went away fails to open with `hosted-host-gone`, and nothing is sent.
- The host going away is the normal end of a hosted application, not a crash: when the body ends with that failure,
  or is cancelled through `ShutdownRequested`, `Run` returns 0.

When the body has not returned within 10 seconds, the guest's process exits. The host's job object or process group is
the backstop.

### Guest gone

The host treats either of these as the guest going away: the host channel closes, or the guest process exits. It
closes the guest's hosted windows (an application may show its own error page instead, D055.8), ends the guest's
process tree if it still runs, and reports the exit code (`RunicHostedApplication.Exited`).

### Page reloads and crashes

A page reload or a crash of the web content process is handled inside the hosted window, as for any Runic Desktop
window: the page reconnects to the guest's surface. Nothing crosses the host channel, and the launch binding keeps
admitting the hosted window's web view. A region's frame reconnects with its frame token
([lifecycle in a region](#lifecycle-in-a-region)).

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
- A [region window](#regions-region1) has no launch binding: its page loads in the host page's frame through
  [frame admission](#frame-admission), with a frame token instead of a cookie, from a surface that is cross-site to
  the host page. The host injects nothing into the frame either.

## Elevated-peer admission

A guest that runs elevated, as an elevated pair with its host allows, admits connections to its listeners only from a
loopback peer process that runs elevated itself. The rule covers every listener of an elevated guest: its channel
listener, its page surfaces, and its frame and component surfaces ([regions](#regions-region1)). A region's frame is
rendered by whichever process hosts the page, so without the rule a process that does not run elevated, such as an
installed browser or a process that obtained a frame launch code or token, could render and drive the elevated guest's
page.

- **The check.** The guest looks up the process that owns the peer's end of each accepted TCP connection, before it
  reads the request: on Windows the owning process of the connection (`GetExtendedTcpTable`) and its token, which
  must be elevated (`TokenElevation`) or have an integrity level of High or above, as for the elevated pair's parent;
  on Linux the owner of the socket (`/proc/net/tcp`, `/proc/net/tcp6`), which must be uid 0. A peer that fails the
  check, or that the guest cannot look up, is refused: the connection is closed without a response, and the guest
  reports `elevated-peer-refused` to its diagnostics.
- **Process ids.** On Windows the guest holds the owning process open while it reads its token, and reads the
  connection's owner again while it holds it, so the process it read is the one that owns the connection: an id is
  reused only once every handle to the old process is closed. The process must still run and must have been created
  before the connection was accepted. An IPv4 connection that a dual-mode socket lists with IPv4-mapped IPv6 addresses
  is matched as IPv4.
- **What passes.** The elevated host's own processes, including its web view's processes, which start from the host's
  token. An installed browser of the user, a browser (serve-only) host, and any process of the user that does not run
  elevated fail, whatever code or token they hold.
- **macOS.** Runic's guest has no peer lookup on macOS. There an elevated guest refuses region mode (`region-attach`
  `rejected` with `refused`) and component mode, and its channel listener and page surfaces keep the protections of
  the elevated pair without the peer check.
- A guest that does not run elevated does not apply the rule.

## Limits

| Limit | Value | Over it |
| --- | --- | --- |
| Message size | 1 MiB | Channel closed with status 1009 |
| First message before the handshake | 16 KiB | Connection closed with status 1009 |
| A side's requests waiting for replies | 64 | `rejected` with `capacity` |
| Request timeout | 30 seconds, except requests that wait for the user | The requester gives up: `failed` |
| Send timeout | 10 seconds for one message | The sender closes the connection |
| Open windows of a guest | The host's policy | `window-open` `rejected` with `refused` |
| Requests per window | The host's policy, such as a rate | `rejected` with `capacity` |
| Menu requests per window (`set-menu-bar`, `update-menu-items`, `show-context-menu`) | The host's policy; Runic's host: 20 a second after a burst of 20 (`RunicHostedLimits`) | `rejected` with `capacity` |
| Menu requests of a guest, across its windows | The host's policy; Runic's host: 80 a second after a burst of 80 (`RunicHostedLimits`), which also bounds the native menu bar rebuilds of the guest's windows | `rejected` with `capacity` |
| Native menu bar rebuilds per window | The host's choice; Runic's host: one per 50 milliseconds, coalescing the requests meanwhile | — |
| Dialogs per window | 1 | `rejected` with `busy` |
| Dialogs of a guest at once, across its windows | The host's policy; Runic's host: 1 (`RunicHostedLimits`) | `rejected` with `busy` |
| Dialogs of a guest over time, across its windows | The host's policy; Runic's host: 10 per ten seconds after a burst of 10 (`RunicHostedLimits`), counting dialogs the guest cancelled | `rejected` with `busy` |
| Context menus | The active window's, inside its viewport | `rejected` with `refused`; the point is clamped |
| Dialog title, message and detail | 1,024, 4,096 and 4,096 characters | `rejected` with `invalid-request` |
| Clipboard and monitor requests of a guest, across its windows | The host's policy; Runic's host: 20 a second after a burst of 20 (`RunicHostedLimits`) | `rejected` with `capacity` |
| Files and links a guest opens, across its windows | The host's policy; Runic's host: 5 per ten seconds after a burst of 5 (`RunicHostedLimits`) | `rejected` with `capacity` |
| Requests that can activate a window (`present`, `minimize`, `set-visible`, `set-full-screen`, `toggle-maximized`), across a guest's windows | The host's policy; Runic's host: 2 a second after a burst of 3 (`RunicHostedLimits`) | `rejected` with `capacity` |
| Announcements of a guest, across its windows | The host's policy; Runic's host: 2 a second after a burst of 5 (`RunicHostedLimits`); `assertive` only from the window the user works in | `rejected` with `capacity`; made `polite` |
| Clipboard reads and writes, launches | The window the user works in only, by default; writes also within a second after | `rejected` with `refused` |
| Clipboard content, a read's value | One message; the host reads no more than that | `failed` with `too-large` |
| Dropped paths, monitors | 1,024 paths, 64 monitors | — |
| Menu bar depth and entries | 8 levels, 1,000 entries | `rejected` with `capacity` |
| Context menu depth and entries | 5 levels, 256 entries | `rejected` with `capacity` |
| Unauthenticated connections | 8 at once | The oldest is closed for a new one |
| Upgrade | 10 seconds from the accept | Connection closed |
| Hello timeout | 2 seconds from the upgrade to the first message | Connection closed |
| Start (offer to `hello` reply) | 30 seconds by default, on both sides | Guest exits; host ends the guest |
| Stop (`shutdown` to exit) | 10 seconds by default | Host ends the guest's process tree |
| Host gone to exit | 10 seconds | Guest process exits |
| `set-title` per window ([regions](#regions-region1)) | The host's policy; Runic's host: 10 a second after a burst of 10 (`RunicHostedLimits`) | `rejected` with `capacity` |
| `resize` of a region window | Counts toward the requests that can activate a window | `rejected` with `capacity` |
| `region-bounds` pushes | Coalesced per window, at most one per animation frame of the host page | — |
| `region-attach` per window | One waiting for its reply | — |
| Frame launch code | One-time; 30 seconds from the `region-attach` reply | 403; a spent code revokes the token |
| Pages a frame token admits | One connected page at a time | 403 |
| `embedder`, frame launch address, title | 256, 2,048 and 1,024 characters | `rejected` with `invalid-request` |

## Host start failures

A host reports a guest that it cannot host with one failure code (`RunicHostedApplicationException.Code` in .NET).
The vocabulary is open.

| Code | Cause |
| --- | --- |
| `guest-start-failed` | The guest's executable could not be started, such as one that does not exist, or could not be contained (a Windows job object). |
| `guest-start-timeout` | No channel line, or no `hello` reply, within the start timeout, retries included. |
| `guest-exited` | The guest exited before the `hello` reply. |
| `guest-not-hostable` | The guest wrote an invalid channel line, a reply carried no matching guest proof, or a proven reply failed for another reason than `protocol-mismatch`. |
| `guest-refused` | The guest wrote a refusal line; the failure names the guest's `code`, also one the host does not know. |
| `host-not-elevated` | The host allows an elevated guest (`elevatedGuest`) but does not run elevated itself. It starts no guest. |
| `protocol-unsupported` | The guest refused with `protocol-unsupported`, or answered `hello` with a proven `protocol-mismatch`. |

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
| 1 | 0.17 | First version, experimental (`RUNICDESK103`). 0.18 added, without a version change, the features `region/1` and `region-component/1`. |

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
   unauthenticated connections without a reply, holds at most 8 of them, closing the oldest for a new one, gives each
   2 seconds after its upgrade for its first message and reads at most 16 KiB of it. The host retries a connection
   that was closed before a reply.
7. **No web pages.** The guest refuses an upgrade with an `Origin` header and one whose `Host` is not its loopback
   address and port.
8. **No elevated guests, except the elevated pair.** A guest refuses hosted mode when it runs elevated, or when it
   cannot show that it runs as the same user as its parent ([same user](#same-user), fail closed), because a host
   controls what it does with the user's answers. The one exception is an elevated guest whose verified parent runs
   elevated as the same user, when both sides opted in ([elevated host](#elevated-host)); the guest checks the parent's
   elevation itself and refuses when it cannot. A host allows an elevated guest only when it runs elevated, and starts
   it only from a fully qualified path.
9. **Least privilege.** The channel gives a guest its own windows only: no list of the host's windows, no script in
   the host's pages, no navigation outside its own origin. Window ids are scoped to the channel, and `owner` names only
   the guest's own windows. The host validates every message and applies its policy and limits.
10. **Separate pages.** A hosted window injects no credential into the guest's page, does not admit the guest's origin
    as one of its own and uses a data store of its own for the host session. A region's frame shares its host window's
    data store instead, so the guest serves it cross-site to the host page and admits it without cookies
    (requirement 13).
11. **Lifetime.** The guest exits when the host goes away and never shows a window of its own; the host ends the
    guest's process tree when it stops.
12. **Window services.** A host reads and writes its clipboard and opens files and links for a guest only for the
    window the user works in, not for one the guest activated itself, unless its policy says otherwise; never reports
    clipboard changes to a guest; opens only the files and links its policy allows, with its own launcher's rules; and
    forwards only paths its platform reported for a drop onto the guest's window. It bounds how often a guest makes the
    requests that can activate its windows and announces, and makes only the window the user works in announce
    assertively.
13. **Regions.** A guest admits a region's frame only with a one-time frame launch code from the channel and the frame
    token it redirects to, in the address's fragment, never with a cookie; a token admits one connected page and dies
    at detach. A frame surface allows framing only by the `embedder` of the latest `region-attach`, listens on the
    loopback family that the embedder does not use, and falls back to the embedder's own only when the host allows it,
    which a browser host never does; an elevated guest refuses a browser host's page. The host gives the frame only the permissions its policy granted the guest's
    Window, routes drops and permission requests of the frame to the guest and never to its own page, never moves the
    focus out of its page for the guest, counts focus that the user did not move into the region as the guest's for
    the window services, and redacts frame tokens in console messages it forwards.
14. **Elevated peers.** An elevated guest admits connections to its listeners only from elevated loopback peers, and
    refuses region mode where it cannot check them ([elevated-peer admission](#elevated-peer-admission)).
15. **Components.** Component mode needs both opt-ins: a guest lists `region-component/1` only when its application
    allows the host, and a host shows a component only when its policy decides it. The component token travels only
    over the channel and to the page that declared the region, and is never in a URL. A guest admits a component's
    WebSocket only with `Origin` equal to the embedder and the token, one page at a time, and serves its component build,
    and its blobs and uploads cross-origin, only to the embedder; everything ends at detach. A component never runs
    script of its guest or navigates the host page. An elevated guest allows component mode only with elevated-peer
    admission on its listeners, and refuses it where it cannot check its peers
    ([component mode](#component-mode-region-component1)).

### Threats

| # | Threat | Mitigation |
| --- | --- | --- |
| T1 | Another local user connects to the channel or to a guest surface | Loopback listener, 256-bit key and host proof, one connection; the surfaces' launch binding and their refusal of another user's process (Linux) |
| T2 | A web page in any browser targets the channel (DNS rebinding, cross-site WebSocket) | Upgrades with `Origin` and non-loopback `Host` refused |
| T3 | Secrets leak through the command line or the environment | Nonce and key on pipes only; launch codes over the channel |
| T4 | A malicious guest takes the host's credential or cookies | No credential injection; a data store per host session; the guest's origin is not one of the host's |
| T5 | A malicious guest escapes its window (navigates away, opens pop-ups, spoofs other windows) | The host's web content policy; window ids per channel; the host's policy; context menus only in the active window and inside it. A guest can still draw convincing UI in its own window, as any application can, and words its dialogs' and menus' texts, which nothing marks as the guest's: a host that does not trust a guest marks or refuses them in its policy. |
| T6 | A guest floods the host with windows, dialogs, menus or messages | Message size, waiting requests, menu size, one dialog per window and the host's dialogs per guest at once and per ten seconds, the host's menu request rates per window and per guest and coalesced menu rebuilds, context menus only in the active window, the host's window limits |
| T7 | A malicious host forges dialog results or close decisions | Inherent to hosting: a guest trusts its host as it trusts its own native dialogs, so elevated guests refuse hosted mode unless their verified parent runs elevated as the same user and both opted in, and a guest acts only on what a dialog returned, as always |
| T8 | Orphaned guests | Standard input end or channel loss shut the guest down, which exits within 10 seconds; job object or process group; the host's stop timeout |
| T9 | Another process takes the port after the guest exited, and the host connects to it | The guest proof, which needs the nonce the host never sends |
| T10 | Version skew or downgrade | The offer's `protocols` and the guest's highest common choice; features negotiated; unknown members ignored and unknown kinds `invalid-request` |
| T11 | A local process that read one of the pipes, such as the guest's standard output in a log, opens the channel first | It needs both the key (standard output) and the host proof (standard input) |
| T12 | Another local user fills the listener's unauthenticated slots, or sends large first messages, to keep the host out | The oldest pending connection is closed for a new one, the hello timeout is 2 seconds, a first message is at most 16 KiB, and the host retries within its start timeout |
| T13 | A host that never answers, or a guest event handler that blocks, hangs the other side | The request timeout; a guest raises a window's events off the channel's receive loop and bounds its sends |
| T14 | A malicious guest snoops on the clipboard, such as a password the user copies in another application, or replaces it (pastejacking), also by making its own window active first | Reads and writes only for the window the user works in by default, not for one that became active within two seconds after any guest's request that can activate one of its windows (`window-open`, `present`, `minimize`, `set-visible`, `set-full-screen`, `toggle-maximized`, `window-closed`, the end of its dialogs and context menus, and a host policy's decision that did not answer at once), host-wide across guests, nor the first activation of a window whose activation the guest asked for and the desktop deferred, however late, writes also within a second after; only the host's own UI gives the window back to the user (the user's later activation, a choice in its native menu bar or in a context menu shown while the user worked in it, a drop), never a shortcut or the page; the WebView2 page's key and drop reports carry a token the page cannot read; no clipboard watch; the service rate and the rate of requests that can activate a window. Residual: a guest the user works in reads what the clipboard holds and writes it, as any focused application can; a policy can refuse reads or narrow writes. The host's own windows are not tracked: when one of them closes, hides or minimizes by itself, such as a finished progress window, the desktop may activate a guest's window, which then counts as the user's. With `RunicGuestStartInfo.UserStarted`, the guest's first window counts as the user's when it is first activated, whenever the guest opens it. GTK 4 menu-bar clicks do not yet give a window back to the user (a false refusal only). A guest's process runs as the user, so it can reach the desktop's own interfaces, such as the global menu's D-Bus objects, as any of the user's processes can; this rule covers what the protocol gives it. |
| T15 | A malicious guest opens files or links to run programs, phish or flood the desktop | Only `http`, `https` and `mailto` links and files the user gave the guest, only for the window the user works in and not one the guest activated itself (as for T14), by default; the host's launcher's own rules (no programs, scripts or `mailto` attachments, the `ms-settings` page rule); the launch rate |
| T16 | Paths in a drop or in the given set lead a guest elsewhere: a page forges a drop, a guest names a path through a given one (`../`), opens a file it wrote at a save dialog's destination, or replaces a given file, or links it to another, before it opens it | Dropped paths come from the host's platform only, never from a page; the page's `target` and `effect` are routing hints; given paths are compared without `.` and `..` segments, only open and folder dialogs and drops give paths, a folder the user chose does not give its contents, and a given path opens only while its file identity and its target with links resolved are the ones it had when given, checked again through the file the host opens for the launch: Linux launches that descriptor, Windows holds it, sharing neither delete nor write, across `ShellExecute`. Residual: macOS checks the path right before the launch, so a guest that wins that race there opens its own file, with the same user's rights and the launcher's rules; a change of content that keeps the file's identity (a rewrite in place, a new mode) passes everywhere; the opened application reads the path after the launch |
| T17 | A malicious guest speaks through the user's screen reader as the host application, such as a fake security alert, or floods it | Announcements are bounded by a rate, and `assertive` ones, which interrupt, come only from the window the user works in; others are `polite`. Residual: an announcement's text is the guest's, as its dialogs' are (T5); a policy can refuse or reword announcements. |
| T18 | A process of the same user that does not run elevated drives or reads an elevated guest of the [elevated pair](#elevated-host): it starts the guest itself, poses as its host, opens its channel or its surfaces, or makes the elevated host start another executable | The guest accepts only a parent that runs elevated as its user, verified against id reuse, and checks that itself; the nonce and the key travel only on pipes between the two elevated processes, and launch codes only over the channel, none of which an unelevated process can read (Windows: integrity levels keep a Medium process from reading a High one's memory or duplicating its handles; Linux and macOS: an unprivileged process can neither ptrace nor read the `/proc/<pid>/fd` and `/proc/<pid>/mem` of a root process). The host allows an elevated guest only from a fully qualified path, so it never looks one up in `PATH` or the current directory. Residual: such a process can still connect to the guest's loopback listeners, which refuse it without the key and proof, or without a launch code (on Linux a surface's launch binding also refuses another uid), and can fill their unauthenticated slots as in T12; see the screen, which the pair shows to the desktop, and read the clipboard, which the host serves the guest; and on Windows send the elevated windows only the messages UIPI lets through. An elevating launcher that relays another process's standard input and output, such as `sudo` with a pipe, or a Windows `sudo` that runs inline, makes that process the guest's parent's stand-in: it writes the offer and reads the channel line, so it is the host; the user's authentication for the launcher is the consent. The guest's executable, its directory and the directories above it must be writable only by administrators or root: the host does not check them, and the guest's runtime loads libraries from its own directory. Pages of the elevated guest load in the elevated host's web view, whose processes start from the host's token; how far the engine's own sandboxed processes are kept from unelevated ones is the engine's. |

Same-user processes are outside what Runic protects ([security guide](../../docs/security.md#what-runic-protects-and-what-it-does-not)):
any process of the user that knows a guest's hosted parameter can host it, see its pages and answer its dialogs. An
elevated guest of the [elevated pair](#elevated-host) is the exception: processes of its user that do not run elevated
can host it only through an elevating launcher (T18).
`RunicGuestOptions.AcceptHost` filters hosts by their name and version for the product's sake; it is not a security
boundary.

#### Threats in a region

The threats of [regions](#regions-region1) (`region/1`) and of [component mode](#component-mode-region-component1) (`region-component/1`, T24–T26):

| # | Threat | Mitigation |
| --- | --- | --- |
| T19 | A compromised host page, such as one with injected script or one that renders remote content, clickjacks an attached guest: it lays elements over the frame, makes it transparent, or moves it under the pointer before a click; or it misreports how the focus entered the region | Outside the host's threat model: the guest trusts its host as it trusts its own native dialogs (T7), and the host can already forge its dialogs' results, decide its closes and invoke its menu items. Such a page cannot attach a guest, since only the host's backend receives frame launch codes, and it already drives the host's own Views. Residual, not mitigated by Runic: host pages that render untrusted content should not embed guests; a guest can guard a sensitive confirmation itself, such as with IntersectionObserver v2 on Chromium |
| T20 | A guest's frame takes the keyboard focus from the host page with script and reads keystrokes meant for the host | `present` never takes the focus from the host page; focus that entered the region without the user's Tab or click counts as the guest's for the window services. Residual: the guest's page reads the keys typed while it has the focus, as a process of the same user can at the desktop level |
| T21 | A frame launch code or frame token leaks, such as through a console message of a framing that `frame-ancestors` blocked, or the host page uses the code itself | Codes are one-time and short-lived, and come only over the channel; the token is in a fragment, never sent to a server or in a `Referer`, and is removed from the address; a token admits one connected page at a time and dies at detach; a spent code presented again revokes the token; `frame-ancestors` admits only the embedder; console forwarding redacts `#runic-frame=`. Residual: a host page can keep the guest's page from loading by spending its code |
| T22 | The guest's surface receives the host page's cookies, or its frame shares the host page's renderer process | The frame surface is cross-site to the host page ([guest origin](#guest-origin)), and frame admission uses no cookie. Residual: the same-site fallback, which only an embedded host allows and logs; and in engines without site isolation (WebKit) the frame shares the host page's content process, which the same-origin policy still separates |
| T23 | A process that does not run elevated renders or drives an elevated guest's page, such as an installed browser or a process that obtained a frame launch code or token | [Elevated-peer admission](#elevated-peer-admission) on every listener of an elevated guest; on macOS an elevated guest refuses region mode. Residual: on Windows a process that an elevated process handed or let inherit its connected socket passes, as that process chose to, and so do services of High or System integrity |
| T24 | A component's code reads or changes the host page, uses its Bridge, or keeps running after the region went; the host page reads the guest's token or state and acts as the guest's page | Inherent to [component mode](#component-mode-region-component1), which merges the two trust domains at the page: both sides opt in, for applications of the same vendor, and each backend keeps its own policy for what the other's page asks. The token dies at detach, and the component's connection with it. Residual: a host that embeds a component trusts the guest's code as its own page's, and the reverse |
| T25 | Another page or process connects as the component: another local application's page in the user's browser, a web page, a process that is not a browser and sends the embedder's `Origin`, or a replay of a token after the region went | The upgrade needs both `Origin` equal to the embedder, which a browser cannot forge, and the token, which only the host's backend and page hold; the token is never in a URL; one page at a time; the token dies at detach and at the next attach. Residual: a process of the same user that reads the host's memory, outside what Runic protects |
| T26 | Another origin loads the guest's component build or reaches its blobs and uploads through CORS | The build, blob reads that name an origin and uploads are granted to the embedder of the current attach alone, with `Vary: Origin`; nothing before an attach or after a detach. Residual: a no-cors embedding of a blob, such as an `<img>` on another page, works with its URL alone while the component is attached, as the URL is the capability |
