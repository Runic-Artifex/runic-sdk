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
path, and that it is self-contained. The .NET guest runner belongs in `Runic.Application.Views.Desktop.Tests` and a
host runner in the tests of Runic's host; each is added with its implementation.

## Scenarios

A scenario file follows [`scenario.schema.json`](scenario.schema.json) (format `runic.host.scenario/1`). Its `id` is
`{set}.{file}.v{n}` for the file `scenarios/{set}/{file}.json`. `roles` names the role it checks, `requires` the
features it needs, `arrange` the limits and application of the implementation under test, `ids` every identifier it
uses, and `steps` what happens, in order.

### Arrange

- `limits`: `startTimeout`, `stopTimeout` and `unauthenticatedTimeout`, in milliseconds, that the runner configures
  the implementation under test with. A scenario that waits for a limit names it in an [`advance`](#steps) step; a
  runner waits the configured time, so scenarios use small values.
- `application`: for the `guest` role, the guest application's `name`, `version` and `applicationId`, which its
  successful `hello` reply carries.
- `host`: for the `host` role, the host's `name` and `version`, which its offer carries.

### Steps

Each step has one action. `connection` names the connection of a `send`, `receive`, `closed` or `disconnect` step;
`note` is a comment.

| Step | Guest runner (plays the host) | Host runner (plays the guest) |
| --- | --- | --- |
| `start` | Starts the guest with `offer` written to its standard input as the offer line, `offerLine` as raw text, or nothing (`"none"`) | Asks the host to start the guest |
| `line` | Requires the guest's next line with a launch prefix: `channel` (and its members), `refused` (and its members), or `"none"`, no such line before the guest exits | Writes the line: the channel line for its own listener, a refusal, or raw text |
| `offer` | — | Requires the host's offer line to match |
| `connect` | Opens a WebSocket to the channel line's `url`, with `headers` added and the `Host` header or the path replaced by `host` or `path` (`{port}` in them is the listener's port), and requires the upgrade's `status` (101 by default), or with `refused`, that the connection is refused or closed before an upgrade response | — |
| `accept` | — | Accepts the host's connection on its listener |
| `send` | Sends the message to the guest | Sends the message to the host |
| `receive` | Requires the guest's next message on the connection to equal it | Requires the host's next message on the connection to equal it |
| `closed` | Requires that the guest closes the connection, with `status` when given, without sending another message | The same, for the host |
| `disconnect` | Closes the connection | Closes the connection |
| `closeInput` | Closes the guest's standard input | — |
| `exit` | — | Ends its own process with `code` |
| `advance` | Waits for the named limit of `arrange.limits` to pass | The same |
| `guest` | Makes the test application do one [action](#test-application) | — |
| `expect` | Checks the outcome of the guest application: an action's result, the guest's start failure, its exit | Checks the host's outcome: its start failure code, its windows |

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
  window id that the guest chooses and the runner binds the same way. A `wire` id has a fixed `value`.
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

## Test application

A guest runner starts a guest application with these Windows and actions. The Windows' pages are not loaded: the
runner checks the protocol, not the page. A guest application under test opens no Window on its own.

| Window type | Guards its close |
| --- | --- |
| `Main` | no |
| `Guarded` | yes: its guard answers what the last `answerGuard` action said, `true` before any |

| Action | Members | Result for `expect` |
| --- | --- | --- |
| `open` | `window` (a `window` id it binds), `type`, optional `owner` (a `window` id), `modality` | `opened`, or `failed` with a code |
| `close` | `window` | — |
| `requestClose` | `window` | `closed`: `true` or `false` |
| `present`, `minimize` | `window` | A platform outcome: `success`, `unavailable` with `reason`, `failed` with `code` |
| `setVisible`, `setFullScreen`, `setDocumentEdited` | `window`, `value` | A platform outcome |
| `setColorScheme` | `window`, `value` | A platform outcome |
| `answerGuard` | `allow` | — |
| `activity` | `window` | The Window's `IRunicWindowPresentation.Activity`, `{active, minimized, visible}` |

`expect` names the action by the step's `as`, or checks `startFailure` (the guest's hosted start failed with a code),
`exited` (the guest process exited; a code when given), or `running` (the guest still runs).
