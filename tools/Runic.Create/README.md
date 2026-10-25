# Runic.Create

Create a Runic application by answering a few questions:

```sh
dnx Runic.Create@<VERSION>
```

`dnx` ships with the .NET 10 SDK and runs the tool without installing it. The
creator asks for the project name, frontend (React, Vue, Svelte, SvelteKit or Angular),
JavaScript package manager (npm, pnpm or Bun), Window host (Runic Desktop, which
opens a native window, or CS-WebUI, which opens a browser window), ViewModel
library (ReactiveUI or CommunityToolkit.Mvvm), whether to add a test
project, a Nix flake or the packaging manifests, and whether to restore the NuGet
packages. Each choice is described in one line. It then installs the matching
`Runic.Application.Templates` version and runs `dotnet new runic-app`. The
install goes to your `dotnet new` template store, as `dotnet new install` does,
and replaces another installed version; an installed matching version is kept,
whatever language the SDK uses. `dotnet new uninstall Runic.Application.Templates`
removes it.
Accepting every default creates a Runic Desktop and ReactiveUI project.

On NixOS, or another system with Nix but without a .NET 10 SDK, the Runic SDK
flake's `create` app runs the creator with a .NET SDK from nixpkgs; its
arguments follow `--` as well:

```sh
nix run github:Runic-Artifex/runic-sdk/v<VERSION>#create -- MyApp --nix
```

`--nix` adds the project's own `flake.nix`, whose `nix develop` shell then has
the .NET SDK. See the
[Nix packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/nix/README.md#create-a-project).

Pass an option to skip its question, or `--yes` to accept the defaults for
every option you leave out. The questions are written to standard error and
asked only when standard input and standard error are a terminal; otherwise, and
with `RUNIC_COMMANDLINE_NO_INPUT=1`, the defaults apply. Escape or Ctrl+C at a
question cancels. Arguments for the creator follow `--`:

```sh
dnx Runic.Create@<VERSION> -- MyApp --frontend svelte --package-manager bun --host cswebui --view-models toolkit
```

After creating a project, the creator prints the next steps and two commands
that reproduce it without questions: the `dnx Runic.Create` invocation and
the equivalent `dotnet new install` and `dotnet new runic-app` commands.
`--dry-run` prints those commands without creating anything. The
[documentation picker](https://docs.runic-artifex.eu/create/) builds the same
commands in the browser.

| Option | Choices | Default |
| --- | --- | --- |
| `--frontend` | `react`, `vue`, `svelte`, `sveltekit`, `angular` | `react` |
| `--package-manager` | `npm`, `pnpm`, `bun` | `npm` |
| `--host` | `desktop`, `cswebui` | `desktop` |
| `--linux-gtk` | `3`, `4`: the GTK version of Runic Desktop on Linux; Windows, macOS and `cswebui` are the same with either, so the creator asks for it, and prints it, only with `--host desktop` unless you pass it | `3` |
| `--view-models` | `reactiveui`, `toolkit` | `reactiveui` |
| `--tests` | Adds a TUnit test project for the Window | No test project |
| `--nix` | Adds a Nix flake that builds the application with the Runic SDK's `buildRunicApplication` | No flake |
| `--packaging` | Adds the `packaging/` manifests that `dotnet runic package` fills (MSIX, macOS bundle, desktop entry, AppStream metadata, Flatpak manifest), as editable overrides of the command's defaults | The command's built-in manifests |
| `--restore` | Restores the project's NuGet packages after creating it, for an IDE that opens it next | No restore |
| `--application-id` | The reverse-DNS application ID, such as `org.example.Notes` | `com.example.` and the project name's ASCII letters and digits |
| `--output`, `-o` | A directory | The project name |
| `--force` | Creates the project in a directory that is not empty, as `dotnet new --force` does; files the template writes replace existing files of the same name | A non-empty directory is `RCREATE003` |
| `--no-install` | Uses the installed `Runic.Application.Templates` instead of installing this version, for example offline | Installs the matching version |
| `--template-source` | An additional NuGet source for the template | |

As with `dotnet new`, `--output apps/MyApp` or `-o apps/MyApp` chooses the
destination directory. The creator's global `--format human|json` option selects
the command output format.

The creator has no subcommand to name: `dnx Runic.Create -- create` creates a
project named `create`.

The frontend, package manager, host, Linux GTK and ViewModel choices, `--tests`, `--nix`, `--packaging`, `--restore`
and `--application-id` come from the template itself, so `dotnet new runic-app --help` lists the same options.
The creator reads the template's text options, such as `--application-id`, from the template model, so a text option
a later template adds is a creator option too. They are not asked: without a value the template computes its
default, and the commands leave the option out.

With `--no-install` the creator runs only `dotnet new runic-app`, with whichever
`Runic.Application.Templates` version is installed; if none is,
`dotnet new` fails with `RCREATE005`. Install the matching version with the
printed `dotnet new install` command, or run the creator without
`--no-install`.
With `--tests`, the next steps include `dotnet test --project MyApp.Tests`.

## JSON output

`--format json` writes one `runic.commandline/1` response to standard output
with `"command": "create"` and the `runic.create/2` payload:

```json
{
  "name": "MyApp",
  "directory": "apps/MyApp",
  "fullPath": "/work/apps/MyApp",
  "version": "<VERSION>",
  "options": { "frontend": "react", "package-manager": "npm", "host": "desktop", "linux-gtk": "3", "view-models": "reactiveui" },
  "toggles": { "tests": true, "nix": false, "packaging": false, "restore": false },
  "textOptions": {},
  "commands": [
    "dotnet new install Runic.Application.Templates@<VERSION>",
    "dotnet new runic-app --name MyApp --output apps/MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests"
  ],
  "reproduceCommand": "dnx Runic.Create@<VERSION> -- MyApp --output apps/MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests",
  "created": true
}
```

| Property | Meaning |
| --- | --- |
| `name` | The project name. |
| `directory` | The destination as given with `--output`, or the project name. |
| `fullPath` | The absolute path of `directory`. |
| `version` | The creator and template version. |
| `options` | The selected value of each choice option, keyed by its long name. |
| `toggles` | Whether each Boolean option is on, keyed by its long name. |
| `textOptions` | The value of each text option you passed, such as `application-id`, keyed by its long name. Since 0.11. |
| `commands` | The `dotnet new install` and `dotnet new runic-app` commands; with `--no-install`, only `dotnet new runic-app`. |
| `reproduceCommand` | The `dnx Runic.Create` invocation that creates the same project without questions. |
| `created` | `true` when the project was created; `false` for `--dry-run`, which runs no command and leaves the directory untouched. |

JSON output never asks questions; options you do not pass use their
defaults. A failed run sets `success` to `false` and carries the `RCREATE` code
in `fault.code`. The output of `dotnet new` goes to standard error.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The project was created, or `--dry-run` printed its commands. |
| `1` | The project could not be created: `dotnet new install` or `dotnet new runic-app` failed (`RCREATE004`, `RCREATE005`). |
| `2` | Usage or validation error, such as an unknown option value, an invalid name or a non-empty destination without `--force` (`RCREATE001` to `RCREATE003`). |
| `3` | Internal failure. |
| `130` | Cancelled, for example with Ctrl+C. |

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/),
[source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Create),
and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview
package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
