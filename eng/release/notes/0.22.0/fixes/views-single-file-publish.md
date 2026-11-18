- Single-file publish of a Views application failed with NETSDK1191 ("A runtime
  identifier for the property 'PublishSingleFile' couldn't be inferred"), for every
  host and also with `-r`. The Views generator's own build and its bootstrap pass
  clear the runtime identifier but kept `PublishSingleFile` and
  `IncludeNativeLibrariesForSelfExtract`, as they kept `PublishReadyToRun` before
  0.15. They now reset both, so `dotnet publish -r <rid> -p:PublishSingleFile=true`
  works, framework-dependent or self-contained.
