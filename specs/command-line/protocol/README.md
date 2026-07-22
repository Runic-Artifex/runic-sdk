# `webuitoolkit.cli/1` machine response protocol

This directory is the language-neutral, offline contract for WebUIToolkit
command-line machine output. `--output json` or the captured
`WEBUITOOLKIT_CLI_OUTPUT=json` value selects this protocol. The namespace of
the implementation remains `WebUIToolkit.CommandLine.*`; neither a parser
package nor a hosting framework is part of the wire contract.

The words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are normative.

## Wire framing and encoding

A response is exactly one compact JSON object encoded as strict UTF-8 and
followed by exactly one byte `0A` (LF). The first byte MUST be `{`; the final
two bytes MUST be `}` and LF. A producer MUST NOT emit a UTF-8 BOM, a leading
or trailing space, CRLF, a second JSON value, an ANSI escape sequence, a log
line, or progress output on stdout. JSON strings escape control characters in
the usual JSON form. stderr MAY contain operator diagnostics, but a client
MUST NOT need stderr to interpret the response.

The complete framed response MUST be at most 1,048,576 bytes, including the
terminal LF, and JSON nesting MUST be at most 32 levels. A reader rejects an
oversized response while continuing to drain the process pipe. NDJSON and
streaming are not `webuitoolkit.cli/1`.

## Envelope

Every envelope contains all nine keys below. Mutually exclusive values remain
present as JSON `null`. Producers emit the keys in this order for reproducible
output; consumers MUST NOT depend on property order.

| Key | JSON type | Rule |
|---|---|---|
| `protocol` | string | Exactly `webuitoolkit.cli/1`. |
| `requestId` | string | Valid opaque request identifier, as defined below. |
| `command` | string | Non-null canonical command path separated by one ASCII space. |
| `success` | Boolean | True only for the success outcome category. |
| `exitCode` | integer | Signed 32-bit process exit code; zero exactly when `success` is true. |
| `payloadType` | string or null | Required and non-null on success; null on failure. |
| `payload` | any JSON value | Typed result on success, including JSON null when its type permits null; null on failure. |
| `fault` | object or null | Null on success; required and non-null on failure. |
| `diagnostics` | array | Zero to 64 safe diagnostic objects. |

Success has `success: true`, `exitCode: 0`, a supported non-null
`payloadType`, a payload value, and `fault: null`. Failure has
`success: false`, a nonzero `exitCode`, `payloadType: null`, `payload: null`,
and a fault. The default failure exit codes are 2 usage/parse, 3 validation,
4 cancelled, 5 unavailable, 10 expected command failure, and 70 unexpected
host/software failure. A host policy may select another nonzero signed 32-bit
value without changing the semantic fault.

The `payload` key being JSON null is not evidence of failure. Clients use
`success`, `payloadType`, and `fault` together and validate all invariants.

## Identifier validation

Limits below count UTF-8 bytes after JSON decoding, not UTF-16 code units or
escaped source characters.

- `requestId` is 1 to 128 UTF-8 bytes. It is opaque, is never interpreted as
  authorization or executable input, and MUST contain no Unicode control,
  whitespace, surrogate, or noncharacter scalar. A caller-supplied invalid ID
  is rejected before execution; a dispatcher otherwise generates one.
- `command` is 1 to 512 UTF-8 bytes and matches
  `[a-z][a-z0-9-]*( [a-z][a-z0-9-]*)*`.
- `payloadType` is an independently versioned ASCII identifier of 3 to 128
  bytes matching `[a-z][a-z0-9.-]*/[1-9][0-9]*`. A client MUST compare the
  complete identifier with its allow-list before deserializing `payload` and
  MUST report an unsupported or mismatched identifier as a protocol failure,
  not as a command fault.
- Fault and diagnostic codes are 1 to 64 ASCII bytes matching
  `[A-Z][A-Z0-9_.-]*`. Codes owned by this library use `WUTCLI####`.

The protocol deliberately defines no JSON Schema `$id` and assigns no domain
name. Payload owners choose their own type identifier within their documented
application boundary.

## Fault object

A fault contains every key below in this output order:

```json
{"code":"WUTCLI3001","message":"The command could not complete.","details":{},"retryable":false}
```

- `code` is a stable code satisfying the identifier rule.
- `message` is a nonempty safe presentation string of at most 4,096 UTF-8
  bytes.
- `details` is an object with at most 32 unique entries. Keys are 1 to 64 UTF-8
  bytes; values are strings of at most 1,024 UTF-8 bytes.
- `retryable` is Boolean and means retrying the same logical operation may
  succeed. It never authorizes an automatic retry.

## Diagnostic object

A diagnostic contains every key below in this output order:

```json
{"code":"WUTCLI2001","kind":"invalid-format","commandPath":["export"],"messageKey":"diagnostics.invalid-format","message":"The value is invalid.","phase":"binding","severity":"error","tokenIndex":2,"arguments":["format"]}
```

- `code` is exactly `WUTCLI` followed by four ASCII digits other than
  `0000`.
- `kind` is a stable symbolic identity of at most 128 UTF-8 bytes matching
  `[a-z][a-z0-9-]*`, without consecutive or trailing hyphens.
- `commandPath` is the ordered canonical path. The catalog root is an empty
  array. Each segment matches `[a-z][a-z0-9-]*`; joining the segments with one
  ASCII space MUST be at most 512 UTF-8 bytes.
- `messageKey` is a stable localization key of 1 to 128 ASCII bytes. Its first
  character is an ASCII letter; remaining characters are ASCII letters,
  digits, dots, underscores, or hyphens.
- `message` is nonempty and at most 4,096 UTF-8 bytes.
- `phase` is exactly `parse`, `binding`, or `execution`.
- `severity` is exactly `error`, `warning`, or `information`.
- `tokenIndex` is null or a nonnegative signed 32-bit integer. It indexes the
  already-tokenized argument array and MAY equal its length for missing input.
- `arguments` is an ordered array of at most 16 safe strings. Each value is at
  most 1,024 UTF-8 bytes. Raw token values are excluded unless the command
  definition explicitly classifies the value as safe.

Fault messages, fault details, diagnostic messages, and diagnostic arguments
are consumer-safe surfaces. They MUST NOT contain secrets, original values of
sensitive options, stack traces, exception type names, environment-variable
values, absolute internal paths, line breaks, terminal controls, or Unicode
control characters. Unexpected exceptions are logged only to an authorized
diagnostic sink and map to the sanitized `WUTCLI5000` host fault.

## Compatibility

A version-1 reader MUST ignore unknown fields at the envelope, fault,
diagnostic, and payload levels while still enforcing all known fields and
limits. Producers MUST emit every required known field. Adding an optional
unknown field is compatible. Removing a required field, changing a known
field's JSON type, weakening an invariant, or reinterpreting a known value
requires a new protocol major identity. Duplicate JSON property names are
invalid rather than last-value-wins.

Payload compatibility is independent of envelope compatibility. A client
MUST validate `payloadType` before invoking its pre-registered, non-reflection
JSON metadata. Arbitrary object deserialization and type-name activation are
not supported.

## Corpus layout

`manifest.json` records the fixture inventory and all machine-readable limits.
Files in `examples/` are complete valid envelopes. `invalid-structures.json`
contains parseable JSON documents that violate envelope semantics.
`wire-inputs.json` represents malformed or hostile stdout bytes as base64 so
every checked-in `.json` file remains strict, valid JSON and can be validated
offline without executing a process.
