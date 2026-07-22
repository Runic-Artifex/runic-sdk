# Reproduction probe

This non-shipping project records the high-level feasibility scenario. It deliberately stays outside root solution/build files and has no default upstream path.

Clone external `cs-webui` at the report's exact SHA. Copy `Program.cs.txt` to `Program.cs` and `WebUIToolkit.MVVM.UpstreamAotProbe.csproj.txt` to `WebUIToolkit.MVVM.UpstreamAotProbe.csproj` in a temporary directory, then supply the absolute checkout through MSBuild:

```powershell
dotnet publish WebUIToolkit.MVVM.UpstreamAotProbe.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output <temporary-output> `
  -p:UpstreamRepositoryRoot=<absolute-cs-webui-checkout>
```

At run time, set the external wrapper's documented native-library path environment variable to `webui-2.dll` built from the exact native-source pin. Start the published executable, read its `READY` URL, and keep a real browser client alive at that URL until the process prints `PASS` or exits. The browser must execute `/webui.js`; an HTTP-only request is not the tested scenario.

The project name, assembly identity, and MSBuild property are first-party `WebUIToolkit` or neutral. Names preserved inside the inert `.txt` source transcripts refer only to the external dependency's actual API identity.
