# dotnet-runic

Run generated-command diagnostics and coordinate local development for a Runic Application.

Generated applications pin this tool locally:

```bash
dotnet tool restore
dotnet runic doctor --project path/to/App.csproj
```

For other projects, install it globally with `dotnet tool install --global dotnet-runic --prerelease`. The frontend may use Node.js with npm or pnpm, or Bun; `doctor` reads the declared `packageManager` and committed lock file, then checks the matching toolchain. Use `dev` to run the managed host with configured Vite, Angular, or custom frontend development support and `inspect` for generated diagnostics.

Vite+ remains an optional facade over that declared manager and the standard project scripts. `vp run dev` is compatible with generated projects, but `dotnet runic dev` deliberately uses the underlying npm, pnpm, or Bun declaration so the reproducible build does not depend on a separate global Vite+ installation.

```bash
dotnet runic dev --project path/to/App.csproj
dotnet runic dev --project path/to/App.csproj -- --safe-mode profile-a
dotnet runic inspect --project path/to/App.csproj
dotnet runic migrate --check
```

## Local support envelope

`support` only reads an explicitly selected Editor diagnostic ZIP. It can preview the selected collector and every omission, collect one unsigned local JSON envelope, or verify and remove that envelope. It never launches a product, scans a workspace, uploads data, opens a network transport, or configures telemetry.

```bash
dotnet runic support --mode preview --editor-diagnostics /path/to/editor-diagnostics.zip
dotnet runic support --mode collect --editor-diagnostics /path/to/editor-diagnostics.zip --destination /path/to/support-envelope.json
dotnet runic support --mode remove --destination /path/to/support-envelope.json
```

The collector accepts only `runic.translations.editor-diagnostics/1` and rejects paths, source/translation/review text, sessions, cookies, and tokens. The resulting `runic.support-envelope/1` contains normalized application/workspace counts plus a fixed omission record; it is not a telemetry or hosted-diagnostics API.

Everything after `--` is forwarded unchanged to the application, including option-looking and negative values. The tool reads optional project properties and runs only your configured local commands. See the [development guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/contributing/development.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview tool; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
