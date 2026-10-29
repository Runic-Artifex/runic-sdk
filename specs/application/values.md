# Views protocol 4: values and the canonical form

This document belongs to the [Runic Application Views protocol](README.md), version 4. It defines how values are
written in messages (the wire value types) and the canonical form used to compare JSON values. The cases are in
[`conformance/vectors/values`](conformance/vectors/values) and
[`conformance/vectors/canonical`](conformance/vectors/canonical).

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

## JSON profile

Every message and every value in it is I-JSON (RFC 7493):

- The text is UTF-8 without a byte order mark.
- Strings and member names contain no lone surrogate, whether literal or escaped.
- An object has no two members with the same name, compared after unescaping.
- Numbers are IEEE 754 doubles. A number that rounds to an infinite double is invalid. A number that rounds to zero,
  such as `1e-400`, is zero.
- A value is nested at most 64 deep. A scalar has depth 0, and an array or object has the depth of its deepest member
  plus 1, so an empty array has depth 1. The limit applies to a whole message: a value inside a message is limited by
  its position.

A reader rejects a message that breaks this profile. A writer never writes one.

## Wire value types

A contract (the generated client and the producer) gives each member, command input, result, failure, interaction
input and output a type. The wire value types are named after the value they carry, not after a language's type. The
[language mappings](language-mappings.md) list which .NET and TypeScript types map to them.

Each type has a **normal form**. A writer MUST write values in their normal form. A reader MUST accept every valid
form and MUST reject everything else as invalid input. Two values of one type are the same value when their normal
forms are equal.

### Scalars

| Type | JSON | Valid | Normal form |
| --- | --- | --- | --- |
| `boolean` | `true` or `false` | | the same |
| `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32` | number | a number whose value is an integer within the type's range: −2^7 … 2^7−1, 0 … 2^8−1, −2^15 … 2^15−1, 0 … 2^16−1, −2^31 … 2^31−1 and 0 … 2^32−1. `1.0`, `1e2` and `-0` are integers. | the integer in the canonical number form (`100`, `0`) |
| `float64` | number | any number of the JSON profile | the number in the canonical number form |
| `float32` | number | a number whose nearest IEEE 754 single-precision value is finite | the shortest decimal number that rounds to the same single-precision value (`0.1` for the single nearest to 0.1) |
| `int64`, `uint64` | string | `-?(0\|[1-9][0-9]*)`, at most 4,096 characters, within −2^63 … 2^63−1 or 0 … 2^64−1 | the decimal digits without sign for zero (`"-0"` is `"0"`) |
| `big-integer` | string | `-?(0\|[1-9][0-9]*)`, at most 4,096 characters | as `int64` |
| `decimal` | string | `-?(0\|[1-9][0-9]*)(\.[0-9]+)?`, at most 4,096 characters, with at most 28 digits after the point, and the digits without the point, read as an integer, below 2^96 | the same text, except that a negative zero loses its sign (`"-0.00"` is `"0.00"`). Trailing zeros are kept: `"1.50"` and `"1.5"` are different values. |
| `string` | string | any string of the JSON profile | the same |
| `uuid` | string | 8, 4, 4, 4 and 12 hexadecimal digits separated by `-`, in either case | lowercase |
| `local-date` | string | RFC 3339 `full-date` (`YYYY-MM-DD`), a valid date of the proleptic Gregorian calendar from `0001-01-01` to `9999-12-31` | the same |
| `local-time` | string | `hh:mm:ss` with an optional fraction of 1 to 7 digits (`hh` 00–23, `mm` and `ss` 00–59) | without trailing zeros in the fraction, and without the fraction when it is zero (`"10:30:00"`, `"10:30:00.5"`) |
| `date-time` | string | a `local-date` and a `local-time` joined by `T`, followed by nothing (a local date-time), `Z` (UTC) or an offset `±hh:mm` within ±14:00 | the date and time in the forms above, then nothing, `Z` or the offset as read (`"+00:00"` stays `"+00:00"`) |
| `offset-date-time` | string | as `date-time`, but the offset (`Z` or `±hh:mm`) is required | the date and time in the forms above, then `Z` when the offset is zero, otherwise the offset |
| `duration` | string | ISO 8601 `-?P(nD)?(T(nH)?(nM)?(n(.f)?S)?)?` (below) | below |
| `enum` | string | one of the names the contract defines for the type, compared exactly | the same |
| `json` | any | any JSON value of the profile; with `nullable: false`, not `null` | the same value |

`T` and `Z` are uppercase. A reader rejects lowercase forms, a leap second (`ss` = 60), week dates, ordinal dates and
any other ISO 8601 form not listed here.

Integers above 2^53 in a `json` value, or in a `float64`, are read as the nearest double, as a JavaScript page reads
them. A contract that needs exact 64-bit or larger integers uses `int64`, `uint64`, `big-integer` or `decimal`.

#### `duration`

A duration is a signed number of 100-nanosecond units from −2^63 to 2^63−1. Its JSON form is an ISO 8601 duration
(the JSON Schema `duration` format, which `Temporal.Duration.from` reads) restricted to days, hours, minutes and
seconds:

