import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { Schema } from "effect";
import { bridge, dispatch } from "./bridge";
import {
  CustomerRejected,
  type CustomerSnapshot,
  type ContactData,
} from "./application.bridge.generated";
import {
  dirty,
  emptyEditor,
  importContact,
  receiveSnapshot,
  receiveContactCandidate,
  selectCustomer,
} from "./editor-state";
import "./style.css";

declare global {
  interface Window {
    confirmCustomerClose?: () => Promise<boolean>;
  }
}

function Customers() {
  const [editor, setEditor] = useState(emptyEditor);
  const [search, setSearch] = useState("");
  const [nativePending, setNativePending] = useState(false);
  const [nativeRequest, setNativeRequest] = useState<string>();
  const [candidate, setCandidate] = useState<{ data: ContactData; sequence: number; customerId: string }>();
  const handledNative = useRef<string | undefined>(undefined);
  const nativeTrigger = useRef<HTMLButtonElement | null>(null);
  const [exportAction, setExportAction] = useState<"copy" | "export">();
  const exportDialog = useRef<HTMLDialogElement>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const [destination, setDestination] = useState<string>();
  const dialog = useRef<HTMLDialogElement>(null);
  const closeDialog = useRef<HTMLDialogElement>(null);
  const [closing, setClosing] = useState(false);
  const closeDecision = useRef<{
    promise: Promise<boolean>;
    resolve: (allow: boolean) => void;
  } | null>(null);
  const closeState = useRef({ busy: false, navigating: false });
  const form = useRef<HTMLFormElement>(null);
  const editSequence = useRef(0);
  const current = useRef(editor);
  current.current = editor;
  const busy = pending || editor.snapshot?.save.status === "saving";
  const nativeBusy = nativePending || editor.snapshot?.native.status === "running";
  const changed = dirty(editor);
  closeState.current = { busy: busy || nativeBusy, navigating: !!destination };
  const receive = (snapshot: CustomerSnapshot) =>
    setEditor((state) => receiveSnapshot(state, snapshot));
  const failure = (value: unknown) => {
    setError(
      value && typeof value === "object" && "message" in value
        ? String(value.message)
        : "The connection failed. Reconnect to recover application state.",
    );
    if (Schema.is(CustomerRejected)(value))
      setEditor((state) => ({ ...state, issues: value.issues }));
  };
  useEffect(() => {
    const unsubscribe = bridge.subscribe(
      (event) => receive(event.snapshot),
      failure,
    );
    void bridge
      .initialize()
      .then(async (snapshot) => {
        receive(snapshot);
        await bridge.uiReady();
      })
      .catch(failure);
    return unsubscribe;
  }, []);
  useEffect(() => {
    const protect = (event: BeforeUnloadEvent) => {
      if (changed || busy || nativeBusy) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    window.addEventListener("beforeunload", protect);
    return () => window.removeEventListener("beforeunload", protect);
  }, [changed, busy, nativeBusy]);
  useEffect(() => {
    if (destination) dialog.current?.showModal();
  }, [destination]);

  // Application-specific presentation policy, called through the authenticated Desktop script channel.
  // It never sends drafts into an MVVM adapter or duplicates their ownership in the host.
  useEffect(() => {
    const confirm = async () => {
      if (closeState.current.busy) {
        setError("Finish or cancel the current operation before closing.");
        return false;
      }
      if (!current.current.snapshot || closeState.current.navigating)
        return false;
      if (closeDecision.current) return closeDecision.current.promise;
      if (!dirty(current.current)) return true;
      let resolve!: (allow: boolean) => void;
      const promise = new Promise<boolean>((accept) => {
        resolve = accept;
      });
      closeDecision.current = { promise, resolve };
      setClosing(true);
      return promise;
    };
    window.confirmCustomerClose = confirm;
    return () => {
      if (window.confirmCustomerClose === confirm)
        delete window.confirmCustomerClose;
      closeDecision.current?.resolve(false);
      closeDecision.current = null;
    };
  }, []);
  useEffect(() => {
    if (closing) closeDialog.current?.showModal();
  }, [closing]);
  function finishClose(allow: boolean) {
    closeDialog.current?.close();
    setClosing(false);
    closeDecision.current?.resolve(allow);
    closeDecision.current = null;
  }

  useEffect(() => {
    if (exportAction) exportDialog.current?.showModal();
  }, [exportAction]);
  useEffect(() => {
    const result = editor.snapshot?.native;
    if (!result || result.status === "running" || !result.operationId || result.operationId !== nativeRequest || handledNative.current === result.operationId) return;
    const incoming = receiveContactCandidate(result, nativeRequest, handledNative.current);
    handledNative.current = result.operationId;
    if (incoming) setCandidate(incoming);
    nativeTrigger.current?.focus();
  }, [editor.snapshot, nativeRequest]);
  async function transfer(action: "import" | "paste" | "export" | "copy") {
    const state = current.current;
    if (!state.draft || !state.baseline || nativeBusy || busy) return;
    setNativePending(true);
    setError("");
    setCandidate(undefined);
    try {
      const receipt = await dispatch({ _tag: "TransferContact", action, customerId: state.draft.id, version: state.baseline.version, draftSequence: editSequence.current });
      if (receipt._tag === "NativeContactStarted") {
        setNativeRequest(receipt.operationId);
        receive(receipt.snapshot);
      }
    } catch (value) { failure(value); }
    finally { setNativePending(false); }
  }
  function applyCandidate() {
    if (!candidate || current.current.draft?.id !== candidate.customerId) return;
    ++editSequence.current;
    setEditor(state => ({ ...state, draft: state.draft && { ...state.draft, ...candidate.data }, issues: [] }));
    setCandidate(undefined);
    form.current?.querySelector<HTMLInputElement>("input")?.focus();
  }
  async function validate() {
    const draft = current.current.draft;
    if (!draft || busy) return;
    const sequence = ++editSequence.current;
    try {
      const receipt = await dispatch({ _tag: "ValidateCustomer", draft });
      if (
        sequence === editSequence.current &&
        receipt._tag === "DraftValidated"
      )
        setEditor((state) => ({ ...state, issues: receipt.issues }));
    } catch (value) {
      if (sequence === editSequence.current) failure(value);
    }
  }
  async function save() {
    if (!editor.draft || busy) return;
    ++editSequence.current;
    setPending(true);
    setError("");
    try {
      // Submit the complete draft: Save cannot overtake queued property updates.
      const receipt = await dispatch({
        _tag: "SaveCustomer",
        draft: editor.draft,
      });
      if (receipt._tag === "SaveStarted") receive(receipt.snapshot);
    } catch (value) {
      failure(value);
    } finally {
      setPending(false);
    }
  }
  function navigate(id: string) {
    if (busy || id === editor.draft?.id) return;
    if (changed) {
      setDestination(id);
      return;
    }
    ++editSequence.current;
    setEditor((state) => selectCustomer(state, id));
    setError("");
  }
  function discard(id: string) {
    ++editSequence.current;
    setEditor((state) => selectCustomer(state, id));
    setError("");
    dialog.current?.close();
    setDestination(undefined);
    form.current?.querySelector<HTMLInputElement>("input")?.focus();
  }
  const rows =
    editor.snapshot?.customers.filter((row) =>
      `${row.name} ${row.email} ${row.company}`
        .toLowerCase()
        .includes(search.toLowerCase()),
    ) ?? [];
  return (
    <main>
      <header>
        <div>
          <p className="eyebrow">WORKSPACE</p>
          <h1>
            Customers<span className="dot">.</span>
          </h1>
          <p className="subtitle">Keep your relationships in good order.</p>
        </div>
        <span className="connection">
          {editor.snapshot ? "Connected" : "Connecting…"}
        </span>
      </header>
      <div className="workspace">
        <aside aria-label="Customer directory">
          <label htmlFor="search">Find a customer</label>
          <input
            id="search"
            type="search"
            placeholder="Name, email or company"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <p className="list-heading">
            DIRECTORY <span>{rows.length}</span>
          </p>
          <ul>
            {rows.map((row) => (
              <li key={row.id}>
                <button
                  className={
                    row.id === editor.draft?.id
                      ? "customer selected"
                      : "customer"
                  }
                  aria-current={
                    row.id === editor.draft?.id ? "true" : undefined
                  }
                  disabled={busy}
                  onClick={() => navigate(row.id)}
                >
                  <span className="avatar" aria-hidden="true">
                    {row.name
                      .split(" ")
                      .map((part) => part[0])
                      .slice(0, 2)
                      .join("")}
                  </span>
                  <span>
                    <strong>{row.name}</strong>
                    <small>{row.company || row.email}</small>
                  </span>
                </button>
              </li>
            ))}
          </ul>
          {rows.length === 0 && <p>No customers match your search.</p>}
        </aside>
        <section className="detail" aria-labelledby="detail-title">
          <div className="detail-heading">
            <div>
              <p className="eyebrow">CUSTOMER DETAILS</p>
              <h2 id="detail-title">
                {editor.baseline?.name ?? "Loading customers…"}
              </h2>
            </div>
            <span className={changed ? "badge changed" : "badge"}>
              {changed ? "Unsaved changes" : "Up to date"}
            </span>
          </div>
          <form
            ref={form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
          >
            <fieldset disabled={busy || !editor.draft}>
              <legend className="sr-only">Contact information</legend>
              {(["name", "email", "company"] as const).map((field) => {
                const issue = editor.issues.find(
                  (issue) => issue.field === field,
                );
                return (
                  <div className="field" key={field}>
                    <label htmlFor={field}>
                      {field === "name"
                        ? "Full name"
                        : field === "email"
                          ? "Email address"
                          : "Company"}
                    </label>
                    <input
                      id={field}
                      type={field === "email" ? "email" : "text"}
                      autoComplete={
                        field === "company" ? "organization" : field
                      }
                      maxLength={1000}
                      value={editor.draft?.[field] ?? ""}
                      aria-invalid={Boolean(issue)}
                      aria-describedby={issue ? `${field}-error` : undefined}
                      onChange={(event) => {
                        ++editSequence.current;
                        const value = event.target.value;
                        setEditor((state) => ({
                          ...state,
                          draft: state.draft && {
                            ...state.draft,
                            [field]: value,
                          },
                          issues: state.issues.filter(
                            (issue) => issue.field !== field,
                          ),
                        }));
                        setError("");
                      }}
                      onBlur={() => void validate()}
                    />
                    {issue && (
                      <p id={`${field}-error`} className="field-error">
                        {issue.message}
                      </p>
                    )}
                  </div>
                );
              })}
              <div className="import">
                <label htmlFor="contact">Import contact details (browser JSON file)</label>
                <p>
                  Choose a JSON file with name, email and company. Review the
                  changes before saving.
                </p>
                <input
                  id="contact"
                  type="file"
                  accept=".json,application/json"
                  onChange={(event) => {
                    const file = event.target.files?.[0];
                    event.target.value = "";
                    const draft = current.current.draft;
                    const sequence = ++editSequence.current;
                    if (!file || !draft) return;
                    if (file.size > 4096) {
                      setError("Choose a contact JSON file smaller than 4 KB.");
                      return;
                    }
                    setPending(true);
                    void file
                      .text()
                      .then((text) => {
                        if (editSequence.current !== sequence) return;
                        const imported = importContact(text, draft);
                        setEditor((state) => ({
                          ...state,
                          draft: imported,
                          issues: [],
                        }));
                        setError("");
                      })
                      .catch(failure)
                      .finally(() => setPending(false));
                  }}
                />
              </div>
            </fieldset>
            <section aria-label="Native contact actions">
              <p>Native contacts use the desktop file picker and clipboard. Export and copy use the confirmed saved revision.</p>
              {(["import", "paste", "export", "copy"] as const).map(action => {
                const available = editor.snapshot?.capabilities[action === "import" ? "open" : action === "export" ? "save" : action];
                return <button key={action} type="button" disabled={busy || nativeBusy || !editor.draft || !available}
                  onClick={event => { nativeTrigger.current = event.currentTarget; if (action === "export" || action === "copy") setExportAction(action); else void transfer(action); }}>
                  {action === "import" ? "Import native contact" : action === "paste" ? "Paste contact" : action === "export" ? "Export saved contact" : "Copy saved contact"}
                </button>;
              })}
              {!editor.snapshot?.capabilities.open && <p>Native services unavailable in this presentation. Use the explicit browser JSON import above.</p>}
              <p role="status" aria-live="polite">{editor.snapshot?.native.message}</p>
              {editor.snapshot?.native.cleanupFailed && <p role="alert">The native write outcome above is retained, but resource cleanup failed. Inspect the destination before retrying.</p>}
              {nativeBusy && <button type="button" disabled={nativePending || !editor.snapshot?.native.operationId} onClick={() => { const id = editor.snapshot?.native.operationId; if (id) void bridge.cancel(id).catch(failure); }}>Cancel contact operation</button>}
              {candidate && <div role="region" aria-label="Review imported contact">
                <p>{candidate.sequence !== editSequence.current ? "Your draft changed while this contact was loading. Apply it only if you want to replace your current fields." : "Review this contact before applying it to your draft."}</p>
                <p>{candidate.data.name} · {candidate.data.email} · {candidate.data.company}</p>
                <button type="button" disabled={busy || current.current.draft?.id !== candidate.customerId} onClick={applyCandidate}>Apply contact to draft</button>
                <button type="button" onClick={() => setCandidate(undefined)}>Dismiss imported contact</button>
              </div>}
            </section>
            {error && (
              <div role="alert" className="error">
                {error}
              </div>
            )}
            <div className="operation" role="status" aria-live="polite">
              <span>
                {editor.snapshot?.save.message ??
                  "Connecting to your workspace"}
              </span>
              {busy && (
                <progress
                  aria-label="Save progress"
                  max={100}
                  value={editor.snapshot?.save.progress ?? 0}
                />
              )}
            </div>
            <footer>
              <button
                type="button"
                disabled={busy || !changed}
                onClick={() => setDestination(editor.draft?.id)}
              >
                Discard changes
              </button>
              <div>
                {busy && (
                  <button
                    type="button"
                    disabled={pending || editor.snapshot?.save.status !== "saving" || !editor.snapshot.save.operationId}
                    onClick={() => {
                      const id = editor.snapshot?.save.operationId;
                      if (id) void bridge.cancel(id).catch(failure);
                    }}
                  >
                    Cancel save
                  </button>
                )}
                <button
                  className="primary"
                  type="submit"
                  disabled={busy || !changed}
                >
                  {busy ? "Saving…" : "Save customer"}
                </button>
              </div>
            </footer>
          </form>
        </section>
      </div>
      <div className="bottom">
        <span>Customer workspace</span>
        <button
          disabled={pending}
          onClick={() => {
            ++editSequence.current;
            setPending(true);
            void bridge
              .reconnect()
              .then(receive, failure)
              .finally(() => setPending(false));
          }}
        >
          Reconnect
        </button>
      </div>
      <dialog ref={exportDialog} aria-labelledby="export-title" onCancel={() => setExportAction(undefined)}>
        <h2 id="export-title">Confirm saved contact</h2>
        <p>{editor.baseline?.name} · {editor.baseline?.email} · {editor.baseline?.company} · revision {editor.baseline?.version}</p>
        <p>Unsaved draft edits are not included.</p>
        <button autoFocus onClick={() => { exportDialog.current?.close(); setExportAction(undefined); }}>Keep editing</button>
        <button onClick={() => { const action = exportAction; exportDialog.current?.close(); setExportAction(undefined); if (action) void transfer(action); }}>Confirm {exportAction}</button>
      </dialog>
      <dialog
        ref={closeDialog}
        aria-labelledby="close-title"
        onCancel={(event) => {
          event.preventDefault();
          finishClose(false);
        }}
      >
        <h2 id="close-title">Close without saving?</h2>
        <p>Your unsaved edits will be lost.</p>
        <div className="dialog-actions">
          <button autoFocus onClick={() => finishClose(false)}>
            Keep editing
          </button>
          <button className="primary" onClick={() => finishClose(true)}>
            Discard and close
          </button>
        </div>
      </dialog>
      <dialog
        ref={dialog}
        onCancel={() => setDestination(undefined)}
        aria-labelledby="discard-title"
      >
        <h2 id="discard-title">Discard your changes?</h2>
        <p>Your unsaved edits will be lost.</p>
        <div className="dialog-actions">
          <button
            autoFocus
            onClick={() => {
              dialog.current?.close();
              setDestination(undefined);
            }}
          >
            Keep editing
          </button>
          <button
            className="primary"
            onClick={() => destination && discard(destination)}
          >
            Discard changes
          </button>
        </div>
      </dialog>
    </main>
  );
}
createRoot(document.getElementById("app")!).render(<Customers />);
window.addEventListener("pagehide", () => void bridge.dispose(), {
  once: true,
});
