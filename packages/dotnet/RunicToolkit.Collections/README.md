# RunicToolkit.Collections

Use deterministic observable range updates while preserving collection-item identity.

```bash
dotnet add package RunicToolkit.Collections --prerelease
```

Requires .NET 10. Use this BCL-only package when a UI-facing collection needs range notifications; choose `UpdateTo` when reconciling a new data set with existing item instances.

```csharp
using RunicToolkit.Collections;

var items = new ObservableRangeCollection<string>(["north"]);
items.AddRange(["east", "west"]);
```

The collection is single-owner, does not capture a dispatcher, and is not thread-safe. Structural mutation from its callbacks is rejected. See the [collection contract](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Collections), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
