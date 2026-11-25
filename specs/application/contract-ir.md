# Views contract IR

The contract IR (`runic.views.contract-ir/1`) describes what one **producer unit** — a .NET model assembly, a Rust
crate or a C++ target — exposes to pages through the [Views protocol](README.md): its Views, their state members,
commands, interactions and view requests, and the value types they carry. It is language-neutral, so that one client
emitter generates the TypeScript clients of every producer language from it, and a producer in another language is
checked against the same descriptions.

The IR is a **build intermediate**. A producer writes one document per unit and build; it is never committed or
published, and nothing reads it at run time. The .NET generator writes `contract.ir.json` beside the generated C# in
`obj/<configuration>/<framework>/runic-bridge/`, after a generation without errors. The TypeScript emitter that will
read it is not part of this version; until it is, the generated TypeScript comes from the .NET generator itself and the
IR is checked against it through the [corpus](#corpus).

| File | Content |
| --- | --- |
| This document | The document and its parts |
| [`contract-ir/contract-ir.schema.json`](contract-ir/contract-ir.schema.json) | The JSON Schema (draft 2020-12) of format `runic.views.contract-ir/1` |
| [`contract-ir/corpus/`](contract-ir/corpus/index.json) | The IR of every example that commits its generated clients |

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

## Document

A document is one JSON object of the [JSON profile](values.md#json-profile):

| Member | Content |
| --- | --- |
| `format` | `runic.views.contract-ir/1` |
| `producer` | `language` (`csharp`, `rust` or `cpp`), `tool` (the program that wrote it), `unit` (the assembly, crate or target name), and `clientLibrary: true` in [library mode](#library-and-application-mode) |
| `clientModules` | In application mode, the client libraries whose clients the unit imports: `{unit, specifier}` |
| `views` | The [Views](#views), in the order the producer generates them |
| `sharedClients` | The [shared clients](#shared-clients), when the unit has any |
| `types` | The [named types](#named-types) by id |
| `sources` | Source locations by id ([below](#sources)) |

A producer MUST write a document that the schema accepts. A reader MUST reject a document whose `format` it does not
know and SHOULD ignore members it does not know. A change that an existing reader would misread is a new format
version; members are added in a new revision of the schema.

Optional flags are written only when true (`"settable": true`), and other optional members only when they have a
value. Documentation (`doc`) is Markdown text that the producer rendered from its language's doc comments (XML
documentation, rustdoc, Doxygen); the emitter writes it into TSDoc as it is.

### Names

Three kinds of names appear in the IR:

- **`wire`** names a member, command, interaction or view request on the wire and in the generated client. The
  emitter writes it unchanged ([values.md, Names](values.md#names)).
- **`name`** of a member, command, interaction, view request or shared client member is **UpperCamelCase**. The
  emitter appends it to `set`, `write`, `can`, `is…Executing`, `start` and `recover` (`setStep`, `canIncrement`). A
  producer converts its source name: a Rust producer its `snake_case` identifier (`step_size` is `StepSize`), a C#
  producer the member name with an uppercase first letter (C# naming makes it UpperCamelCase already). The schema
  requires a leading uppercase letter or `_`.
- **`id`** of a View, shared client or named type is the producer's stable identity, such as the .NET full type name.
  A View id is the `{view}` of the contract string `{view}:{fingerprint}` (protocol [Operations](README.md#operations)),
  so it changes only when the type is renamed or moved.

## Views

| Member | Content |
| --- | --- |
| `id` | The View id |
| `name` | The public name, such as `Counter` for `CounterViewModel`: letters and digits, starting with a letter. It prefixes the client's TypeScript names (`CounterState`, `CounterClient`). |
| `route` | The route prefix and module name: `name` with a lowercase first letter (`counter`, `counter.ts`) |
| `fingerprint` | The producer's contract fingerprint, 1 to 128 characters from U+0021 to U+007E without `:`. Opaque to the emitter, which writes it into the client's contract string. The .NET producer writes its `runic-bridge-contract-v6` hash, which covers every View of the assembly ([language mappings](language-mappings.md#net-producer)); a later revision derives a fingerprint per View from the IR. |
| `kinds` | The presentation kinds, one per View contract (`notes`, `notesCompact`); the first is the default |
| `doc` | The View's documentation |
| `developmentOnly` | A View that a trimmed or NativeAOT publish leaves out. Its client is the same in every configuration. |
| `owner` | In application mode, the `unit` of the client library that owns the View's client |
| `memberErrors` | Every state member has a `{wire}Errors` member (`INotifyDataErrorInfo`) |
| `validation` | The View has the `validation` state member |
| `uploads` | A command input or settable member takes an `upload`, so the client has `upload()` |
| `sharedClients` | The ids of the shared clients the View implements |
| `members` | The [state members](#state-members), in declaration order |
| `commands` | The [commands](#commands), in declaration order |
| `interactions`, `requests` | The [interactions and view requests](#interactions-and-view-requests) |

### State members

Every member has `name`, `wire`, `kind` and optionally `doc` and `developmentOnly`. A development-only member is
absent in a trimmed or NativeAOT publish, so its client types it as possibly missing and gives it no field descriptor.

| `kind` | Content |
| --- | --- |
| `value` | `value`: the [value position](#values) of the member |
| `selection` | A `NavigationSelection`: `value` is a nullable `int32` index, sent to the page |
| `content` | Presented content: `pages`, the Views it may present, most specific first; `nullable`; `region` for a navigation region slot, which presents its region's current content |
| `content-list` | A list of presented content: `pages`, `nullable` |
| `region-collection` | A region collection ([protocol](README.md#region-collections)): `pages`, and `pageCanClose`, whether the page may close a region |
| `query-collection` | A query collection ([protocol](README.md#query-collections)): `rows`, the value position of its rows (a list); `defaultKey`, the row member a mock keys rows by, when there is one |

A page reference in `pages` is `{view, name, kind}`: the presented View's id and public name, and the presentation kind
the member shows it with.

A `value` or `selection` member may also have:

| Member | Content |
| --- | --- |
| `settable` | The page sets it: the client has `set{Name}`. `declaringType` names the source type that declares the setter, for its documentation. |
| `checked` | A checked field ([protocol](README.md#checked-writes)): the client has `write{Name}` and a field descriptor |
| `constraints` | Of a checked field: the [constraints](#constraints) a page may mirror on its input |
| `fieldPaths` | The [field paths](#field-paths) below a settable object member, or below the rows of an editable collection |
| `text` | Its changes may be text splices (`text-splice/1`) |
| `appendOnlyText` | Append-only text (`defineText`) |
| `collectionKey` | A keyed collection: the `wire` of the row member that is its key |
| `rowEditor` | The page edits the cells of the keyed collection's rows |

### Commands

| Member | Content |
| --- | --- |
| `name`, `wire`, `doc` | As for members. The protocol's `can{Name}` and `is{Name}Executing` state members use `name`. |
| `parameter` | The parameter name of the client's command method; present exactly when the command takes an argument |
| `input` | The argument's value position. A keyed command's input is its key. `untyped: true` marks a legacy string argument without a typed input graph. |
| `result`, `failure` | The result and the declared failure ([protocol](README.md#commands-and-availability)) |
| `async` | Also an operation: the client has `start{Name}` and `recover{Name}` |
| `stream` | An operation that streams its result values |
| `executionState` | The `is{Name}Executing` state member exists |
| `keyed` | The producer resolves the key to the command's own parameter, and answers `unresolved-key` when it finds none |
| `scoped` | Scoped to its presentation (`presentation-epoch/1`) |
| `developmentOnly` | A trimmed or NativeAOT publish rejects it with reason `unavailable` |

A command without an argument has the `can{Name}` state member; one with an argument has the `can{Name}(argument)`
query instead.

### Interactions and view requests

An interaction has `name`, `wire`, `doc`, `contract` (`{fingerprint}:interaction:{Name}`), `input` and `output`. A view
request has the same without `output`, and the contract `{fingerprint}:request:{Name}`
([protocol](README.md#interactions)).

## Values

A value position is `{direction, type}`. `direction` is where its values cross: `to-page` (state the page reads,
results, failures, view request inputs), `from-page` (command inputs, interaction outputs) or `both` (settable
members). It decides which object members a page may leave out: a member that is `optional` may be missing only from a
value that comes from the page ([values.md](values.md#composite-values)).

A type is an object with a `kind` and `nullable: true` when `null` is a value:

| `kind` | Wire value type |
| --- | --- |
| `boolean`, `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`, `big-integer`, `float32`, `float64`, `decimal`, `string`, `uuid`, `local-date`, `local-time`, `date-time`, `offset-date-time`, `duration`, `text-reference`, `blob`, `upload` | The scalar of that name in [values.md](values.md#wire-value-types) |
| `json` | Any JSON value. `jsonNull` says whether JSON `null` is a value (`true`) or only the absence of one. |
| `list` | An array of `element` |
| `map` | An object with string keys and `value` values |
| `named` | The named type `ref`, a key of `types`: an enum, an object or a union |
| `custom` | A value with its own codec: `codec` names it, and `typescript` gives the page-side form the codec declares (`type`, and the `decode` and `encode` expressions, in which `$value` stands for the value) |

### Named types

`types` holds one definition per source type **and member shape**: two uses of one generic type whose members differ in
nullability (`Box<string>` and `Box<string?>`) are two definitions. An id is the producer's name for the type,
followed by `#2`, `#3` for further shapes of the same type.

| Member | Content |
| --- | --- |
| `kind` | `enum`, `object` or `union` |
| `name` | Name hints: `source`, the source type's full name; `base`, an identifier the emitter names the type from (`PageOfNoteRow` for `Page<NoteRow>`); `annotatedBase`, the base name with the use's nullable annotations (`BoxOfNullableOfString`), when it differs; `qualifiers`, the declaring types and then the namespace or module segments, innermost first. An emitter that needs distinct names takes `base` (`annotatedBase` when two definitions share a source type), and qualifies colliding names with qualifiers, ordering ties by `source`. |
| `doc` | The type's documentation |
| `cases` | `enum`: `{wire, value, doc}` in declaration order, where `value` is the case's integer value as decimal text. Aliases share a value; the wire name a producer writes for a value is the first case that has it. `union`: `{tag, object}`, where `tag` is the `$case` name and `object` the named object of its fields, or `null` for a case without fields. |
| `members` | `object`: `{wire, type, optional, absent, doc}` in declaration order. `optional`: the producer fills the member in when a page leaves it out. `absent`: a `json` member left out of the object when it holds no value. |

Type graphs are finite: a producer rejects a recursive type before it writes the IR.

### Constraints

A checked field, and each field path, may carry the constraints of its source member that a page can mirror on an
input: `required`, `minLength`, `maxLength`, `min` and `max` (numbers, or the text of a decimal, date or time bound in
its wire form), `minExclusive`, `maxExclusive`, and `pattern`, only when a browser's `pattern` attribute (with the `v`
flag) reads it as the producer does. They describe the producer's validation, which still runs; they are not
wire-visible.

### Field paths

`fieldPaths` lists the fields below a settable `object` member, and below the rows of a keyed collection with a row
editor (`field-paths/1`), as `{path, constraints}`. `path` is the list of `wire` names from the member's object (or
the row) down to the field, at most three deep, in the order the client declares them; a path whose field is itself an
object is followed by the paths below it. The type of each field is found by following the path through the named
object types.

## Shared clients

A shared client is one client type for the Views that implement a common interface (`[RunicSharedClient]` in .NET), so
that one component renders all of them:

| Member | Content |
| --- | --- |
| `id`, `name` | The interface's id, and the name of the shared client (`ResultPage`), which names `{name}State`, `{name}Client`, `{name}PageReference` and the module |
| `interface` | The interface's name in the source language, for documentation |
| `doc` | The interface's documentation |
| `owner` | In application mode, the client library that writes the module |
| `views` | The ids of the implementing Views |
| `members` | The interface's members: `name`, `doc`, `settable` (the shared client sets it), and `published`, by View id, the View `member` or `command` (its `wire`) that publishes it |

A producer writes a shared client only when every implementing View publishes every member. The emitter compares the
parts of the client each View has for a member and shares those that are alike.

## Library and application mode

In **library mode** (`producer.clientLibrary`), the unit's clients are a module that other frontends import: the
emitter also writes the barrels of the clients and the mocks, and the contract of each client.

In **application mode**, `clientModules` lists the client libraries whose Views the unit presents. Their Views appear in
`views` with `owner`, because the unit's content references them, but the emitter writes no module for them and
imports their clients from the library's `specifier`.

## Sources

`sources` gives the source location of each thing a client declares, so that a declaration map leads from the
generated client to the producer's source: `{file, line, column}` with 1-based line and column. Ids are a View id, or
`{view}/{kind}/{member}` with `kind` one of `state`, `setter`, `command`, `commandState`, `interaction` and `request`,
and `member` the client member name (`canIncrement` for a command state). `file` is absolute, or relative to the
directory of the document. A location the producer does not know is left out.

## Corpus

[`contract-ir/corpus/`](contract-ir/corpus/index.json) holds the IR of each example whose generated clients the
repository commits. `index.json` lists for each document the example project that the IR was written from and the
directory of its committed generated clients, which are the expected output of an emitter for that IR. The sources are
relative to the corpus directory.

The engineering test `eng/views-contract-ir.test.mjs` (`bun run test eng/views-contract-ir.test.mjs`, and in the
engineering job) checks every corpus document against the schema, and that the contract of each of its Views is the
one the example's committed clients carry, so a change to an example's contract that leaves the corpus behind fails.
