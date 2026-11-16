- **Internal MSBuild names start with `_Runic`.** Twelve names that the packages
  use internally, but that looked like settings, now have the prefix of the other
  internal names: the targets `_RunicDesktopValidateServer`,
  `_RunicDesktopValidateNativeServer`, `_RunicDesktopResolveNativeServer`,
  `_RunicDesktopLinkNativeServer`, `_RunicDesktopRemoveLinkedNativeServer`,
  `_RunicDesktopRequireRepositoryNativeServer`,
  `_RunicNativeServerRequireStaticLibrary` and
  `_RunicAssetsVerifyEmbeddedArchive`, and the properties
  `_RunicDesktopKestrelReferenced`, `_RunicDesktopNativeServerReferenced`,
  `_RunicNativeServerArtifacts` and `_RunicNativeServerRuntime` (each formerly
  without the leading underscore). [MSBuild properties and items](https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/docs/msbuild.md)
  now says that the targets of the Runic packages are not settings, apart from
  the three documented extension points `RunicViewsGenerateBridge`,
  `RunicViewsBuildFrontend` and `RunicViewsCopyFrontend`: a project that ran a
  target of its own with `BeforeTargets` or `AfterTargets` on one of the renamed
  targets hooks it onto an MSBuild target instead. No documented setting
  changed, and Runic.Desktop still reads the server packages' markers of 0.20
  through 0.21.
