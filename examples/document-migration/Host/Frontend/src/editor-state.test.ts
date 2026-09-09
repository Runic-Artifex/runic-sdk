import { expect, test } from "bun:test";
import { dirty, edit, emptyEditor, receive } from "./editor-state";
const result = (status: string, generation: number, text: string, capturedRevision = 0) => ({ status, generation, text, capturedRevision, operationId: null, name: "document.txt", cleanupFailed: false, hasResult: false });
test("late open does not overwrite an edit", () => {
  const state = receive(edit(emptyEditor, "new edit"), result("opened", 1, "disk"));
  expect(state.text).toBe("new edit"); expect(dirty(state)).toBe(true);
});
test("captured save preserves edits made while pending", () => {
  const state = receive(edit(edit(emptyEditor, "saved revision"), "newer edit"), result("saved", 2, "saved revision", 1));
  expect(state.text).toBe("newer edit"); expect(state.saved).toBe("saved revision"); expect(dirty(state)).toBe(true);
});
test("reconnect cannot replay an open or older receipt", () => {
  const opened = receive(emptyEditor, result("opened", 2, "disk"));
  const edited = edit(opened, "draft");
  expect(receive(edited, result("opened", 2, "disk"))).toEqual(edited);
  expect(receive(edited, result("opening", 1, ""))).toEqual(edited);
});
test("uncertain commits never mark draft saved", () => {
  const state = receive(edit(emptyEditor, "draft"), result("commit-unknown:IoError", 2, ""));
  expect(dirty(state)).toBe(true);
});
