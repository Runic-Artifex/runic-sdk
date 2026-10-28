# Views protocol 4 conformance corpus

Portable vectors and scenarios for the [Runic Application Views protocol](../README.md), version 4. Contract id
`runic.views`, version 4.

## Vectors

A vector file follows [`vector.schema.json`](vector.schema.json) (format `runic.views.vector/1`) and holds the cases of
one topic. Every case has an `id` that is unique in the corpus, an `operation`, an `input` and one `expect`.

### Canonical form (`vectors/canonical`)

`operation` is `canonicalize`. `input` is bytes: `utf8` gives the bytes as the UTF-8 encoding of `value`, and `base64`
gives them in base64, for inputs that are not UTF-8. The implementation reads the bytes as one JSON text under the
rules of the canonical form (RFC 8785 with the I-JSON profile, specified in `values.md`) and either writes its canonical form, which must equal the
UTF-8 encoding of `expect.output.value` byte for byte, or fails with the error `expect.error.code`:

| Code | Meaning |
| --- | --- |
| `invalid-utf8` | The bytes are not well-formed UTF-8. |
| `invalid-json` | The text is not one JSON text (RFC 8259), including a leading byte order mark. |
| `lone-surrogate` | A string or member name, after unescaping, contains a lone surrogate. |
| `duplicate-member` | An object has two members with the same name, compared after unescaping. |
| `depth-exceeded` | The value is nested deeper than 64. |
| `number-out-of-range` | A number rounds to an infinite double. |

[`jcs-testdata.json`](vectors/canonical/jcs-testdata.json) holds the sample inputs of the RFC 8785 reference test data
([cyberphone/json-canonicalization](https://github.com/cyberphone/json-canonicalization), Apache-2.0).