```text
duration = ["-"] "P" [n "D"] ["T" [n "H"] [n "M"] [n ["." f] "S"]]
n = 1*DIGIT    f = 1*7DIGIT
```

- At least one component is present, and a `T` is followed by at least one component.
- A day is exactly 24 hours. Years, months and weeks (`Y`, `M` before `T`, `W`) are rejected, because they have no
  fixed length, and so is a fraction on any component other than seconds.
- Components may exceed their usual range when reading (`PT36H` is 1.5 days), but the total must be within the range
  above.
- A reader rejects a `,` as the decimal separator, lowercase letters and a sign on a component.

The normal form writes the sign `-` for a negative duration, then days when nonzero, then `T` and the hours (0–23),
minutes (0–59) and seconds (0–59, with the fraction without trailing zeros) that are nonzero. Zero is `PT0S`.
Examples: `P1DT2H3M4.5S`, `PT0.0000001S`, `-PT1H`, `P1D`, and the extremes `-P10675199DT2H48M5.4775808S` and
`P10675199DT2H48M5.4775807S`.

### Composite values

| Shape | JSON | Rules |
| --- | --- | --- |
| nullable `T` | `null` or a `T` | `null` only where the contract declares the value nullable |
| array of `T` | array | each element a `T` |
| map of `T` | object | member names are any strings; each member value a `T`; member order carries no meaning |
| object | object | one member per field the contract defines, named by its wire name (below) |
| union | object | a member `$case` (string) names the case; the other members are the fields of that case |
| content reference | `{"kind", "id"}` or `null` | `kind` is the presented View's kind name and `id` the content's id (see [Content](README.md#content)) |
| custom | any | a value type with its own codec; its contract specifies its JSON |

For objects and unions:

- A writer writes every field, except an optional `json` field that holds no value, which it leaves out.
- A reader fills in a missing field that is nullable or has a default value, and rejects a value that lacks any other
  field.
- A reader ignores members the type does not define.
- `$case` is reserved: no object field is named `$case`.

### Names

Wire names of members, fields, commands and interactions, enum names and union case tags are strings that the
contract defines. The protocol does not transform them: a reader compares them exactly, and a client exposes them
to the application as they are. Two conventions apply to the names that generated code derives:

- A command `{name}` gives the state members `can{Name}` and `is{Name}Executing`, where `{Name}` is `{name}` with its
  first character in uppercase ([Commands](README.md#commands-and-availability)).
- Multi-word protocol tokens, such as `kind` and `reason` values, are kebab-case (`domain-failed`, `result-too-large`).
  A new token follows the rule.

How Runic's .NET generator chooses the names is in the [language mappings](language-mappings.md#names). Enum names and
union case tags follow different conventions there on purpose.

## Canonical form

The canonical form of a JSON value is RFC 8785 (JSON Canonicalization Scheme, JCS) applied to a value of the JSON
profile above:

- No whitespace.
- Object members are sorted by their names as arrays of UTF-16 code units, compared unit by unit (so U+1F600, the
  surrogates D83D DE00, sorts before U+FFFF). Arrays keep their order.
- Strings escape only `"` as `\"`, `\` as `\\`, and U+0000 to U+001F: `\b`, `\t`, `\n`, `\f` and `\r` in their short
  forms and the others as `\u00xx` with lowercase hexadecimal digits. Every other character, including U+007F,
  U+2028, `/`, `<`, `>`, `&`, `'` and `+`, is written as literal UTF-8. Strings are not normalized.
- Numbers are written as ECMAScript's `Number::toString` writes the double (the shortest form that reads back as the
  same double): `1.0` and `1e0` are `1`, `-0` is `0`, `1e21` is `1e+21`, `0.0000001` is `1e-7`.
- `true`, `false` and `null` are written as they are.

The canonical form is defined for every value of the JSON profile and for nothing else. Canonicalizing rejects
invalid UTF-8, text that is not one JSON text (including a leading byte order mark), lone surrogates, duplicate
member names, depth above 64 and numbers that round to an infinite double. In JavaScript, the canonical form of a
valid value is `JSON.stringify` with the object members sorted.

### Canonical equality

Two JSON values are **canonically equal** when their canonical forms are equal byte for byte. The protocol uses
canonical equality, and nothing else, to compare values that a client sent:

- an operation `start` with a known operation id: its `member` and `input` against the original
  ([Operations](README.md#operations));
- a checked `write`: `expectedValue` against the field's current value, written in its normal form
  ([Checked writes](README.md#checked-writes));
- a repeated `interaction-reply`: its `outcome` and `output` against the reply already received
  ([Interactions](README.md#interactions)).

A producer MAY store a digest of the canonical form instead of the form itself. The digest algorithm is the
producer's own and never appears in a message, so it cannot make two implementations disagree.

### Messages

Messages need not be canonical: member order and number layout carry no meaning. A writer SHOULD escape strings as the
canonical form does, which keeps non-ASCII text at its UTF-8 size.
