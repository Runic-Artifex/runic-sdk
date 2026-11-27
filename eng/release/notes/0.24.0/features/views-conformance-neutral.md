## A Views conformance corpus for any language

The Views protocol 4 conformance corpus now states everything a producer or client in another language needs, so a
Rust or C++ implementation can be written from the specification alone. See the
[conformance corpus](../../../specs/application/conformance/README.md).

- **The `hello` reply names the Runic release.** A producer adds the optional member `release`, the version of the
  Runic release it ships with, to every `hello` reply; the .NET producer sends its package version. A client of
  another release can then report its version diagnostic for any Runic producer, not only one named
  `Runic.Application.Views`. Clients that do not read the member ignore it, as protocol 4 requires. See
  [Handshake](../../../specs/application/README.md#handshake).
- **Revisions are specified as .NET publishes them.** When one change shows on several routes, each route's frame
  takes its own revision; a publication withheld for invalid keys takes a revision too; and a checked field's
  `version` counts the field's changes, not revisions. Each rule has a scenario. See
  [Publication units](../../../specs/application/state-changes.md#publication-units).
- **The corpus describes its own setup.** Message vectors commit the `hello` a producer runner sends first and name the
  roles that run them; the README gives the valid-request criterion, the provided ids with their types, the closed
  list of requirement names (now with `blobs/1`, `client-errors/1`, `field-flush/1` and `validation-pending/1`), and the
  test Views in full, including the 256 MiB upload limit of the `files` View.
- **What a second producer found is specified as .NET behaves.** Rust and C++ producers written from the
  specification alone found the gaps that the corpus now closes, each with a case where it shows on the wire:
  reply and delivery order per route, the order of a `start`'s and an upload's checks, value directions and the
  field paths of rows that hold blobs, path-write receipts, operation and interaction identities, public names and
  optional members, a close without a timeout, the page-wait limit, and which terminal operation expires first.
  The .NET producer passes every new case unchanged. See [State changes](../../../specs/application/state-changes.md)
  and [contract IR](../../../specs/application/contract-ir.md).
- **The TypeScript client runs the client cases.** `bun run conformance ts-client` runs every client case of the
  corpus and its value vectors against the production build of `@runic-artifex/views`, with the driver playing the
  producer; a client in another language adds an adapter of its own.
