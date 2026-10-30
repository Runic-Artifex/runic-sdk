linux-x64, linux 7.3.0, .NET SDK 10.0.403; startup medians of 15 serve-only runs.

| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| fdd | Kestrel | 9.14 | 0.10 | 0.00 | NETCore.App, AspNetCore.App | 170.7 | 214.5 | 82.3 |
| fdd | Native | 10.64 | 0.10 | 0.00 | NETCore.App | 134 | 160.9 | 65.6 |
| sc | Kestrel | 113.71 | 0.10 | 20.47 | — | 167.2 | 208.4 | 80.1 |
| sc | Native | 89.39 | 0.10 | 0.00 | — | 129.6 | 154.3 | 63.6 |
| aot | Kestrel | 13.16 | 12.96 | 0.00 | — | 17.4 | 21.4 | 33.4 |
| aot | Native | 10.16 | 9.97 | 0.00 | — | 9 | 12.8 | 24.2 |
