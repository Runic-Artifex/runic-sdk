# Choose an application host

Runic Application members, actions, validation, generated contracts and frontend
controllers work with either host. Choose the host at composition time:

```sh
dotnet new runic-app-react --host cswebui -n CustomerApp
cd CustomerApp
dotnet tool restore
dotnet runic dev
# Switch the same project, including its frontend and contract generation:
dotnet runic dev --host desktop
# Select the host for a normal build or publish:
dotnet publish -c Release -r linux-x64 -p:RunicHost=cswebui
```

The same `--host` option is available in the React, Vue, Svelte and Angular
templates. Set `RunicHost` in the project to change the default permanently.
Keep restore enabled when switching: each selection has a different dependency
graph. CLI overrides apply to child processes, including frontend tooling.

| Requirement | CS-WebUI integration | Runic Desktop integration |
| --- | --- | --- |
| Runic Application lifecycle, typed members and actions | Yes | Yes |
| Validation, cancellation, state subscriptions and reconnect | Yes | Yes |
| CLI host selection and frontend development server | Yes | Yes |
| Embedded, manifest-bound assets | Yes | Yes |
| ASP.NET Core runtime dependency | No | Yes |
| Isolated surfaces and presentation fallback policy | Use Desktop for these enhancements | Yes |
| Native close veto / unsaved-change coordination | Explicitly rejected when required | Yes |
| Multiple hosts and independent native lifetime | One native runtime owner per process | Desktop presentation ownership rules |

The CS-WebUI package remains independent. The SDK integration consumes public
`CsWebUi` packages and does not reference its source checkout. An application can
use the lower-level CS-WebUI API directly; the Runic integration adds application
composition and bridge policy. Stopping the integration ends the process-wide
WebUI runtime, including other WebUI windows. It cannot be restarted in that
process.

The customer migration reference exercises the same UI, business rules and
contract through both hosts. Its development acceptance test switches from
CS-WebUI to Desktop, connects the live bridge, edits CSS and checks that the
unsaved draft survives HMR. The template acceptance matrix builds package-only
consumers for all four frontends and both hosts and rejects Desktop/ASP.NET Core
dependencies in CS-WebUI consumers.

## Existing applications

Reference `Runic.Application.CsWebUi` and compose `UseCsWebUi`, or reference
`Runic.Application.Desktop` and compose `UseDesktop`. Configure assets and a
session factory for the selected host. The generated templates keep these
choices in `Host.CsWebUi.cs` and `Host.Desktop.cs`; the project conditionally
compiles one of them. Frontend transport selection happens at bootstrap, without
changing UI components or the contract.

For development, opt into `assets.WithDevelopmentDocument()`. The CLI creates a
bounded entry document whose module and stylesheet URLs point to the frontend
development server. The host retains its native bootstrap and asset boundary;
only that explicit entry snapshot changes. Normal published execution uses the
embedded assets when `RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT` is unset.

On NixOS, use the repository's [development shell](nixos-development.md).
Use the [size and tuning guide](size-and-tuning.md) for measurements and the
minimal Desktop profile.
