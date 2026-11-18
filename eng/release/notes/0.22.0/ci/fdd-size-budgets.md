- The size budgets now also gate the framework-dependent publish
  (`-r linux-x64 --self-contained false`) of the CS-WebUI and Desktop host
  fixtures, in the same CI step as the NativeAOT budgets.
  `fdd/folder-bytes` and `fdd/desktop/folder-bytes` gate the folder bytes
  (+2 %, without XML docs, PDBs and BridgeCodegen until FD5) and name the files
  that grew. `fdd/other-os-bytes` and `fdd/desktop/other-os-bytes` fail on any
  file of another operating system (limit 0). Until #1151, the Desktop fixture's
  project-reference publish exempts the Windows and macOS `Runic.Platform`
  assemblies by name.
