## `dotnet runic dev` stops on a request on its standard input

An IDE that can't send Ctrl+C can now stop `dotnet runic dev` gracefully.
Visual Studio on Windows is one: a process without a console has no Ctrl+C, so
until now it ended the process tree by force. When the IDE sets
`RUNIC_DEV_STOP_ON_INPUT=1` and redirects `dev`'s standard input, `dev` stops
as on Ctrl+C at a line `stop` or at the end of the input. It stops the frontend
and the Window, marks the session file `"stopped"`, deletes its frontend marker
and development pages, and touches `obj/runic/dev-stopped`. So a build right
after the stop builds the production frontend again. The processes that `dev`
starts get an input of their own. Without the variable, `dev` doesn't read its
input, so a terminal is unaffected. See
[IDE contract](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/tools/dotnet-runic/README.md#ide-contract).
