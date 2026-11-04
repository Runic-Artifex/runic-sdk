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
| Features | The producer lists `text-splice/1`, `presentation-epoch/1`, `presented/1`, `view-requests/1`, `region-collections/1`, `query-collections/1`, `blobs/1`, `validation-pending/1` and `translation-text/1`. A connection uses a feature when its `hello` lists it. |
| Window locale | `IRunicWindowLocale.Locale` (a scoped service of a Window that `OpenWindowAsync` opened) or `WindowContentSession.Locale`, settable from any thread, with `PropertyChanged` and `LocaleChanged`. A value set before the window opened is pushed to the first page. Logged as event 1170 `WindowLocaleChanged` (debug); a validation text over 16 KiB is left out and logged as event 1171 `ValidationTextTooLarge` (warning). `RunicTextReference` accepts any integer version from 1 and `TranslationReference.Parse` only the versions its package reads. |
| Validation text | A `RunicValidationMessage` with `Text`, a `RunicValidationResult` (for DataAnnotations and CommunityToolkit `ObservableValidator`) or any entry of `GetErrors` that implements `IRunicTextReferenceSource`. For ReactiveUI.Validation, a component or its `IValidationText` that implements `IRunicTextReferenceSource`; the message of the member's `GetErrors(member)` then takes the text of the context's entry with the same member and message. |
| Query collections | A `RunicQueryCollection<TRow, TKey>` property is a query collection: its state member is written by `BridgeQueryWire.WriteState`, its rows by the generated row codec (`PropertyDescriptor.QueryRowWriter`), and `query-rows`, `query-set` and `query-index` go to the collection. `RunicQuery` is the query; a source's `Validate` rejects a query with `ArgumentException` (`invalid-input`). The collection loads in pages, caches at most `MaximumCachedPages`, and raises its property changes in its `ModelContext`. Loads, searches and query changes that fail log events 1190-1192. |
| Blobs and uploads | `RunicBlob` is a `blob` (`FromBytes`, `FromFile`, `FromStream`); `RunicUpload` is an `upload`, which the ViewModel claims with `OpenReadAsync`, `SaveAsAsync` or `Discard`. `RunicBlob.Release()` ends a blob's URLs in every window; a window keeps at most 1,024 blobs of command results and interaction inputs and releases the least recently written ones beyond that (event 1208, once per request with their number); state members and `[RunicCollection]` rows (held per row key) never count. An upload body that sends nothing for 30 seconds is dropped (`408`). Uploads are stored in a new 0700 folder per window below a base no other user can change, and read through the file handle the host created. A state property that holds a `RunicBlob` is read-only for the page whatever its setter; a `RunicBlob` command input is RUNICBRIDGE065. Limits: `WindowContentSessionOptions.MaximumUploadBytes` (256 MiB) and `MaximumWindowUploadBytes` (1 GiB), also on `RunicWindowOptions` and the host options. The Desktop surface serves both through Kestrel or the native server; CS-WebUI from an `HttpListener` per window. Log events 1200–1208. |
| Requests before the `hello` | The dispatcher holds them on the window's `TimeProvider` (`WindowContentSessionOptions.TimeProvider`), so tests advance a fake clock past the 5 seconds. Runic Desktop admits a connection's requests in order and handles `hello` at once, so its requests are never held in practice; CS-WebUI can deliver an `open` before the `hello` it followed. |
| Text members | Generated `PropertyDescriptor`s set `TextMember` for `string` state properties without `[RunicCodec]` other than `[RunicAppendOnlyText]` fields, and `TextSetter` for settable ones. Text members are compared as strings at publication, not by their encoding. A `set` with a splice, or a `set` whose value is a string, calls `TextSetter`. A checked field is a text member when its state property is; a `write` with a splice runs the checked field's setter with the rebuilt value. |
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
| Region collections | `[RunicRegionCollection(typeof(TContent))]` on a get-only `NavigationSelection` property. `regions` are `Regions` in order; a region's `key` is the decimal id of the `NavigationRegion` in its navigator, which never repeats; a selection's regions share one navigator, so keys are unique in the collection, and its `content` is `Current`, presented with `PresentItem`. `selected` is `SelectedIndex`, or `null` for -1. A `region` request runs `SelectAsync` or `RemoveAsync`; `outcome` is the result's `NavigationOutcome` with enum members by name. `PageCanClose = false` makes `close` `unavailable`. A page close of a region whose page close is pending joins its task (`BridgeRegionCollections`, keyed by region). A guarded window close removes a collection's regions one by one with `RemoveAsync`. The contract fingerprint gets a `region-collection:` line only for such properties. Log events 1210 `BridgeRegionRequestFailed` and 1211 `BridgeRegionRequestUnresolved`. |
| Presentations | Each `open` presentation gets a fresh View instance. `WindowContentSession` pushes the first state of content whose presentations resume, whether or not a connection uses a feature. |
| Presentation epochs | A route's epoch is the revision of the state that began it, so its epochs increase and never repeat. `RunicPresentation.Renew(viewModel)` renews every route that presents the ViewModel now, in every window; it can be called from any thread, usually from a command body, and does nothing for a ViewModel that no route presents. `[RunicPresentationScoped]` (`RunicPresentationScopedAttribute`) on a command property or a CommunityToolkit `[RelayCommand]` method marks the command scoped: the generator lists it in the generated TypeScript's `scopedCommands` and adds a `scoped:{member}` line to the contract fingerprint. `RunicPresentation` is static, as `RunicOperationProgress` is, so a ViewModel renews itself without its window. |
| Painted states | `RunicPresentation.WaitForPresentedAsync(viewModel, timeout, cancellationToken)` returns a `ValueTask<bool>`: `true` once a page reported one of the routes that present the ViewModel at or after the revision the wait took, `false` at the timeout. |
| Operation contract | `view` is the ViewModel's full type name. `fingerprint` is a SHA-256 over lines that describe every model and View of the ViewModel's assembly (type names, members, wire names, command inputs, results, keys, failures, collections, content slots, validation and scoped commands), prefixed `runic-bridge-contract-v5` for protocol 4. Any View change in the assembly changes every fingerprint of it. A View without scoped commands keeps its fingerprint. |
| Operation identity | Input digests are SHA-256 over the canonical form; they never leave the process. |
| Progress | `RunicOperationProgress.Report(double fraction)`, static and ambient through the operation's flow, does nothing outside an operation. Command signatures do not change. |
| Window close | `CanCloseAsync` for a user's close and `RequestCloseAsync` are the window's close decision. The host keeps the window's DI scope until its operations and awaited commands end. `CloseAsync`, `RequestCloseAsync` once it decided, `DisposeAsync` and host shutdown push `closing`. `CloseAsync(timeout)` runs `WindowContentSession.DrainAsync`, which every host's close runs, and waits there for `closing-reply`; `RunicWindowCloseResult.PagesAcknowledged` reports the result (`true` when no connection uses `presented/1`). `TimeSpan.Zero` and `DisposeAsync` do not wait; a later drain, such as a host's drain without a timeout after it closed, reuses the first result. |
| Interaction output errors | The waiting interaction fails with `RunicInteractionOutputException`. |
| Interaction scopes | The generated Bridge enters the command invocation (`RunicInteractionInvocation`) with the transport's keys. A `RunicWindow`'s close decision, and a command of the window (`IRunicMenuCommandSource`) that the native menu bar or a host shortcut runs, run in the window scope; an item the in-page menu bar invokes runs in a command invocation with the invoking connection's keys. Application code enters the window scope with `RunicInteractionInvocation.EnterWindow(viewModel)` or `TryEnterWindow`, which find the one window that presents the ViewModel and enter no scope when none or several do, or when a command invocation of that window is current. Application code cannot enter a command invocation. |
| Interactions | A ReactiveUI `Interaction<TInput, TOutput>` (`Runic.Application.Views.ReactiveUI`) or a flavor-neutral `ViewQuestion<TInput, TOutput>` whose `AskAsync` asks the page and otherwise runs its .NET fallback, or fails with `RunicInteractionUnhandledException`. |
| View requests | A get-only `ViewRequest<T>` property of a ViewModel, in either MVVM flavor; `Raise(input)` sends it without waiting, and its constructor takes the `ViewRequestDelivery` (`Drop`, `NextMounted`, `Broadcast`). The request's contract is `{fingerprint}:request:{Property}`. Dropped and kept requests are logged at Debug (events 1120 to 1122). |
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
| `text-reference` | `Runic.Translations.TranslationReference`, which the generator recognizes by its name and assembly, so the Views packages do not reference Runic Translations: the generated code calls `TranslationReference.Parse` and `TranslationReferenceJsonContext`. `RunicTextReference` (Runic.Application.Views) holds the JSON of a reference for Views code, such as `RunicValidationMessage.Text`. |

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
| Edits | Generated setters send `set` with the next edit number of the member and apply the [overlay](README.md#client-overlay) to `snapshot`. A setter of a text member (`connectView`'s `texts`) sends a splice when the [client rule](README.md#text-splices-in-set) allows one, and sends the value again on `splice-base`. `ViewConnection.write` of a checked text member (in `texts` and `checkedFields`) sends a splice when the [client rule](README.md#text-splices-in-write) allows one, and sends the write in full with the same `requestId` on `splice-base`. `createTextFieldController` and the framework text field bindings write through the setter, so text fields send splices. |
| Features | The runtime lists `text-splice/1`, `presentation-epoch/1`, `presented/1`, `view-requests/1`, `region-collections/1`, `query-collections/1`, `blobs/1`, `validation-pending/1` and `translation-text/1` in `hello`. `SharedRuntime.features` holds the features in use on the current connection. |
| Translated text | `textReference` in `@runic-artifex/views/generated/wire` decodes a `text-reference` as a `TextReference`. `configureTranslatedText` connects the page's Translations runtime (`resolve`, `getLocale`, `setLocale`, `subscribe`); the window's `locale` push calls its `setLocale`. `resolveText(value, fallback)` formats a reference, or shows its `fallbackText`, `fallback` and key; `windowLocale()`, `onWindowLocale` and `subscribeText` observe changes. `fieldErrors` and field controllers show validation messages with a `text` in the window's language. Adapters: React `useText`, Vue `useText`, Svelte `useText`, Angular `RunicTextPipe` (`runicText`) and `injectText`, views-effect `texts` and `textChanges`. |
| Region collections | `RegionCollection<R>` with `RegionSlot<R>` items; generated `select{Name}(key)` and `close{Name}(key)` call `ViewConnection.region(member, action, key)` and resolve with a `RegionOutcome` once the route reached the reply's revision. They wait for the current connection's `hello` (`SharedRuntime.whenGreeted`) and then, without the feature in use, reject with `failed` and send nothing. `regionEntries` and `selectedRegion` read a collection; the adapters' `RegionOutlets` render it. |
| Query collections | A generated client's `queries.{name}` is a `QueryCollection<TRow>` from `ViewConnection.queryCollection`, per presentation; its state member decodes with `decodeQueryCollectionState`. `createQueryViewportController` lays it out with variable heights; the framework packages wrap it as `useQueryCollection` and `injectQueryCollection`. |
| Blobs and uploads | A `blob` decodes to `RunicBlob` (`{url, type, size, name}`) and an `upload` to `BlobRef`; generated types name them as `import("@runic-artifex/views").RunicBlob`. `upload(file, {signal, onProgress})` from `@runic-artifex/views`, and a generated client's `upload` when its contract takes one, read the connection's grant (`SharedRuntime.blobGrant`) and send the file with `XMLHttpRequest`, for upload progress, or `fetch`; a host bridge with an `upload` member (the mock Bridge) sends it itself. `mockBlob()` of `@runic-artifex/views/mock` makes a blob whose URL is an object URL. |
| Presentation epochs | `ViewConnectOptions.scopedCommands`, which generated clients fill in, names the scoped commands; `command` and `commandOutcome` send `epoch` for them. A superseded call resolves as a successful one: `command` with the exposed state, `commandOutcome` with `bridgeSuccess(state)`. `createCommandController`, `useCommand`, `injectCommand` and the Vue and Svelte equivalents therefore see a resolved run. |
| Painted states | The runtime answers `present` after the route's notifications were delivered and one `requestAnimationFrame` passed, or after the notification queue's timer or microtask fallback without animation frames. |
| Window close | On `closing`, the runtime delivers pending notifications, waits one animation frame, sends `closing-reply` and reports the `BridgeConnectionState` `"closed"`, which stays when the connection drops; a new connection reports `"connected"`. The framework connection-state hooks pass `"closed"` through. |
| Requests with the `hello` | `request()` sends a request right after the connection's `hello` without waiting for its reply, and sends it again once if it is answered `no-hello` while that `hello` succeeds. |
| Mock bridge | `createMockBridge` implements features, held requests, `text-splice/1`, `presentation-epoch/1`, `presented/1`, `view-requests/1`, `region-collections/1` (`MockViewDefinition.regions`) `query-collections/1` (`MockViewDefinition.queries`: rows it filters, sorts and serves with latency) and `translation-text/1` (`setWindowLocale`); a View's text members (`MockViewDefinition.texts`) and scoped commands are filled in by generated typed mocks. Its page-test API renews a route, asks for `presented` and closes the window. |
| Operations | `BridgeOperation` with `status()`, `wait()`, `outcome()`, `cancel()`, `subscribe()` and, for streams, `stream()`. `wait({ timeout, signal })` is client-only. |
| Interactions | Handlers registered with `client.interactions.<name>.handle(handler)` or `options.handlers`, sent with `open` and `interactions`. A `ViewQuestion` is an interaction on the page. |
| View requests | Handlers registered with `client.requests.<name>.handle((input, { signal }) => void)`, which returns the function that unregisters it, sent with `view-requests`. The handler runs after the route's notifications were delivered and one animation frame passed, as for `present`; `signal` aborts when the handler is replaced or unregistered, the View is disposed or the connection is lost. |

## Intentional differences

| Shared semantic | .NET | TypeScript | Reason |
| --- | --- | --- | --- |
| Enum values and union case tags | C# member names; declared `[RunicUnionCase]` names | the same strings | See [Names](#names) (D051.4). |
| Cancellation | `CancellationToken` | `AbortSignal` and `cancel()` | Each language's idiom; neither crosses the wire. |
| Failures | Exceptions classified by type | `BridgeError` values and declared failure values | The wire carries `error.kind`, `reason` and `failure`, not exception types. |
| 64-bit integers | `long`, `ulong` | strings (`int64String`) or `bigint` | JavaScript numbers are doubles. |
| Operation progress | an ambient `Report(double)` | `progress` on statuses and controllers | .NET command signatures stay unchanged. |
