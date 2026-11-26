# Views contract IR

The contract IR (`runic.views.contract-ir/1`) describes what one **producer unit** — a .NET model assembly, a Rust
crate or a C++ target — exposes to pages through the [Views protocol](README.md): its Views, their state members,
commands, interactions and view requests, and the value types they carry. It is language-neutral, so that one client
emitter generates the TypeScript clients of every producer language from it, and a producer in another language is
checked against the same descriptions.

The IR is a **build intermediate**. A producer writes one document per unit and build; it is never committed or
published, and nothing reads it at run time. The .NET generator writes `contract.ir.json` beside the generated C# in
`obj/<configuration>/<framework>/runic-bridge/`, after a generation without errors. Since 0.24 the build writes the
generated clients from it with the TypeScript emitter, `generate` of `@runic-artifex/views/codegen`, which Node.js or
Bun runs: the frontend's copy, or without a frontend the one bundled with `Runic.Application.Views`
([Incremental generation](../../packages/dotnet/Runic.Application.Views/README.md#incremental-generation)). The .NET
generator writes no TypeScript. The [corpus](#corpus) and the generator tests' goldens pin the emitter's output.

| File | Content |
| --- | --- |
| This document | The document and its parts |
| [`contract-ir/contract-ir.schema.json`](contract-ir/contract-ir.schema.json) | The JSON Schema (draft 2020-12) of format `runic.views.contract-ir/1` |
| [`contract-ir/corpus/`](contract-ir/corpus/index.json) | The IR of every example that commits its generated clients |
| [`contract-ir/fingerprint-example.json`](contract-ir/fingerprint-example.json) | A worked example of the [fingerprint](#fingerprint) |

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

## Document

A document is one JSON object of the [JSON profile](values.md#json-profile):

| Member | Content |
| --- | --- |
| `format` | `runic.views.contract-ir/1` |
| `producer` | `language` (`csharp`, `rust` or `cpp`), `tool` (the program that wrote it), `unit` (the assembly, crate or target name), and `clientLibrary: true` in [library mode](#library-and-application-mode) |
| `clientModules` | In application mode, the client libraries whose clients the unit imports, each owning at least one View: `{unit, specifier}` |
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
| `name` | The public name, such as `Counter` for `CounterViewModel`: letters and digits, starting with a letter, unique in the unit. It prefixes the client's TypeScript names (`CounterState`, `CounterClient`). The producer chooses it ([below](#public-names)). |
| `route` | The route prefix and module name: `name` with a lowercase first letter (`counter`, `counter.ts`) |
| `fingerprint` | The View's [fingerprint](#fingerprint) (`runic.views.fingerprint/1`), a hash of the View's own contract. The emitter writes it into the client's contract string unread. |
| `kinds` | The presentation kinds, one per View contract (`notes`, `notesCompact`); the first is the default |
| `doc` | The View's documentation |
| `developmentOnly` | A View that a trimmed or NativeAOT publish leaves out. Its client is the same in every configuration. |
| `owner` | In application mode, the `unit` of the client library that owns the View's client |
| `memberErrors` | The model reports errors per member (`INotifyDataErrorInfo`): each entry of `members`, of every `kind`, has the state member `{wire}Errors` ([protocol](README.md#nameerrors)), directly after it in the state. The availability members, the execution members and `validation` have none. |
| `validation` | The View has the `validation` state member |
| `uploads` | A command input or settable member takes an `upload`, so the client has `upload()` |
| `sharedClients` | The ids of the shared clients the View implements |
| `members` | The [state members](#state-members), in declaration order |
| `commands` | The [commands](#commands), in declaration order |
| `interactions`, `requests` | The [interactions and view requests](#interactions-and-view-requests) |

### Public names

A producer derives a View's `name` from its source name, and MAY let the source choose another one:

- the .NET producer takes the ViewModel type's name without a trailing `ViewModel` (`CounterViewModel` is `Counter`,
  `Settings` stays `Settings`);
- a Rust producer takes the struct's name without a trailing `ViewModel` or `View` (`CounterView` is `Counter`), and
  its `name` attribute replaces it.

`route` is `name` with a lowercase first letter, and the [fingerprint](#fingerprint) covers it. Two producers that
mean to describe the same View, such as the [conformance test Views](conformance/README.md#test-views), give it the same
name, whatever their source names.

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
| `settable` | The page sets it: the client has `set{Name}`. `declaringType` names the source type that declares the setter. It is informational: the client's documentation names no source type, so that it fits a producer in any language. |
| `checked` | A checked field ([protocol](README.md#checked-writes)): the client has `write{Name}` and a field descriptor, which also sets the field with `set{Name}`. A checked field MUST be `settable`, and the schema requires it. The .NET producer makes every settable value member checked; another producer may have settable members that are not. |
| `constraints` | Of a checked field: the [constraints](#constraints) a page may mirror on its input |
| `fieldPaths` | The [field paths](#field-paths) below a checked object member, or below the rows of a collection with `rowEditor` |
| `text` | Its changes may be text splices (`text-splice/1`) |
| `appendOnlyText` | Append-only text (`defineText`) |
| `collectionKey` | A keyed collection: the `wire` of the row member that is its key |
| `rowEditor` | The page edits the cells of the keyed collection's rows |

### Commands

| Member | Content |
| --- | --- |
| `name`, `wire`, `doc` | As for members. The protocol's `can{Name}` and `is{Name}Executing` state members use `name`. |
| `parameter` | The parameter name of the client's command method; present exactly when the command takes an argument |
| `input` | The argument's value position. A keyed command's input is its key, typed as the key the producer resolves: the type its resolver takes (the .NET `RunicCommandKey` method's parameter), which need not be the type of a collection's key member. The conformance `notes` View's `removeRow` takes the string `"1"`, so its input is `string`. `untyped: true` marks a legacy string argument without a typed input graph. |
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

An interaction has `name`, `wire`, `doc`, `contract` (`{fingerprint}:interaction:{name}`), `input` and `output`. A view
request has the same without `output`, and the contract `{fingerprint}:request:{name}`
([protocol](README.md#interactions), [derived contracts](#derived-contracts)).

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
followed by `#2`, `#3` for further shapes of the same type. A named type without a source type of its own, such as the
object of a Rust enum variant's fields, is named after its owner: its id is `{owner}::{Variant}` (the enum's id, then
the variant's source name), `source` the same, `base` the variant's name, and `qualifiers` the owner's name first, then
the owner's qualifiers. Ids and name hints only name types: the fingerprint leaves them out.

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

`fieldPaths` lists the fields below a checked `object` member, and below the rows of a keyed collection with a row
editor (`field-paths/1`), as `{path, constraints}`. `path` is the list of `wire` names from the member's object (or
the row) down to the field, at most three deep. The paths are in declaration order, depth first: a path whose field is
itself an object is followed by the paths below it. The type of each field is found by following the path through the
named object types.

A field is a member of the object that the producer can change in a copy of the object. A path leaves out:

- below a row, the row's key member (`collectionKey`), which a row editor never changes;
- `absent` members;
- members the producer cannot set in a copy (in .NET, members without a setter that a copy can use, and the members
  of a class it cannot copy exactly), and members the source marks as no field (`[RunicNoField]`).

A member without fields below it has no `fieldPaths`.

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

## Fingerprint

A View's `fingerprint`, format `runic.views.fingerprint/1`, is the **uppercase hexadecimal SHA-256** of the UTF-8 bytes of
the [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) canonical form of the View's [projection](#projection). It
identifies what the View exposes on the wire. A producer derives the View's contracts from it
([below](#derived-contracts)). It computes the fingerprint from the descriptions it writes the IR from: at build time
for the IR, and at run time for its own contracts if it does not embed the values. Every Runic producer writes this
format since 0.24. Before that, the .NET producer wrote one hash over every View of the assembly
(`runic-bridge-contract-v6`).

The fingerprint depends only on the View's wire contract. Producer names (type ids, source names) and documentation do
not change it, so:

- one change to a View changes that View's fingerprint and no other;
- a View has the same fingerprint in every document that describes it, such as a client library's and an application's
  that imports it;
- two producers, in any languages, that describe the same wire contract compute the same fingerprint.

[`fingerprint-example.json`](contract-ir/fingerprint-example.json) is a worked example: a document, the canonical form
of its View's projection, the fingerprint and the derived contracts. The engineering test
`eng/views-contract-ir.test.mjs` computes the fingerprint of the example and of every [corpus](#corpus) View again
from this section alone, without .NET.

### Projection

The projection is the object `{"format": "runic.views.fingerprint/1", "view": V}`. V is the View object of the IR with
these changes; everything else stays as the IR has it, including the order of every array and the optional flags,
which are present only when true:

| Part | Left out | Changed |
| --- | --- | --- |
| The View | `id`, `name`, `fingerprint`, `doc`, `owner`, `sharedClients` | `kinds` sorted by UTF-16 code units: the kinds are a set on the wire, and their order only chooses the client's default |
| A state member | `name`, `doc`, `declaringType`, `constraints`, `defaultKey` | `name` left out of each page reference in `pages`, and `constraints` left out of each entry of `fieldPaths` |
| A command | `doc`, `parameter` | |
| An interaction or view request | `doc`, `contract` | |
| A value position (`value`, `rows`, `input`, `result`, `failure`, `output`) | | Its `type` projected |

A type is projected as follows:

- a `named` type loses `ref` and gets `definition`, the projection of the named type `types[ref]`;
- a `list` gets its `element` projected, and a `map` its `value`;
- every other type is unchanged, `custom` included.

The projection of a named type is its definition without `name` and `doc`:

- an `enum` case loses its `doc`;
- an `object` member loses its `doc` and gets its `type` projected;
- a `union` case gets, in place of the id in `object`, the projection of that named object (`null` stays `null`).

Producers reject recursive types, so the expansion ends.

What stays is the wire contract:

- the route, the kinds, `memberErrors`, `validation`, `uploads` and the `developmentOnly` marks;
- each state member's `wire`, `kind` and value position (direction, value kinds, nullability, and the wire names,
  `optional` and `absent` flags and enum values of the named types it reaches);
- `settable`, `checked`, `text`, `appendOnlyText`, `collectionKey`, `rowEditor` and the field paths;
- the presented Views' ids and kinds, `nullable`, `region` and `pageCanClose`;
- each command's `name` (on the wire in `can{Name}` and `is{Name}Executing`), `wire`, input, result, failure and flags;
- each interaction's and view request's `name` (on the wire in its contract), `wire`, input and output.

What is left out:

- the names that only the client or the source uses: the View's `id`, which the operation contract carries beside the
  fingerprint, the public name, the member names, parameter names, declaring types, and the named types' ids and name
  hints;
- documentation;
- the constraints, which the producer checks anyway;
- the mock's `defaultKey`;
- how units share clients (`owner`, `sharedClients`);
- the contracts, which contain the fingerprint.

### Canonical form

The canonical form is RFC 8785. The projection holds only objects, arrays, strings, booleans and `null`. It has no
numbers, because enum values are decimal text. So the canonical form is the JSON text with:

- no whitespace;
- the members of each object sorted by name, comparing UTF-16 code units;
- each string written as ECMAScript's `JSON.stringify` writes it: `"` and `\` escaped with a backslash; U+0008,
  U+0009, U+000A, U+000C and U+000D as `\b`, `\t`, `\n`, `\f` and `\r`; other code points below U+0020 as `\u00xx`
  with lowercase hexadecimal digits; every other character as itself.

The IR's strings are valid Unicode. In JavaScript, the canonical form is `JSON.stringify` of the projection with each
object's members sorted. In Rust or C++, any RFC 8785 writer writes it.

### Derived contracts

A producer derives these contracts from the View's `id`, the fingerprint and the IR `name`s:

| Contract | Value | Used by |
| --- | --- | --- |
| The View's contract | `{id}:{fingerprint}` | Operation requests ([identity](README.md#identity)), the `open` reply with `view-contract/1`, the generated client's contract |
| An interaction's | `{fingerprint}:interaction:{name}` | [Interactions](README.md#interactions) |
| A view request's | `{fingerprint}:request:{name}` | [View requests](README.md#view-requests) |

The .NET generator writes them into the generated Bridge. A Rust or C++ producer can compute them when it starts,
from the same registry that writes its IR, with a JCS writer and SHA-256.

A change to the projection is a new format version, `runic.views.fingerprint/2`. It changes every fingerprint once,
and a page that recovers an operation started before the change gets `unknown`
([start](README.md#start): the other requests find no operation under that contract).

## Corpus

[`contract-ir/corpus/`](contract-ir/corpus/index.json) holds the IR of each example whose generated clients the
repository commits. `index.json` lists for each document the example project that the IR was written from and the
directory of its committed generated clients, which are the expected output of an emitter for that IR. The sources are
relative to the corpus directory.

The engineering test `eng/views-contract-ir.test.mjs` (`bun run test eng/views-contract-ir.test.mjs`, and in the
engineering job) checks every corpus document against the schema, and that the contract of each of its Views is the
one the example's committed clients carry, so a change to an example's contract that leaves the corpus behind fails.
`packages/web/views/test/codegen.test.ts` checks that the emitter writes, from each corpus document, the example's
committed clients byte for byte.
To update the corpus, build the examples (`bun run build` builds those of `RunicSdk.Core.slnx`), then run the test with
`RUNIC_UPDATE_CONTRACT_IR_CORPUS=1`, which copies each example's `contract.ir.json` from its `obj` directory.
