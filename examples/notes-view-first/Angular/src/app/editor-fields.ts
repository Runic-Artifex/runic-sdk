import type { EditorState, EditorView } from "../../../Frontend/src/generated/editor.js";

// App-local stand-in for descriptors the Bridge generator could emit.
export const editorFields = {
  title: (view: EditorView, value: string) => view.setTitle(value),
  body: (view: EditorView, value: string) => view.setBody(value),
} satisfies { [K in "title" | "body"]: (view: EditorView, value: EditorState[K]) => Promise<unknown> };
