// `@runic-artifex/views/bindings`: plumbing for framework bindings such as `@runic-artifex/react`,
// which depend on the exact same release. Applications use a binding or the controllers in
// `@runic-artifex/views` instead.
export { isViewClient, viewSourceIdentity, viewSourcePreview } from "./controller.js";
export { checkKeepAlive, defaultKeepAlive } from "./keep-alive.js";
export { missingViewMessage, reportMissingView } from "./missing-view.js";
export { translationsTextRevision } from "./translated-text.js";
export type { ViewDescription } from "./describe.js";
import { describe, type ViewDescription } from "./describe.js";

/**
 * The View of a connected client, for a binding's DevTools labels: its view model, route and revision. Only the
 * development build (the `development` export condition) describes clients; the production build returns `undefined`.
 */
export function describeViewClient(client: unknown): ViewDescription | undefined {
  return __RUNIC_DEVELOPMENT__ ? describe(client) : undefined;
}
