osx-arm64, darwin 25.6.0, .NET SDK 10.0.403, macos26 20261013.0424.1; startup medians of 9 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.17 | 0.12 | 0.00 | NETCore.App, AspNetCore.App | 439.2 | 533.2 | 108.7 |
| fdd | Native | 10.41 | 0.12 | 0.00 | NETCore.App | 311.9 | 356 | 90.8 |
| sc | Kestrel | 120.46 | 0.12 | 22.81 | — | 277.5 | 334.3 | 108.8 |
| sc | Native | 93.02 | 0.12 | 0.00 | — | 200.3 | 226.1 | 90.9 |
| aot | Kestrel | 12.05 | 11.83 | 0.00 | — | 137.4 | 187.5 | 52.5 |
| aot | Native | 8.87 | 8.65 | 0.00 | — | 67.1 | 74.9 | 43.8 |
