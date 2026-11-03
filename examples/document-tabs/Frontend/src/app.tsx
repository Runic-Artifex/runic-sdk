import { createContext, StrictMode, useContext, useState } from "react";
import { createRoot } from "react-dom/client";
import { RegionOutlets, regionEntries, useCommand, useField, useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/react";
import { DocumentFields, type DocumentPageReference } from "./generated/document.js";
import type { DocumentInfoPageReference } from "./generated/documentInfo.js";
import { connectDocuments, type DocumentsState } from "./generated/documents.js";

type TabPage = DocumentsState["documents"]["regions"][number]["content"];

const documents = { connect: connectDocuments };

// Closes the tab that presents a page; App provides it.
const CloseTab = createContext<(page: NonNullable<TabPage>) => Promise<void>>(async () => {});

function App() {
  const { state, client, error } = useView(documents);
  const [notice, setNotice] = useState<string>();
  const newDocument = useCommand(() => client?.newDocument());

  async function close(key: string) {
    if (!client) return;
    const outcome = await client.closeDocuments(key);
    if (outcome.kind === "Committed") {
      setNotice(undefined);
    } else if (outcome.rejection === "Guard") {
      // The document refused to retire: show it, with its Discard and close button.
      setNotice("The document has unsaved changes. Save it, or discard the changes and close it.");
      await client.selectDocuments(key);
    } else {
      setNotice(`The tab was not closed: ${outcome.kind}${outcome.rejection ? ` (${outcome.rejection})` : ""}.`);
    }
  }
  const closePage = async (page: NonNullable<TabPage>) => {
    const tab = regionEntries(state?.documents).find(entry => entry.content === page);
    if (tab) await close(tab.key);
  };

  if (error) return <p role="alert">{String(error)}</p>;
  return <CloseTab.Provider value={closePage}>
    <div role="tablist" aria-label="Documents">
      {regionEntries(state?.documents).map(tab => (
        // The highlight follows `selected`, which changes only when a switch or close commits.
        <span key={tab.key} className="tab" data-selected={tab.selected || undefined}>
          <button role="tab" aria-selected={tab.selected} onClick={() => void client?.selectDocuments(tab.key)}>
            <ViewOutlet content={tab.content} registry={tabTitles} fallback="Empty" />
          </button>
          <button aria-label="Close" onClick={() => void close(tab.key)}>×</button>
        </span>
      ))}
      <button disabled={!state?.canNewDocument || newDocument.pending} onClick={() => void newDocument.run()}>New</button>
    </div>
    {notice && <p role="status">{notice}</p>}
    {/* Every tab stays mounted and only the selected one is shown, so a tab keeps its DOM, such as unsent input. */}
    <RegionOutlets collection={state?.documents} registry={pages} mode="keep-mounted"
      fallback={<p>No open documents. Choose New to open one.</p>} />
  </CloseTab.Provider>;
}

// A tab's label follows the title of the page it shows.
function DocumentTitle({ page }: { page: DocumentPageReference }) {
  const { state } = useView(page);
  return <>{state?.title ?? "…"}{state?.isDirty ? " •" : ""}</>;
}

function DocumentInfoTitle({ page }: { page: DocumentInfoPageReference }) {
  const { state } = useView(page);
  return <>{state?.title ?? "…"}{state?.isDirty ? " •" : ""}</>;
}

const tabTitles = { document: DocumentTitle, documentInfo: DocumentInfoTitle } satisfies ViewRegistry<TabPage>;

function DocumentPage({ page }: { page: DocumentPageReference }) {
  const view = useView(page);
  const { state, client } = view;
  const title = useField(view, DocumentFields.title);
  const body = useField(view, DocumentFields.body);
  const closeTab = useContext(CloseTab);
  const save = useCommand(() => client?.save());
  const details = useCommand(() => client?.showDetails());
  const discardAndClose = useCommand(async () => {
    await client?.discard();
    await closeTab(page);
  });
  if (!state) return <p>Connecting…</p>;
  return <section>
    <label>Title <input {...title.inputProps} /></label>
    <label>Body <textarea rows={10} {...body.inputProps} /></label>
    <div className="actions">
      <button disabled={!state.canSave || save.pending} onClick={() => void save.run()}>Save</button>
      <button disabled={!state.canShowDetails || details.pending} onClick={() => void details.run()}>Details</button>
      {state.isDirty && <>
        <span role="status">Unsaved changes</span>
        <button disabled={discardAndClose.pending} onClick={() => void discardAndClose.run()}>Discard and close</button>
      </>}
    </div>
  </section>;
}

function DocumentInfoPage({ page }: { page: DocumentInfoPageReference }) {
  const { state, client } = useView(page);
  const back = useCommand(() => client?.back());
  if (!state) return <p>Connecting…</p>;
  return <section>
    <h2>{state.title}</h2>
    <dl>
      <dt>Words</dt><dd>{state.words}</dd>
      <dt>Characters</dt><dd>{state.characters}</dd>
      <dt>Saved</dt><dd>{state.saves} times</dd>
    </dl>
    {state.isDirty && <p role="status">Unsaved changes</p>}
    <button disabled={!state.canBack || back.pending} onClick={() => void back.run()}>Back</button>
  </section>;
}

const pages = { document: DocumentPage, documentInfo: DocumentInfoPage } satisfies ViewRegistry<TabPage>;

createRoot(document.querySelector("#app")!).render(<StrictMode><App /></StrictMode>);
