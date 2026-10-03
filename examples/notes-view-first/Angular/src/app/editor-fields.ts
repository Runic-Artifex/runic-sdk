import type { EditorState, EditorClient } from "../../../Frontend/src/generated/editor.js";

// App-local stand-in for descriptors the Bridge generator could emit.
export const editorFields = {
  title: (view: EditorClient, value: string) => view.setTitle(value),
  body: (view: EditorClient, value: string) => view.setBody(value),
} satisfies { [K in "title" | "body"]: (view: EditorClient, value: EditorState[K]) => Promise<unknown> };
