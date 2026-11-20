- **`RunicDevelopmentOnlyViews` and `RunicBridgeBlobs` warn on a value other than `auto` or `true` (API-30).** Both
  take `auto` (the default, also when empty) or `true`, compared without case. Until now another value, such as
  `false` or a typo, silently meant `auto`; it is now warning `RUNICBRIDGE075`, and the build still goes on as `auto`.
  Neither has a `false`: development-only views and Bridge blobs can't be left out of a build or an untrimmed
  publish. [MSBuild properties](../../../docs/msbuild.md) now also says that the runtime feature-switch names the
  targets add, such as `Runic.Application.Views.Blobs`, are not settings: set the MSBuild property instead.
