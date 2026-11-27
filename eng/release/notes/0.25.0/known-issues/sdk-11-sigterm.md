- **With the .NET 11 SDK, a SIGTERM does not stop `dotnet runic dev` on Linux.** The SDK's native `dotnet` entry
  point exits on SIGTERM without forwarding it to the local tool
  ([dotnet/sdk#57091](https://github.com/dotnet/sdk/issues/57091)), so `dev` keeps running without its parent, with
  its Window and development server. A process manager or IDE that stops it with SIGTERM leaves it behind. Stop it
  with Ctrl+C, send the signal to `dev` itself (the session file's `processId`), or set `DOTNET_CLI_ENABLEAOT=0`,
  which makes SDK 11 forward the signal as SDK 10 does. Runic DevKit stops `dev` through its standard input
  (`RUNIC_DEV_STOP_ON_INPUT`), which SDK 11 does not affect.
