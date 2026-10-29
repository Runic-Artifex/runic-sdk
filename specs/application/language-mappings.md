# Views protocol 4: language mappings

This document belongs to the [Runic Application Views protocol](README.md), version 4. The protocol is normative and
language-neutral. This document is informative about how Runic's implementations realize it: the generated .NET
producer of `Runic.Application.Views` and the TypeScript client `@runic-artifex/views`. It is normative only where it
states an intentional difference that the protocol leaves to the implementation. Package guides document the public
APIs; names here may lag behind them, but the mapped behaviour must not.

Another implementation, for example in Rust or C++, maps the same rules to its own idioms: ownership, futures or
coroutines, result types and its own serialization. Runic does not ship or support such an implementation.

## .NET producer

| Protocol | .NET mapping |
| --- | --- |
| Producer | A window session (`WindowContentSession`) with the generated `ViewModelBridge` subclass of each root and content View. |
| Implementation identity | `implementation.name` is `Runic.Application.Views`; `version` is the package's version without build metadata. |
| Transport | `IBridgeTransport.Serve(IBridgeRequestHandler)` delivers the requests of a window's connections to the window, which answers each through `IBridgeRequestHandler.HandleAsync(BridgeRequest)`; `BridgeRequest` carries the UTF-8 message and the transport's `ClientKey` and `ConnectionKey`. `IBridgeTransport.BroadcastAsync` sends a push to every connection; a transport that can address one connection also implements `IBridgeConnectionTransport.SendAsync`. The transport reports closed connections with `IBridgeRequestHandler.ConnectionClosed`. Runic Desktop, WPF (`DesktopBridgeTransport`), CS-WebUI (`CreateBridgeSession`) and the in-memory test transport of `Runic.Application.Testing` implement it. |
| Host bridge | Runic Desktop: requests on the capability `__runicViews`, pushes as raw data to `__runicViewsPush`, connection transitions from the served bridge script (`runic-desktop-views.js`). CS-WebUI: requests on the binding `__runicViews`, broadcasts with `SendRaw` to `__runicViewsPush`, the `receive` loop of `runic-cswebui.js`. |
| Admission order | Runic Desktop admits the requests of one connection in arrival order. CS-WebUI runs each call on its own native thread, so it does not keep the order; edits carry their numbers. |
| Trusted identities | The transport's `ClientKey` and `ConnectionKey`. CS-WebUI uses the WebUI client id and `clientId:connectionId`; Runic Desktop uses its connection id for both, so a reconnect has new keys. |
| Mailbox (`receive`) | CS-WebUI, because WebUI cannot send to one client outside an event. A connection's mailbox keeps at most 1,024 pushes and 4 MiB; beyond that it drops its oldest pushes and logs event 1023 `BridgeMailboxOverflowed` once per connection. A pending `receive` holds one native WebUI thread, and WebUI runs every call on its own thread, so it does not hold up the connection's other requests. |
| Handshake log events | Event 1018 `BridgeProtocolMismatch` (warning, once per connection) for a `hello` of another protocol; event 1019 `BridgeRequestBeforeHello` (debug, once per connection) for a request answered `no-hello`. |
| Features | The producer lists `text-splice/1`. A connection uses it when its `hello` lists it. |
| Requests before the `hello` | The dispatcher holds them on the window's `TimeProvider` (`WindowContentSessionOptions.TimeProvider`), so tests advance a fake clock past the 5 seconds. Runic Desktop admits a connection's requests in order and handles `hello` at once, so its requests are never held in practice; CS-WebUI can deliver an `open` before the `hello` it followed. |
| Text members | Generated `PropertyDescriptor`s set `TextMember` for `string` state properties without `[RunicCodec]` other than `[RunicAppendOnlyText]` fields, and `TextSetter` for settable ones. Text members are compared as strings at publication, not by their encoding. A `set` with a splice, or a `set` whose value is a string, calls `TextSetter`. |
| Splices in broadcast frames | Every connection receives one encoding of a frame, so the producer computes splices while every connection that completed its `hello` uses `text-splice/1`. A frame with splices keeps a fallback writer; when a connection without the feature completed its `hello` before the frame is handed to the transport, the frame goes out with a `set` of each spliced member instead. |
| Route names | A root route is the root ViewModel's short type name, camel-cased (`counter`). A content route is `content` and the content's page-reference id. |
| Member wire names | The C# property, command or field name, camel-cased, or its `[RunicAlias]`. `[RunicIgnore]` leaves a member out. |
| Failed publications | A state writer that throws (including `ObjectDisposedException` from a getter of a disposed object) does not throw to the code that raised the change. The failure is traced, nothing is published, and the next change tries again. A push on its way when the window disposes its transport is dropped. |
| Failure detail | `WindowContentSessionOptions.IncludeFailureDetail` (also a host option); on by default in the Development environment. `type` is the exception's full type name and `stack` the exception's `ToString()`. |
| Input limit | `WindowContentSessionOptions.MaximumInputBytes`, `RunicWindowOptions.MaximumInputBytes` and the host options' default. |
| `invalid-input` | A value that does not decode, or a setter or command that throws `ArgumentException`, `FormatException` or `JsonException` (a setter also `OverflowException`). |
| `unavailable` | The command's `ICommand.CanExecute` returned false. |
| Key commands | `[RunicCommandKey]`: the generated bridge decodes the key and resolves it with the ViewModel's resolver before `CanExecute` and `Execute`. |
| Declared failures | `[RunicFailure(typeof(T))]` on a command, thrown as `RunicFailureException` with a `T` value. Any other exception, or a value that is not a `T`, is `failed`. Classification is by exception type, so a declared failure thrown while cancellation is requested stays `domain-failed`. |
| Asynchronous commands | Commands whose execution returns a task, such as ReactiveUI's and the Toolkit's asynchronous commands; `is{Name}Executing` is their execution state. The `can{Name}` member of a command whose `CanExecute` reads a boolean property of the same name is that property (#305). |
| Availability capacity | 64 awaited command calls per window. |
| Checked fields | Generated checked properties with a per-field registry. Versions advance on each observed change, including external changes that the owner reports. |
| Optional values | A `JsonElement` member that holds no value (`JsonValueKind.Undefined`) is left out of objects. A missing member is filled in when it is nullable or its constructor parameter has a default. |
| Content | `Present`, `PresentItem` and `Expose` create content references; `Forget` drops an identity. |
| Presentations | Each `open` presentation gets a fresh View instance. |
| Operation contract | `view` is the ViewModel's full type name. `fingerprint` is a SHA-256 over lines that describe every model and View of the ViewModel's assembly (type names, members, wire names, command inputs, results, keys, failures, collections, content slots and validation), prefixed `runic-bridge-contract-v5` for protocol 4. Any View change in the assembly changes every fingerprint of it. |
| Operation identity | Input digests are SHA-256 over the canonical form; they never leave the process. |
| Progress | `RunicOperationProgress.Report(double fraction)`, static and ambient through the operation's flow, does nothing outside an operation. Command signatures do not change. |
| Window close | `CanCloseAsync` for a user's close and `RequestCloseAsync` are the window's close decision. The host keeps the window's DI scope until its operations and awaited commands end. |
| Interaction output errors | The waiting interaction fails with `RunicInteractionOutputException`. |
| Invalid keys | Logged as event 1012 `BridgeCollectionKeysRejected` (model, field, key and route, with the exception). |
| Validation | A ViewModel whose model or a type of its state graph implements `INotifyDataErrorInfo` or `IValidatableViewModel` has [`validation`](README.md#validation); a ViewModel that implements it also has `{name}Errors`. Errors come from `GetErrors(null)` for the object and `GetErrors(member)` for each member; a ViewModel or DTO that implements ReactiveUI.Validation's `IValidatableViewModel` (`RunicArtifex.ReactiveUI.Validation` or its `.Reactive` flavor), such as a `ReactiveValidationObject`, is read through its `ValidationContext` instead of `GetErrors(null)` (D033), and has `validation` also without `INotifyDataErrorInfo` (D053.3). A `RunicValidationMessage` supplies `code`, `severity` and relative member paths, which may index lists and dictionaries (`Items[0].Name`, `Lookup[primary]`, `Groups[0][1]`). `ErrorsChanged` and the context's `ValidationStatusChange` request a publication, for the ViewModel and every object of its graph. A checked write's `applied.validation` is the member's `GetErrors(member)`. |

### Names

- Enum values are the C# member names, or `[RunicAlias]`, as `System.Text.Json` writes them. They are not
  camel-cased.
- Union case tags (`$case`) are the names that each case declares with `[RunicUnionCase(name)]` (at most 128
  printable characters), conventionally camelCase (`titleTaken`).

The difference is intentional (D051.4): enum values are data that also appears in .NET logs, storage and other JSON
that the application writes, while `$case` tags are TypeScript discriminants. The protocol itself does not transform
either; it carries the names the contract defines ([values](values.md#names)).

### Values

| Wire type | .NET type |
| --- | --- |
| `boolean` | `bool` |
| `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32` | `sbyte`, `byte`, `short`, `ushort`, `int`, `uint` |
| `float32`, `float64` | `float`, `double` |
| `int64`, `uint64`, `big-integer` | `long`, `ulong`, `BigInteger` |
| `decimal` | `decimal` (its scale is the number of digits after the point) |
| `string` | `string` |
| `uuid` | `Guid` (`D` format) |
| `local-date` | `DateOnly` |
| `local-time` | `TimeOnly` |
| `date-time` | `DateTime`: no offset is `DateTimeKind.Unspecified`, `Z` is `Utc`, and an offset is `Local`, converted to the machine's local time when read. A `Local` value is written with the machine's offset. |
| `offset-date-time` | `DateTimeOffset` |
| `duration` | `TimeSpan` (100-ns ticks) |
| `enum` | an `enum` |
| `json` | `JsonElement`, `JsonNode` |
| array, map | arrays, lists, `Dictionary<string, T>`, `IDictionary<string, T>`, `IReadOnlyDictionary<string, T>` |
| object, union | records and classes; `[RunicUnion]` hierarchies |
| keyed collection | `[RunicCollection(keyProperty)]` on a read-only collection of rows, such as `ReadOnlyObservableCollection<Row>`; the key property is a nonnullable `string`, `Guid` or `int` |
| text field | `[RunicAppendOnlyText]` on a read-only `string` property |
| text member | any other `string` state property without `[RunicCodec]` |

### Publication units and change detection

- Collection changes come from `INotifyCollectionChanged`, row changes from rows that implement
  `INotifyPropertyChanged`. A `Reset`, a notification without a starting index, a `Replace` with different old and
  new counts, a row reached through more than one path, and a change of an object nested inside a row are published
  as a `set` of the field.
- A publication unit is one notification, or a `BridgeSnapshotBatch`, which publishes once when the outermost batch
  ends. The producer may also publish once per model turn.
- Members other than collections and text fields are compared by encoding at publication, so `PropertyChanged` names
  are not needed for them.
- ViewModels with validation (`INotifyDataErrorInfo` or `IValidatableViewModel`, on the ViewModel or in its state
  graph) publish frames like any other, collection and text changes included; a frame is one unit, so validation stays
  atomic with the changes that caused it.
- DynamicData's `Bind` and `SortAndBind` raise one `CollectionChanged` per row they change. `BatchBridgeSnapshots(model)`
  on the changeset stream makes each changeset one unit. Bind on the window's model context
  (`ObserveOn(ReactiveSchedulers.For(context))`): a collection that changes outside a model turn is logged once per
  field as event 1025 `BridgeCollectionChangedOutsideModelTurn`, because the change waits for a turn and can race the
  window. A change notification after the window's model context closed counts as one for a released Bridge and does
  not throw into `Bind`.
- The delivery queue's 1 MiB bound is counted in UTF-8 bytes; .NET holds a queued push in memory as UTF-8.

### Window turns

A window's bridges share one model context. A request runs its work inline when the context reports `IsExecuting`,
and otherwise waits until `InvokeAsync` has run it. Without a model context, the work runs on a thread-pool thread
while holding the model gate that also serializes change notifications. A requested state is captured by the
delivery in a model turn, so state writers and the getters they call must not assume the thread that raised
`PropertyChanged`. The reply order wait (`BridgeReplyOrder`) holds a reply until the pushes requested before it were
handed to the transport. Teardown runs inline when an application-owned context was disposed first.

## TypeScript client

| Protocol | TypeScript mapping |
| --- | --- |
| Client | The shared page runtime (`runic.views.generated-client-runtime`) and the generated clients, which call `connectView` from `@runic-artifex/views/generated`. |
| Client identity | `client.name` is `@runic-artifex/views`; `version` is the package version. |
| Host bridge | `RunicBridgeClient` (`globalThis.__runicBridge`). |
| Errors | `BridgeError` with `kind`, `reason`, `detail` (`BridgeFailureDetail`, whose `type` is optional) and, for declared failures, the decoded failure. `disconnected` and `timeout` are client-only kinds. |
| Diagnostics | `onBridgeDiagnostic`, codes `protocol`, `version`, `revision-timeout` and the error kinds. |
| Values | Decoders in `@runic-artifex/views/generated/wire`: `int64String`, `bigint`, `decimal` and `duration` keep strings or bigints; `uuid`, `localDate`, `localTime`, `dateTime`, `offsetDateTime` keep strings in their normal form; `json` keeps the value. |
| Canonical form | Not needed in the client. Where the mock bridge compares values, it writes each scalar and member name with `JSON.stringify` and sorts the members of each object, which is the canonical form in JavaScript. Sorting the keys of a plain object and calling `JSON.stringify` on it is not: JavaScript enumerates integer-like keys (`"1"`, `"10"`) first. |
| Edits | Generated setters send `set` with the next edit number of the member and apply the [overlay](README.md#client-overlay) to `snapshot`. A setter of a text member (`connectView`'s `texts`) sends a splice when the [client rule](README.md#text-splices-in-set) allows one, and sends the value again on `splice-base`. `createTextFieldController` and the framework text field bindings write through the setter, so text fields send splices. |
| Features | The runtime lists `text-splice/1` in `hello`. `SharedRuntime.features` holds the features in use on the current connection. |
| Requests with the `hello` | `request()` sends a request right after the connection's `hello` without waiting for its reply, and sends it again once if it is answered `no-hello` while that `hello` succeeds. |
| Mock bridge | `createMockBridge` implements features, held requests and `text-splice/1`; a View's text members are `MockViewDefinition.texts`, which generated typed mocks fill in. |
| Operations | `BridgeOperation` with `status()`, `wait()`, `outcome()`, `cancel()`, `subscribe()` and, for streams, `stream()`. `wait({ timeout, signal })` is client-only. |
| Interactions | Handlers registered through generated `on{Name}` functions, sent with `open` and `interactions`. |

## Intentional differences

| Shared semantic | .NET | TypeScript | Reason |
| --- | --- | --- | --- |
| Enum values and union case tags | C# member names; declared `[RunicUnionCase]` names | the same strings | See [Names](#names) (D051.4). |
| Cancellation | `CancellationToken` | `AbortSignal` and `cancel()` | Each language's idiom; neither crosses the wire. |
| Failures | Exceptions classified by type | `BridgeError` values and declared failure values | The wire carries `error.kind`, `reason` and `failure`, not exception types. |
| 64-bit integers | `long`, `ulong` | strings (`int64String`) or `bigint` | JavaScript numbers are doubles. |
| Operation progress | an ambient `Report(double)` | `progress` on statuses and controllers | .NET command signatures stay unchanged. |
