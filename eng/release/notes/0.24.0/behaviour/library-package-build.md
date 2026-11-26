- A client library whose frontend package has a `build` script now builds it (`RunicApplicationFrontendBuildEnabled`
  defaults to `auto` instead of `false` in a client library), and installs the frontend's packages for that build. Set
  `RunicApplicationFrontendBuildEnabled=false` in the library to keep building the package yourself. A package without
  a `build` script stays unbuilt, as before.
