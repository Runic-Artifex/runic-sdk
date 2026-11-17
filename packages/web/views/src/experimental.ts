// The experimental entry (docs/experimental.md, "npm packages"): APIs that may change or be removed in a minor release.
// Importing this entry is the opt-in.
//
// Hosted regions (0.18): a host page shows a hosted guest application's Window in a region of its own document; the
// .NET side is the experimental family RUNICDESK103.
export {
  defineHostedRegion,
  hostedRegionTag,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
  type RunicHostedRegionElement,
} from "./hosted-region.js";

// Trusted component mode (0.18; in `./experimental` since 0.21): a guest's Views as a custom element in a host page's
// own document, the runtime that the `component` build target of `@runic-artifex/vite-plugin-runic` bundles; the .NET
// side is the experimental family RUNICDESK103.
export {
  connectComponentBridge,
  defineRunicComponent,
  type RunicComponentBridge,
  type RunicComponentConnection,
  type RunicComponentContext,
  type RunicComponentDefinition,
  type RunicComponentElement,
  type RunicComponentEndpoint,
  type RunicComponentMount,
  type RunicComponentOptions,
  type RunicComponentStyle,
} from "./component.js";
