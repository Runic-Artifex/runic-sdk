# Command Line Native AOT smoke

This package-free executable references the three owned Command Line projects
directly and proves the complete parser-to-handler-to-JSON path. Its typed
handler also starts a shell-free child process with a ten-second timeout and
bounded output capture.

Run `Invoke-AotSmoke.ps1` to perform restore, managed execution, `win-x64`
NativeAOT publication, and native execution.
