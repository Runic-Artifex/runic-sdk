import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { bridge, dispatch } from "./bridge";
import { dirty, edit, emptyEditor, receive } from "./editor-state";
import type { DocumentsCommand } from "./application.bridge.generated";
import "./style.css";
declare global { interface Window { confirmDocumentClose?: () => Promise<boolean> } }
function Document() {
  const [editor, setEditor] = useState(emptyEditor);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const closeDialog = useRef<HTMLDialogElement>(null);
  const decide = useRef<((allow: boolean) => void) | null>(null);
  const current = useRef({ editor, pending }); current.current = { editor, pending };
  const busy = pending || ["opening", "saving", "launching", "choosing", "revealing"].includes(editor.snapshot?.status ?? "");
  const failure = (value: unknown) => setError(value && typeof value === "object" && "message" in value ? String(value.message) : "Connection failed. Reconnect to recover operation state.");
  useEffect(() => {
    const update = (snapshot: Parameters<typeof receive>[1]) => setEditor(state => receive(state, snapshot));
    const unsubscribe = bridge.subscribe(event => update(event.snapshot), failure);
    void bridge.initialize().then(async snapshot => { update(snapshot); await bridge.uiReady(); }).catch(failure);
    return unsubscribe;
  }, []);
  useEffect(() => {
    const confirm = async () => {
      const { editor, pending } = current.current;
      if (pending || ["opening", "saving", "launching", "choosing", "revealing"].includes(editor.snapshot?.status ?? "")) { setError("Finish or cancel the operation before closing."); return false; }
      if (!dirty(editor)) return true;
      if (decide.current) return false;
      closeDialog.current?.showModal();
      return new Promise<boolean>(resolve => { decide.current = resolve; });
    };
    window.confirmDocumentClose = confirm;
    const protect = (event: BeforeUnloadEvent) => {
      const { editor, pending } = current.current;
      if (dirty(editor) || pending || ["opening", "saving", "launching", "choosing", "revealing"].includes(editor.snapshot?.status ?? "")) { event.preventDefault(); event.returnValue = ""; }
    };
    window.addEventListener("beforeunload", protect);
    return () => { delete window.confirmDocumentClose; window.removeEventListener("beforeunload", protect); decide.current?.(false); };
  }, []);
  async function run(command: DocumentsCommand) {
    setPending(true); setError("");
    try { const result = await dispatch(command); setEditor(state => receive(state, result.snapshot)); }
    catch (error) { failure(error); }
    finally { setPending(false); }
  }
  const close = (allow: boolean) => { closeDialog.current?.close(); decide.current?.(allow); decide.current = null; };
  return <main><h1>Text document</h1><p>UTF-8 · maximum 32 KiB · {dirty(editor) ? "Unsaved changes" : "Saved"}</p>
    <nav aria-label="Document actions">
      <button disabled={busy || dirty(editor)} onClick={() => void run({ _tag: "OpenDocument", revision: editor.revision })}>Open…</button>
      <button disabled={busy} onClick={() => void run({ _tag: "SaveDocument", text: editor.text, revision: editor.revision })}>Save as…</button>
      {([['open', 'Open result'], ['choose', 'Open with…'], ['reveal', 'Show in folder']] as const).map(([action, label]) =>
        <button key={action} disabled={busy || !editor.snapshot?.hasResult} onClick={() => void run({ _tag: "LaunchDocumentResult", action, revision: editor.revision })}>{label}</button>)}
      <button disabled={!editor.snapshot?.operationId || !busy} onClick={() => { const id = editor.snapshot?.operationId; if (id) void bridge.cancel(id).catch(failure); }}>Cancel operation</button>
      <button disabled={busy} onClick={() => { if (!dirty(editor) || window.confirm("Discard unsaved changes?")) setEditor(state => ({ ...edit(state, ""), saved: "" })); }}>New</button>
    </nav>
    <label htmlFor="document">Document text</label><textarea id="document" rows={24} value={editor.text} onChange={event => setEditor(state => edit(state, event.target.value))} />
    <p role="status">{editor.snapshot?.status ?? "Ready"}{editor.snapshot?.name ? ` · ${editor.snapshot.name}` : ""}</p><p role="alert">{error}{editor.snapshot?.cleanupFailed ? " Native access cleanup failed. The reported write outcome still applies; do not retry automatically." : ""}</p>
    <p>Open is disabled while there are unsaved changes. Save them or choose New to discard. Edits remain available while operations run.</p>
    <dialog ref={closeDialog} aria-labelledby="close-title" onCancel={event => { event.preventDefault(); close(false); }}><h2 id="close-title">Discard unsaved document?</h2><p>Your latest edits have not been saved.</p><button autoFocus onClick={() => close(false)}>Keep editing</button><button onClick={() => close(true)}>Discard and close</button></dialog>
  </main>;
}
createRoot(document.getElementById("app")!).render(<Document />);
