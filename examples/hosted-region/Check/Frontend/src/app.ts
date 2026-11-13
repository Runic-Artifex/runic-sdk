import { connectCheck } from "./generated/check";

const check = await connectCheck();
const element = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const run = element<HTMLButtonElement>("run");
const outcome = element<HTMLOutputElement>("outcome");

const statusText: Record<string, string> = {
  pending: "Waiting", running: "Checking", passed: "Passed", failed: "Failed", info: "Note",
};
const titles: Record<string, string> = {
  pending: "System check", running: "System check: running", passed: "System check: passed", failed: "System check: problems found",
};

const render = () => {
  const state = check.snapshot;
  for (const [id, status, detail] of [
    ["space", state.space, state.spaceDetail],
    ["folder", state.folder, state.folderDetail],
    ["account", state.account, state.accountDetail],
  ] as const) {
    const cell = element(id);
    cell.dataset.status = status;
    cell.textContent = statusText[status] ?? status;
    element(`${id}-detail`).textContent = detail;
  }
  run.disabled = !state.canRun;
  outcome.value = state.outcome === "passed" ? "Every check passed." : state.outcome === "failed" ? "Some checks failed." : "";
  // In a region Runic reports the title to the guest's host, which names the frame after it (its accessible name) and
  // can show it, as the installer's step indicator does. In a window of its own, the title names the window.
  document.title = titles[state.outcome] ?? "System check";
};
check.subscribe(render);
render();

run.addEventListener("click", async () => { await check.run(); });
