# Runic.Create

Create a Runic application by answering a few questions:

```sh
dnx Runic.Create@<VERSION>
```

`dnx` ships with the .NET 10 SDK and runs the tool without installing it. The
creator asks for the project name, frontend (React, Vue, Svelte or Angular),
JavaScript package manager (npm, pnpm or Bun), Window host (CS-WebUI or Runic
Desktop), and ViewModel library (CommunityToolkit.Mvvm or ReactiveUI). It then
installs the matching `Runic.Application.Templates` version and runs
`dotnet new runic-app`.

Pass an option to skip its question, or `--yes` to accept the defaults for
every option you leave out. Arguments for the creator follow `--`:

```sh
dnx Runic.Create@<VERSION> -- MyApp --frontend svelte --package-manager bun --host desktop --view-models reactiveui
```

After creating a project, the creator prints the next steps and two commands
that reproduce it without questions: the `dnx Runic.Create` invocation and
the equivalent `dotnet new install` and `dotnet new runic-app` commands.
`--dry-run` prints those commands without creating anything. The
[documentation picker](https://docs.runic-artifex.eu/create/) builds the same
commands in the browser.

| Option | Choices | Default |
| --- | --- | --- |
| `--frontend` | `react`, `vue`, `svelte`, `angular` | `react` |
| `--package-manager` | `npm`, `pnpm`, `bun` | `npm` |
| `--host` | `cswebui`, `desktop`, `desktop-gtk4` | `cswebui` |
| `--view-models` | `toolkit`, `reactiveui` | `toolkit` |
| `--directory` | A directory | The project name |
| `--template-source` | An additional NuGet source for the template | |

For example, `--directory apps/MyApp` chooses the destination directory. The
creator's global `--output human|json` option selects the command output format.
The equivalent `dotnet new runic-app` command uses `--output` or `-o` for its
destination directory.

The frontend, package manager, host and ViewModel choices come from the template
itself, so `dotnet new runic-app --help` lists the same choices.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/),
[source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Create),
and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview
package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
