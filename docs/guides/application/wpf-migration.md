# WPF to WebUIToolkit migration guide

This guide maps common WPF application concepts onto a CsWebUi frontend. It
assumes an incremental migration: preserve application and ViewModel behavior
first, then replace each XAML view with compiled HTML or a browser-framework
component.

## Choose a frontend track

Use compiled `.cwhtml` and HTMX when the team wants to keep rendering,
validation, commands, and most UI composition in C#. It has the smallest
conceptual distance from a conventional WPF solution and does not require an
application-specific TypeScript frontend.

Use the TypeScript MVVM client with React, Vue, Svelte, or Angular when the team
wants browser-native component authoring and is comfortable maintaining a
separate frontend project.

Both tracks use the same ViewModels, collections, commands, Flow services,
text resources, CsWebUi host, and desktop capability services.

## Concept mapping

| WPF | WebUIToolkit |
| --- | --- |
| `Window` / `Application` | `WebUiWindow`, `WebUiApplication`, and `WebUIToolkit.Hosting.CsWebUi` |
| `DataContext` | One explicitly registered ViewModel contract/session |
| `{Binding Path=...}` | Generated `.cwhtml` binding or generated TypeScript projection |
| `INotifyPropertyChanged` | CommunityToolkit or ReactiveUI property binding |
| `ObservableCollection<T>` | `BindCollection` plus protocol collection snapshots/patches |
| `ICommand` | Generated command binding |
| `INotifyDataErrorInfo` | Validation projection rendered beside the associated field |
| `DataTemplate` | Compiled fragment/component selected by a closed ViewModel contract |
| `ContentControl` / regions | Flow navigation region and frontend presenter |
| modal `Window.ShowDialog` | Typed Flow dialog and CsWebUi presenter |
| `NavigationService` | `WebUIToolkit.MVVM.Navigation` |
| `Dispatcher` | Host-owned `IUiDispatcher` for native window operations |
| `.resx` / resource lookup | `WebUIToolkit.TextResources` generated catalog |
| styles and resource dictionaries | Bootstrap variables/components or a consumer design system |
| value converter | Typed ViewModel property, generated render helper, or frontend formatter |
| trigger | ViewModel-derived state plus HTMX/browser conditional rendering |
| code-behind | Partial `.cwhtml.cs` type or browser component code |

## Migration sequence

### 1. Separate application behavior from WPF controls

Move business behavior out of `Window`, `UserControl`, and event-handler types.
The ViewModel should expose ordinary typed properties, collections, commands,
validation, and cancellation. It must not retain `DependencyObject`,
`DispatcherObject`, `RoutedEventArgs`, or concrete WPF controls.

Do not rewrite correct business services merely to make them “web shaped.”
CsWebUi runs in the same .NET process, so existing repositories, domain
services, dependency injection, and application state can normally remain.

### 2. Establish stable view contracts

Give each migrated screen a stable logical contract. Bind members explicitly
while migrating so accidental CLR renames cannot silently change the wire
surface. Generated bindings should eventually own these identifiers.

Start with:

- scalar editable properties;
- command availability and execution;
- validation;
- observable collections;
- the screen's authoritative snapshot.

### 3. Replace XAML structure with semantic HTML

Translate layout intent rather than XAML syntax:

- `Grid` becomes Bootstrap grid, CSS grid, or flex layout;
- `StackPanel` becomes a Bootstrap stack or flex container;
- `ItemsControl` becomes a repeated semantic list, table, or card fragment;
- `TextBox` becomes a labelled form control with validation text;
- `Button Command=...` becomes an HTMX action or MVVM client command;
- `Visibility` becomes conditional rendering or appropriate hidden state.

Desktop samples use Bootstrap 5.3 and Font Awesome by default. Keep the markup
semantic so another application can replace those presentation choices.

### 4. Migrate navigation and dialogs

Do not reproduce a tree of WPF windows inside ad-hoc JavaScript. Register
ViewModel-first navigation and typed dialogs with Flow, then let the frontend
presenter decide whether content appears in a region, Bootstrap modal,
offcanvas panel, tab, or separate CsWebUi window.

Preserve close guards, typed results, cancellation, ownership, and deterministic
disposal. These behaviors matter more than matching the old window chrome.

### 5. Move desktop capabilities behind services

Replace direct WPF APIs with application-owned interfaces before selecting the
CsWebUi implementation. Typical boundaries include:

- clipboard;
- open/save file selection;
- drag and drop;
- notifications and tray integration;
- browser profile and storage;
- focus and keyboard accelerators;
- window size, position, minimize/maximize, and multi-window ownership;
- external URL launch.

This keeps ViewModels testable and makes unsupported platform behavior
explicit.

### 6. Validate the native application

For each migrated screen verify:

- a real browser-to-C# command round trip;
- property, validation, and collection updates in the DOM;
- keyboard and focus behavior;
- high-contrast and zoom behavior;
- window close and cancellation;
- persistence and restart;
- trimmed and Native-AOT publication on the target platform.

Fake browser-host tests remain useful unit tests, but they do not replace this
acceptance path.

## Styling policy

Bootstrap 5.3 and locally packaged Font Awesome are migration defaults because
they match the customer applications motivating WebUIToolkit. They are not
runtime requirements.

Keep ViewModels and Flow contracts free of CSS class names and icon identities.
Place visual choices in `.cwhtml` components, framework components, theme
configuration, or small rendering adapters. Applications may use shadcn,
Tailwind, raw CSS, another icon family, or a completely custom design system
without replacing the MVVM or native host layers.

## Intentional differences from WPF

WebUIToolkit does not aim to emulate every dependency-property, routed-event,
or XAML feature. Browser layout, accessibility, focus, input, and styling
should use platform-native HTML and CSS behavior.

The migration promise is preservation of application architecture and typed UI
behavior—not pixel-identical reproduction of WPF's rendering engine.
