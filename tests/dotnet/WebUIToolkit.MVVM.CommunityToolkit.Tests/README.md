# CommunityToolkit MVVM conformance fixtures

This owner-local executable uses real `CommunityToolkit.Mvvm` 8.4.2 attributed
partial ViewModels. Its fixture IDs are prepared for the shared G3 inventory:

| Fixture ID | Evidence |
| --- | --- |
| `communitytoolkit.observable-property.v1` | Generated observable property snapshot, set, `PropertyChanged`, and validation patch |
| `communitytoolkit.relay-command.v1` | Parameterless and typed relay command execution plus `NotifyCanExecuteChangedFor` |
| `communitytoolkit.async-command-cancellation.v1` | `IAsyncRelayCommand.Cancel()` through request cancellation |
| `communitytoolkit.validation-metadata.v1` | `ObservableValidator` errors and deterministic metadata |
| `communitytoolkit.generated-metadata.v1` | Ordered closed binding metadata |
| `communitytoolkit.generated-member.title.v1` | Existing PE-proof title shape remains covered |
| `communitytoolkit.generated-member.submit-command.v1` | Existing PE-proof command shape remains covered |

The root conformance owner must register the first five IDs and add this project
to the mandatory G3 inventory. The final two existing IDs remain owned by the
upstream PE-metadata proof.
