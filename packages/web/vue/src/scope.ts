import { getCurrentScope, onScopeDispose } from "vue";
import { isDevelopment } from "./development.js";


// Ties a composable's controller to the current effect scope. Outside one (not
// in setup() or effectScope().run()) nothing ever releases it, so development
// builds say so.
export function releaseWithScope(composable: string, release: () => void): void {
  if (getCurrentScope()) {
    onScopeDispose(release);
    return;
  }
  if (isDevelopment())
    console.warn(`${composable} was called outside an active effect scope, so nothing releases it: ` +
      "call it in a component's setup() or inside effectScope().run(), and stop that scope to release it.");
}

