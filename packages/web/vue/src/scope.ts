import { getCurrentScope, onScopeDispose } from "vue";

// Vue's bundler builds require bundlers to define process.env.NODE_ENV too.
declare const process: { readonly env: { readonly NODE_ENV?: string } };

// Ties a composable's controller to the current effect scope. Outside one (not
// in setup() or effectScope().run()) nothing ever releases it, so development
// builds say so.
export function releaseWithScope(composable: string, release: () => void): void {
  if (getCurrentScope()) {
    onScopeDispose(release);
    return;
  }
  if (process.env.NODE_ENV !== "production")
    console.warn(`${composable} was called outside an active effect scope, so nothing releases it: ` +
      "call it in a component's setup() or inside effectScope().run(), and stop that scope to release it.");
}

