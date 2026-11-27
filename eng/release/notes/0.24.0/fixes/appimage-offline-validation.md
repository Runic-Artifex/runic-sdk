- **`dotnet runic package appimage` no longer needs the network to validate the metainfo.** appimagetool's built-in
  AppStream check fetched the metainfo's URLs, so an offline machine, or a URL that answered with an error such as a 504,
  failed the package. The command now validates the metainfo itself with `appstreamcli validate --no-net`, as
  `package flatpak` does, and runs appimagetool with `--no-appstream`. A rejected metainfo is `RAPPPKG1006`; without
  `appstreamcli` the metainfo is not validated and the command warns with `RAPPPKG2003`. Fixes #1444.
