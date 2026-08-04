# Portable command-line grammar, version 1

## Input model

Input is an already-tokenized ordered array of strings, equivalent to `Main(string[] args)`. The grammar never re-parses quoting, expands variables or globs, invokes a shell, normalizes Unicode, removes empty tokens, or changes culture. Matching is ordinal and command names are case-sensitive.

Commands use invariant names matching `[a-z][a-z0-9-]*`. Command aliases are explicit and normalize to the canonical command path. Nested commands consume leading tokens. The fixture catalog has no root handler, so empty input is a missing-command error.

Long options accept `--name value` and `--name=value`. A zero-arity Boolean flag accepts no value. Short options are explicit one-ASCII-letter aliases such as `-v`; short bundles are unsupported. Slash aliases such as `/verbose` are recognized only when the exact alias is registered. Other slash-prefixed tokens are data, which preserves absolute Unix paths.

`--` ends option recognition. Every subsequent token is positional data, including `--help`, a registered slash alias, an empty string, or a leading-hyphen token. Positional definitions declare order and arity; only the final positional may be unbounded. Option and argument values are preserved byte-for-byte after UTF-8 JSON decoding.

Scalar options and flags reject a second occurrence. Options configured with `repeat: append` preserve all occurrences in encounter order, including identical values. Built-in `--help` and `-h` are valid at the root and every command node, never invoke a handler, and normalize aliases to a help result. `--version` is root-only.

## Normalized results

Every corpus row has an immutable `id`, `args`, and `expected` object. `expected.kind` is one of:

- `invocation`: canonical `commandPath` plus ordered `options` and `arguments`. Each binding has a catalog `id` and an ordered string `values` array. A present flag has an empty values array; absent bindings are omitted.
- `help`: canonical `commandPath`; the empty path denotes root help.
- `version`: the reserved root version request.
- `error`: one or more ordered diagnostics.

Diagnostic `tokenIndex` is zero-based. When input ends before a required token, it equals `args.length`. Wave A assigns these stable grammar identities within the registered CLI diagnostic range:

| Code | Kind |
|---|---|
| `RCLI1001` | `unknown-option` |
| `RCLI1002` | `unknown-command` |
| `RCLI1003` | `missing-option-value` |
| `RCLI1004` | `unexpected-option-value` |
| `RCLI1005` | `missing-argument` |
| `RCLI1006` | `unexpected-argument` |
| `RCLI1007` | `duplicate-option` |
| `RCLI1008` | `unsupported-short-bundle` |
| `RCLI1010` | `invalid-output-mode` |

Usage and parse diagnostics map to the stable usage exit category, whose default process exit code is 2. Diagnostics contain token positions and safe identifiers, not secret-bearing values.

## Output classification

`--output human|json` is a reserved scalar selector recognized in separated and equals forms before `--`. It is case-insensitive for the two invariant values. Duplicate selectors, a missing value, or another value are usage errors. After `--`, the same text is positional data.

An explicit valid selector has highest precedence and causes `RUNIC_COMMANDLINE_OUTPUT` to be ignored, even when the captured environment value is invalid. Without an explicit selector, non-empty environment values `human` and `json` are recognized case-insensitively. Null and empty environment values are absent. Any other value, including whitespace-padded text, is invalid. If both sources are absent, the default is `human`.

Classification is a pure operation over captured arguments and a captured nullable environment value. Implementations must not read or mutate process-global environment state while replaying the corpus.
