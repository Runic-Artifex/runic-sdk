// Frontend unit tests against the generated typed mocks: no .NET host, browser
// or DOM. The mock states are typed from the generated clients, so renaming a
// ViewModel member breaks these tests at compile time (`bun run typecheck:test`).
import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { BridgeError } from "@runic-artifex/views";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import { mountContent } from "../src/content.js";
import { EditorWrites } from "../src/editor-writes.js";
import { connectEditor } from "../src/generated/editor.js";
import { mockEditor, type EditorMockState } from "../src/generated/editor.mock.js";
import { pageHome } from "../src/generated/home.js";
import { mockHome } from "../src/generated/home.mock.js";
import { connectShell } from "../src/generated/shell.js";
import { mockShell } from "../src/generated/shell.mock.js";
import { mockDocument } from "../src/generated/document.mock.js";
import { mockSidebar } from "../src/generated/sidebar.mock.js";

const draft: EditorMockState = { title: "Untitled", body: "", isDirty: false, savedMessage: "" };
let bridge: MockBridge;

// Each test installs a new Bridge; generated clients then start from a new page runtime.
beforeEach(() => { bridge = installMockBridge(createMockBridge()); });

test("a rejected field write stops the save that follows it", async () => {
  const editor = mockEditor(bridge, {
    state: draft,
    setters: { setTitle: (_state, title) => ({ isDirty: title !== "Untitled" }) },
    commands: { save: state => ({ isDirty: false, savedMessage: `Saved ${state.title}` }) },
  });
  const client = await connectEditor();
  const reported: unknown[] = [];
  const writes = new EditorWrites(cause => reported.push(cause));

  writes.enqueue(() => client.setTitle("Groceries"));
  await writes.run(() => client.save());
  assert.equal(client.snapshot.savedMessage, "Saved Groceries");
  assert.deepEqual(editor.calls.map(call => call.name), ["editorSnapshot", "editorSetTitle", "editorSave"]);

  editor.failNext("setTitle", { kind: "rejected", message: "Titles are limited to 80 characters." });
  writes.enqueue(() => client.setTitle("x".repeat(81)));
  await assert.rejects(writes.run(() => client.save()), (error: unknown) => error instanceof BridgeError && error.kind === "rejected");
  assert.equal(reported.at(-1) instanceof BridgeError, true);
  assert.equal(editor.calls.filter(call => call.name === "editorSave").length, 1);
  client.dispose();
});

test("saving stays pending until the virtual clock passes the storage delay", async () => {
  mockEditor(bridge, {
    state: { ...draft, title: "Groceries", isDirty: true },
    commands: {
      save: async state => {
        await bridge.sleep(250);
        return { isDirty: false, savedMessage: `Saved ${state.title}` };
      },
    },
  });
  const client = await connectEditor();
  let saved = false;
  const save = client.save().then(() => { saved = true; });

  await bridge.advance(249);
  assert.equal(saved, false);
  await bridge.advance(1);
  await save;
  assert.deepEqual([client.snapshot.isDirty, client.snapshot.savedMessage], [false, "Saved Groceries"]);
  client.dispose();
});

test("the main outlet follows the shell's content and disposes the previous View", async () => {
  mockSidebar(bridge, { id: "1", state: { selected: "Home" } });
  const home = mockHome(bridge, { id: "2", state: { greeting: "Welcome", recentNotes: [] } });
  const document = mockDocument(bridge, { id: "3", state: { activePane: "Editor", currentPane: { kind: "editor", id: "4" } } });
  const shell = mockShell(bridge, { state: { sidebar: { kind: "sidebar", id: "1" }, main: home.reference, dialog: null } });
  const client = await connectShell();
  const mounted: string[] = [];
  const view = (kind: string) => (_host: HTMLElement, content: { dispose(): void }) => {
    mounted.push(kind);
    return () => { mounted.push(`-${kind}`); content.dispose(); };
  };
  const host = { replaceChildren() {} } as unknown as HTMLElement;
  const unmount = mountContent(host, client, "main", { home: view("home"), document: view("document") });

  await bridge.flush();
  assert.deepEqual(mounted, ["home"]);
  shell.update({ main: document.reference });
  await bridge.flush();
  assert.deepEqual(mounted, ["home", "-home", "document"]);
  // The outlet acknowledged each presentation and released the one it replaced.
  assert.deepEqual(home.calls.map(call => call.name), ["content2Snapshot", "content2Mount", "content2Unmount"]);
  unmount();
  client.dispose();
});

test("saved notes arrive as keyed changes that keep unchanged rows", async () => {
  const home = mockHome(bridge, { id: "2", state: { greeting: "Welcome", recentNotes: [
    { title: "Ideas", excerpt: "A notes app" },
    { title: "Groceries", excerpt: "Milk" },
  ] } });
  const client = await pageHome("2").connect();
  const ideas = client.snapshot.recentNotes[0];

  // What NotesLibrary.Record does when "Groceries" is saved again.
  home.batch(() => {
    home.collections.recentNotes.replace("Groceries", { title: "Groceries", excerpt: "Milk, eggs" });
    home.collections.recentNotes.move("Groceries", 0);
  });

  assert.deepEqual(client.snapshot.recentNotes.map(note => note.excerpt), ["Milk, eggs", "A notes app"]);
  assert.equal(client.snapshot.recentNotes[1], ideas);
  client.dispose();
});
