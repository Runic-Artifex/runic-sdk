## Development-only views and members (#1109)

A view that only development needs, such as a mock-control or diagnostics panel, can stay out of the published
executable: mark its ViewModel with `[RunicDevelopmentOnly]`, or mark single state properties and commands (on the
command property or its `[RelayCommand]` method). Builds, `dotnet run`, the debugger and untrimmed publishes keep
everything. A trimmed or NativeAOT publish turns the feature switch `Runic.Application.Views.DevelopmentOnlyViews` off,
so the trimmer removes the marked ViewModel's generated Bridge, about 16 KB per ViewModel in the measurements of
#1109, and the code of marked members:

- Opening or presenting a marked ViewModel throws an `InvalidOperationException` that names it. Test
  `RunicDevelopmentOnly.IsSupported` before presenting it, as your own development-only code can.
- A marked property is absent from the state and cannot be set or written; a marked command and its `can…` and
  `is…Executing` state are absent too, and calling or starting it is rejected with reason `unavailable`.

The generated TypeScript is the same in every configuration and describes the marked members truthfully: optional
state members, and commands documented as possibly unavailable. Marking a member changes the View's contract;
marking a ViewModel does not. Set the MSBuild property `RunicDevelopmentOnlyViews` to `true` to keep the marked code in
every publish (`auto`, the default, removes it only from trimmed and NativeAOT publishes). `[RunicDevelopmentOnly]` on
an interaction or view request has no effect and is warning `RUNICBRIDGE072`. See
[Development-only views and members](../../../docs/packaging.md#development-only-views-and-members).
