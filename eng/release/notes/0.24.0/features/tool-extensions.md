## `dotnet runic translations` runs the Translations tool

`dotnet runic translations …` runs the installed Runic Translations tool, `runic-translations …`, with the arguments
unchanged and returns its exit code, so one command reaches both tools. The core finds the tool through
`RAX_TRANSLATIONS_TOOL` (a path), the nearest .NET tool manifest (`dotnet tool run`), `node_modules/.bin` or `PATH`, in
that order. Without it, the command fails with `RAPPCLI1013` and prints the install command for the tested release, for
example `dotnet tool install dotnet-runic-translations --version 0.15.1 --local`; it never installs anything itself.
`doctor` gains the `extensions` check: it asks the installed tool for its version (`--extension-info`, or `--version`
for older releases) and fails for another release line or protocol than the compatibility set's, warning for an older
release of the line. The tool IDs and commands are unchanged. `dotnet runic support` finds the Translations tool the
same way. See [Tool extensions](../../../tools/dotnet-runic/README.md#tool-extensions).
