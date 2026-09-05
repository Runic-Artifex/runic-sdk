# Migrating a customer editor from MVVM to Runic

This maintained reference replaces a CommunityToolkit viewmodel with Runic
application commands and a React presentation. The Runic application has no
CommunityToolkit dependency. There is no viewmodel adapter or remote property bag.

## Run it

From the SDK root, after `bun run bootstrap`:

```sh
bun run example:customers
# Embedded WebView with native close confirmation (Windows/Linux):
bun run example:customers --native
# Serve the same application without opening a native window:
bun run example:customers --serve
```

The default builds the SDK and application, then opens an installed browser through
Runic Desktop. `--native` selects the embedded WebView and enables close confirmation.
`--serve` prints a local URL for the real C# bridge. Keep that process running
while using the URL. The page does not have a mock backend or duplicate C# rules.
For faster subsequent starts use `dotnet run --project
examples/customer-migration/Host/CustomerDesktop.csproj --no-build`.

The native reference requires WebView2 on Windows or GTK 3/WebKitGTK on Linux.
The current asynchronous Application host does not provide the main-thread runner
required by AppKit; use browser/serve mode for this example on macOS. The lower-level
Desktop API has an AppKit close hook and a dedicated main-thread native smoke test.

On Windows, the original WPF implementation is runnable with:

```sh
dotnet run --project examples/customer-migration/Wpf/CustomerWpf.csproj
```

The portable viewmodel is also usable as a MAUI migration reference, but this
example does not include a MAUI host or certify mobile support. WPF UI execution
requires Windows; cross-compilation alone does not verify native behavior.

Both applications start with three fictional customers. Runic saves to
`LocalApplicationData/Runic/CustomerMigration/customers.json`; WPF uses
`before-customers.json` in the same directory. Set `RUNIC_CUSTOMERS_FILE` to choose
a different file. Do not run both applications against the same file: the sample
repository serializes writes within one process, not across processes.

## Try the migration behavior

1. Search by customer name, email, or company, then choose a customer.
2. Edit a name to one character and leave the field. The Runic frontend asks C#
   for validation; it does not copy the domain rule into TypeScript.
3. Correct it and save. A complete draft is submitted once, including the record
   version. The backend validates it again before persistence.
4. Edit again, start saving, and cancel. The draft remains available and the stored
   customer remains unchanged when cancellation wins before the commit point.
5. Navigate while dirty. The application asks whether to discard those changes.
6. Reconnect while dirty. The current in-memory draft and its original version are
   preserved. Reconnection does not replay a save. A full page reload discards
   frontend drafts; it recovers committed data and session operation state.
7. Import `contact.example.json`, review the changes, and save. Only editable fields
   are imported; the file cannot select a different customer or record version.
8. Give another customer the same email. The backend rejects the duplicate and
   returns a field error. A stale record version similarly prevents an overwrite.

9. In `--native` mode, edit a field and click the OS close button. Keep editing or
   press Escape to retain the draft; Discard and close approves shutdown. While an
   operation is active, close is denied until it finishes or is cancelled.

Save deliberately includes three 300 ms staging delays so progress/cancellation
are visible. Those delays are sample behavior, not a Runic requirement.

## Where the responsibilities moved

| Responsibility | Original implementation | Runic implementation |
| --- | --- | --- |
| Rules, uniqueness, optimistic concurrency, atomic persistence | `Domain/Customers.cs` | Same domain code |
| Observable editing properties and computed dirty state | `Before/CustomerEditorViewModel.cs` | `Host/Frontend/src/editor-state.ts` and React form state |
| `[RelayCommand]`, command availability, async lifecycle | `Before/CustomerEditorViewModel.cs` | `After/CustomerFeature.cs` named commands and owned operations |
| `ObservableValidator` attribute validation | Viewmodel properties using shared validators | `ValidateCustomer`/`SaveCustomer`, typed field issues |
| Search and selection | Viewmodel and WPF bindings | Local frontend state |
| UI notifications | Property/collection notifications | Generated snapshot and event contract |
| File selection and unsaved prompts | WPF `OpenFileDialog`/`MessageBox` | WebView file input and accessible HTML dialog |
| Platform host | `Wpf/` | `Host/Program.cs`, Runic Desktop and embedded assets |

`After/` is a C# application module. `Host/` owns the contract root and platform
composition. `Tests/` hosts the same module without a UI and compares its business
results with the original viewmodel. Shared C# records/rules contain no Runic or
CommunityToolkit dependency. DataAnnotations attributes in the domain are ordinary
.NET validation helpers, not MVVM infrastructure.

Form drafts stay local. Accepted customer data stays in C#. Save submits all fields
together, so it cannot overtake outstanding per-property updates. Validation replies
are associated with an edit sequence; late replies cannot overwrite newer edits.
Projection generations prevent an older start receipt from replacing newer progress
or a terminal operation event. These are explicit sample mechanics awaiting a
reusable SDK design, rather than claims that Runic already has a forms package.

## Verification

```sh
dotnet run --project examples/customer-migration/Tests/CustomerMigration.Tests.csproj
bun run --cwd examples/customer-migration/Host/Frontend test
# Install Chromium once, then run full browser acceptance:
bun run --cwd examples/customer-migration/Host/Frontend browser:install
bun run verify:customers
```

`verify:customers` builds the application, starts its real host with an isolated
temporary data file, and exercises search, validation, dirty navigation, saving,
cancellation, reconnect, import, duplicate-email errors, close decisions, and a narrow layout.
Set `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` to use an existing Chromium installation.
No application data or runtime mocks are injected into the frontend.

The root build includes the Runic host; root tests include the managed comparison,
frontend state tests, and generated-contract checks. Linux CI also runs live browser
acceptance. Windows CI compiles the original WPF host. Native UI/accessibility
certification is still separate from these automated checks.

## Deliberate limits and next work

- HTML file selection demonstrates an OS picker through the WebView. It is not a
  Runic native file-dialog API, retained file permission, or arbitrary path access.
- `--native` uses `DesktopWindowOptions.ConfirmCloseAsync` to ask the frontend through
  the authenticated script channel. Failed or missing UI replies keep the window
  open. Its script request times out after ten minutes; a new close attempt can retry.
  `CloseAsync`, disposal and process termination bypass this policy. Unsaved drafts
  are not crash durable. Browser mode uses `beforeunload` as a best effort.
- See the [close lifecycle contract](../../docs/guides/desktop/window-close-lifecycle.md)
  for platform support, custom hosts, cancellation and native verification limits.
- Keyboard labels, error associations, focus restoration, and responsive layout
  are present. This does not establish screen-reader parity on every native host.
- The directory is small and loaded as a snapshot. Large lists require paging,
  virtualization, and a measured update strategy.
- Session operation state survives transport reconnect while the host lives;
  operation resumption after a process crash is not implemented.
- Native clipboard, menus, notifications, navigation/back integration, and mobile
  services remain roadmap work. They are not emulated by an MVVM adapter.

See the [migration RFC](../../docs/guides/application/architecture/mvvm-migration-rfc.md)
and [step-by-step guide](../../docs/guides/application/guides/migrate-mvvm-to-runic.md).
