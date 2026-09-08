// Exercise the exported SDK transport, not the legacy webui.js compatibility bridge.
import { createDesktopFrameChannel } from "../../../../packages/web/desktop/src/index.ts";

const state = {
  documentId: crypto.randomUUID(),
  calls: 0,
  pending: false,
  allow: false,
  quick: 0,
  error: "",
  resolve: undefined as ((allow: boolean) => void) | undefined,
};
const documentMarker = document.createElement("output");
documentMarker.id = "runic-native-document";
documentMarker.textContent = state.documentId;
document.body.append(documentMarker);
Object.assign(globalThis, {
  __runicCloseProbe: state,
  __runicConfirmClose: async () => {
    state.calls++;
    if (state.allow) return true;
    state.pending = true;
    return await new Promise<boolean>((resolve) => {
      state.resolve = (allow) => {
        state.pending = false;
        state.resolve = undefined;
        resolve(allow);
      };
    });
  },
});
const channel = createDesktopFrameChannel();
void channel.reconnect().catch(error => { state.error = String(error); });
