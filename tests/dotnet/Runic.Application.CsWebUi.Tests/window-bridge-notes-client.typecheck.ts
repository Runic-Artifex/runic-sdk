// Compile-only consumer: keep the ordinary TypeScript shape honest.
import { connectNotes, type EditorLease, type SaveAdmission, type TitleReceipt, type TitleSnapshot } from "./window-bridge-notes-client.js";

async function sample(): Promise<void> {
  const notes = await connectNotes();
  const first: EditorLease = notes.editor();
  const peer: EditorLease = notes.editor();
  const firstRelease: () => Promise<void> = first.release;
  const peerRelease: () => Promise<void> = peer.release;
  void [firstRelease, peerRelease];
  await first.mount();
  await peer.mount();
  const snapshot: TitleSnapshot = await first.title();
  const direct: TitleReceipt = await first.setTitle("Draft title");
  // @ts-expect-error A rejected receipt may have no current snapshot.
  const prematureValue: string = direct.current.value;
  void prematureValue;
  if (direct.kind === "applied") {
    const nextVersion: number = direct.current.version;
    void nextVersion;
  }
  const checked = await peer.writeTitle("Revised title", snapshot);
  if (checked.kind === "conflict") await peer.writeTitle("Rebased title", checked.current);
  const admission: SaveAdmission = await first.startSave();
  // @ts-expect-error A rejected admission need not carry a request ID.
  const prematureRequestId: string = admission.requestId;
  void prematureRequestId;
  if (admission.accepted) await notes.saveStatus(admission.requestId);
  const saved: void = await first.save();
  void saved;
  await first.release();
  const stillAvailable: TitleSnapshot = await peer.title();
  void stillAvailable;
  await peer.release();

  // @ts-expect-error Title values must be strings.
  await first.setTitle(42);
  // @ts-expect-error Checked writes require an actual versioned snapshot.
  await peer.writeTitle("Wrong baseline", { value: "Draft" });
  // @ts-expect-error A lease cannot change its server-owned presentation ID.
  first.presentationId = peer.presentationId;
  // @ts-expect-error Operation status requires a request ID.
  await notes.saveStatus();
}

void sample;
