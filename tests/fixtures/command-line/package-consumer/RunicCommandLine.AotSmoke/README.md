# Command Line Native AOT smoke

This package-free executable references the three owned Command Line projects
directly and proves the complete parser-to-handler-to-JSON path. Its typed
handler also starts a shell-free child process with a ten-second timeout and
bounded output capture.

Run `Invoke-AotSmoke.ps1` to perform portable locked restore, managed execution,
`win-x64` Native AOT publication, native execution, and a final portable locked
restore. RID-specific restore state is isolated in the ignored
`obj/aot.packages.lock.json` file.
