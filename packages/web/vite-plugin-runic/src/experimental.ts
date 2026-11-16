// The experimental entry (docs/experimental.md, "npm packages"): APIs that may change or be removed in a minor release.
// Importing this entry is the opt-in.
//
// The `component` build target (0.18; here since 0.21) of trusted component mode, whose runtime is in
// `@runic-artifex/views/experimental`; the .NET side is the experimental family RUNICDESK103.
import { experimentalComponentOption, type RunicComponentOptions } from "./component.js";
import { runic as stableRunic, type RunicVitePlugin, type RunicViteOptions } from "./index.js";

export type { RunicComponentOptions } from "./component.js";

/** The options of the experimental entry's {@link runic}: the root entry's, and the `component` build target. */
export interface RunicExperimentalViteOptions extends RunicViteOptions {
  /**
   * Adds the `component` build target (trusted component mode): `vite build` also builds the guest's Views as an ES
   * module that defines a custom element, into `_runic_component/` of the build output. Requires base `./`. The
   * development server serves the page only.
   */
  readonly component?: RunicComponentOptions;
}

/** The root entry's `runic`, with the experimental options of {@link RunicExperimentalViteOptions}. */
export function runic(options: RunicExperimentalViteOptions = {}): RunicVitePlugin {
  const { component, ...stable } = options;
  return stableRunic({ ...stable, [experimentalComponentOption]: component } as RunicViteOptions);
}
