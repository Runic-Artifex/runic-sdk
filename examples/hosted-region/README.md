# Hosted region

An installer shows a separate check tool as one step of its wizard, in its own
window ([region mode](../../docs/hosted-regions.md)). The installer's header,
step indicator and footer stay; the check tool's page fills the content area
between them. The check tool is a Runic Desktop application of its own, with its
own process and ViewModel, and it also runs on its own. Hosted applications and
regions are experimental: both projects suppress
[`RUNICDESK103`](../../docs/experimental.md#RUNICDESK103).

| File | What it does |
| --- | --- |
| [`Check/Program.cs`](Check/Program.cs) | The guest. Its own `hosted` parameter accepts a host; without it the check opens its own window. It accepts an elevated host (`AllowElevatedHost`, see [Elevated](#elevated)). |
| [`Check/CheckViewModel.cs`](Check/CheckViewModel.cs) | Three checks, which run when the Window opens. Its close guard asks, with a message box, before a running check is given up. It does not know whether it is in a region. |
| [`Check/Tests`](Check/Tests/HostedCheckTests.cs) | The check in a region without an installer: `RunicTestHost` attaches it to a region and plays the installer's departures. |
| [`Installer/CheckPolicy.cs`](Installer/CheckPolicy.cs) | The installer's policy: `RunicHostedWindowDecision.Embed("check", options)` when `CanEmbed`, with the frame's permissions. |
| [`Installer/InstallerViewModel.cs`](Installer/InstallerViewModel.cs) | The wizard: Welcome, System check, Finish in a navigation region. Its check step is a departure guard that asks the check (`RunicHostedRegion.AsDepartureGuard`). It follows the region's state, title, attaches and detaches, and starts the check again after it ended. |
| [`Installer/InstallerWindow.cs`](Installer/InstallerWindow.cs) | The installer's window: its close asks the current step first, as Back and Next do. |
| [`Installer/Frontend/index.html`](Installer/Frontend/index.html) | The step page: `<runic-hosted-region name="check">` with content for each state. |
| [`Installer/Program.cs`](Installer/Program.cs) | The host: GTK 3 on Linux, the page's Content-Security-Policy, the host-wide region events, `--elevated` and `--probe`. |

Run the installer. It builds the check first:

```sh
dotnet run --project examples/hosted-region/Installer -c Release
```

Choose **Next**. The installer starts the check, whose Window shows in the step
page while its three checks run; the step indicator shows the check page's title,
such as "System check (passed)". Tab moves from the installer's Back and Next
into the check's page and out again; a screen reader reads the check as part of
the installer's window. Choose **Next** or **Back** while the check runs: the
check asks, in a message box owned by the installer's window, whether to stop it.
**Keep checking** keeps the wizard on the step. Once the check allows it, its
Window closes, the check exits and the wizard moves on. **Back** from Finish
starts the check again. Closing the installer's window asks the same way.

Run the check on its own, in a window of its own:

```sh
dotnet run --project examples/hosted-region/Check -c Release
```

## What each side does

- **The decision.** The installer's policy shows the check's Window in the
  region "check" (`Embed`) when the check speaks `region/1`
  (`RunicHostedWindowRequest.CanEmbed`); a check of 0.17 could not be embedded,
  and the policy refuses it. The check asks for nothing: its
  `OpenWindowAsync` completes at once, and its page loads when the region
  attaches the Window.
- **The region.** The page declares the region with `<runic-hosted-region>`
  (`defineHostedRegion()` from `@runic-artifex/views/experimental`). The
  region's layout sets its size (`sizing="fill"`): it fills the content area,
  and the check's page scrolls inside it. The check's page loads in a
  sandboxed frame from the check's own loopback origin, `[::1]` for the
  installer's page on `127.0.0.1`, so no cookie of the installer's page reaches
  the check, and neither page can read or script the other.
- **States and fallback content.** The element shows the frame while the
  region is attached, and otherwise the child whose `slot` names its state:
  `waiting` (the check is starting, or could not start, with the reason),
  `loading`, `detached` (with the reason from `InstallerViewModel.CheckNote`),
  `ended` (with a button that starts the check again) and `unsupported`.
  `slot="fallback"` covers any other. The installer's C# follows the same
  states (`RunicHostedRegion.State`, `StateChanged`) and shows the latest
  attach or detach and its reason (`Attached`, `Detached`) in the footer.
- **Departures.** Back, Next and the window's close are navigation departures
  of the wizard's step region. The check step's guard calls
  `RunicHostedRegion.AsDepartureGuard()`, which sends the check a close request:
  the check's `CanCloseAsync` decides, and the departure goes ahead only once
  the check allowed it and its Window closed. A page that removes or reloads the
  region without a decision in C# only detaches the Window, which attaches again
  when the region reappears (`ReattachOnReload`).
- **Permissions.** `CheckPolicy.RegionOptions.Permissions` is
  `DesktopPermissionGrant.None`: the frame's `allow` attribute stays empty, and
  the installer window's engine refuses the frame's camera and microphone
  requests, whatever the installer's own windows may use. `MediaCapture` would
  allow both (not on WebKitGTK, which denies them in a region and, while a
  region is attached, the host page's own requests too).
- **Focus.** `FocusOnAttach` moves the keyboard focus into the check when it
  attaches, but only while the installer's page holds no focus; the check never
  takes the focus from the installer's controls. Clipboard and launches count as
  the user's only after the user moved the focus into the region.
- **Dialogs.** The check's message box shows as the installer's, owned by the
  installer's window. While the region is detached, the check's dialogs report
  `OwnerUnavailable`.
- **The page's policy.** The installer sends a Content-Security-Policy with
  `frame-src http:`. The check's frame comes from `[::1]` on a port that
  changes with each run, and a CSP host source cannot name an IPv6 address; a
  policy without a `frame-src` that admits it (or a `default-src` that does)
  blocks the frame, and the region stays `loading`. The check's own page sends
  no `frame-ancestors`: Runic sends the frame's, which names only the
  installer's page. A guest page whose policy says `frame-ancestors 'none'`
  blocks its own region.

## Elevated

The issue this example follows (#866) is an installer that runs as
administrator and hosts a check tool that needs administrator rights too (#865).
With `--elevated` the installer starts the check with
`RunicGuestStartInfo.AllowElevatedGuest`; the check accepts it because it sets
`RunicGuestOptions.AllowElevatedHost`, and then checks itself that its parent is
the installer, elevated and the same user. In a region, the elevated check
admits connections, its frame's included, only from processes that run elevated,
such as the installer's WebView2 (event 2029 for a refused one).

On Windows, run it from an elevated terminal (**Run as administrator**):

```sh
dotnet run --project examples/hosted-region/Installer -c Release -- --elevated
```

- Without administrator rights, `--elevated` fails to start the check with
  `host-not-elevated`, which the step shows.
- An elevated installer without `--elevated` starts a check that runs elevated
  too and refuses the installer (`guest-refused`, `guest-elevated`).
- A real installer and check declare
  `<requestedExecutionLevel level="requireAdministrator" />` in their
  application manifests and are installed where only administrators can write,
  such as `Program Files`. The installer names the check by a fully qualified
  path; this example's path is its build output, which only suits trying it.
- On Linux the pair is root (`sudo`). On macOS an elevated check refuses
  regions, since Runic cannot check which process connects to it there: the
  region stays `detached` (event 2026, `refused`), and the step's fallback
  content says why.

## Checks

```sh
dotnet run --project examples/hosted-region/Check/Tests -c Release
xvfb-run -a dotnet run --project examples/hosted-region/Installer -c Release -- --probe
```

The check's tests start the built check with `RunicTestHost` and need no
display. `--probe` is CI's check on a real desktop: the installer goes to the
check step, the check's Window attaches to the region and its page reports its
title from the frame, the installer detaches it and attaches it again
(`DetachAsync`, `MoveToAsync`), and Next leaves the step through the check's
close guard until the region ends and the check exits. CI runs the tests in the
Window and View journeys job, the probe in the native job's Linux legs (GTK 3
and WebKitGTK 4.1), and `--probe --elevated` in its Windows x64 leg, whose steps
run elevated: there installer and check run as the elevated pair in WebView2.

Not automated: a screen reader reading the check in document order. Check it by
hand with NVDA and Narrator on Windows and VoiceOver on macOS: Tab from Back
into the check's table and button, and out to Next, and listen that the frame is
announced by the check's title.

The example builds the Runic packages from source. To copy it, replace the
`ProjectReference` and `Import` lines with package references, as
[First Window](../first-window/README.md#copy-it-into-your-own-project) shows.
