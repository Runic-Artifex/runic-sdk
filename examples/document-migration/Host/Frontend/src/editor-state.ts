import type { DocumentSnapshot } from "./application.bridge.generated";
export interface Editor { text: string; saved: string; revision: number; seen: number; snapshot?: DocumentSnapshot }
export const emptyEditor: Editor = { text: "", saved: "", revision: 0, seen: -1 };
export const dirty = (state: Editor) => state.text !== state.saved;
export const edit = (state: Editor, text: string): Editor => ({ ...state, text, revision: state.revision + 1 });
export function receive(state: Editor, snapshot: DocumentSnapshot): Editor {
  // Reconnect and reordered receipt/event delivery cannot replay a completed open.
  if (snapshot.generation <= state.seen) return state;
  const next = { ...state, snapshot, seen: snapshot.generation };
  if (snapshot.status === "opened" && snapshot.text !== null && snapshot.capturedRevision === state.revision)
    return { ...next, text: snapshot.text, saved: snapshot.text, revision: state.revision + 1 };
  // A successful write clears only the captured content, never edits made while saving.
  if (snapshot.status === "saved" && snapshot.text !== null) return { ...next, saved: snapshot.text };
  return next;
}
