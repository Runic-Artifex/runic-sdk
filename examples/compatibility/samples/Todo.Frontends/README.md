# Framework Todo samples

These samples render the same two C# application layers through every supported
browser framework:

| .NET project | Browser adapter | Simple | Advanced |
| --- | --- | --- | --- |
| `Todo.React` | `@webuitoolkit/mvvm-react` hooks and external store | yes | yes |
| `Todo.Vue` | `@webuitoolkit/mvvm-vue` computed refs | yes | yes |
| `Todo.Svelte` | `@webuitoolkit/mvvm-svelte` readable store | yes | yes |
| `Todo.Angular` | `@webuitoolkit/mvvm-angular` signals | yes | yes |

Each project starts SimpleTodo by default and selects AdvancedTodo with
`--advanced`:

```console
dotnet run --project samples/Todo.React
dotnet run --project samples/Todo.React -- --advanced
```

Replace `React` with `Vue`, `Svelte`, or `Angular` to run the other frontend.
`WebUIToolkit.Frontend.Sdk` invokes the matching frontend production build and
publishes its manifest-driven output and the CsWebUi frame bridge. React, Vue,
and Svelte use Vite; Angular uses its supported application builder. The
sample projects add the locally pinned Bootstrap 5.3 and Font Awesome assets.

`Todo.Shared` owns the framework-independent models, ViewModels, persistence,
validation, and workflow. The original cwhtml SimpleTodo and AdvancedTodo
projects consume that same assembly. `Todo.FrontendHost` owns the one native
CsWebUi binary channel. A single `todo.frontend.json` symbol model generates
its C# projection vocabulary and direct CommunityToolkit adapter factory, plus
the typed TypeScript contract used by all four browser applications.

The frontend source shares generated contracts, deterministic connection
startup, and styling. Vue uses ordinary SFCs and Angular uses production AOT
compilation. View composition and lifecycle use generated handles through React
hooks, Vue computed refs, Svelte derived stores, and Angular signals so
differences remain visible. Commands accept typed item IDs directly, derived
properties are read-only, and background C# changes are pushed to the accepted
projection without browser polling.

Run deterministic checks without opening a window:

```console
npm test --workspace @webuitoolkit/sample-todo-react
dotnet msbuild samples/Todo.React/Todo.React.csproj -t:WebUIToolkitFrontendWatch
dotnet run --project samples/Todo.React -- --smoke-test
dotnet run --project samples/Todo.React -- --advanced --smoke-test
dotnet run --project samples/Todo.React -- --browser-smoke-test
dotnet run --project samples/Todo.React -- --advanced --browser-smoke-test
```

Release builds invoke Vite minification and content hashing and emit both
Vite's manifest and a SHA-256 asset manifest. The
[framework integration guide](../../docs/guides/frontend-frameworks.md) records the gaps,
implemented product changes, and remaining release work exposed by this matrix.
