import { collectionViewport } from "@runic-artifex/views";
import { connectRows } from "./src/generated/rows.js";

const view = await connectRows();
const scroll = document.querySelector<HTMLDivElement>("#scroll")!;
const spacer = document.querySelector<HTMLDivElement>("#spacer")!;
const rows = document.querySelector<HTMLDivElement>("#rows")!;
const status = document.querySelector<HTMLDivElement>("#status")!;
const nodes = new Map<number, { element: HTMLDivElement; value: unknown }>();
let frames = 0;
let requested = "";
let pending: { start: number; size: number } | undefined;
let sending = false;

async function sendViewport() {
  if (sending) return;
  sending = true;
  try {
    while (pending) { const request = pending; pending = undefined; await view.setViewport(request); }
  } finally { sending = false; }
}
function requestViewport() {
  const viewport = collectionViewport({ totalCount: view.snapshot.totalCount, scrollTop: scroll.scrollTop, height: scroll.clientHeight, rowHeight: 32 });
  const key = `${viewport.start}:${viewport.size}`;
  if (viewport.size === 0 || key === requested) return;
  requested = key;
  pending = { start: viewport.start, size: viewport.size };
  void sendViewport().catch(error => { status.textContent = String(error); });
}
view.subscribe(state => {
  frames++;
  spacer.style.height = `${state.totalCount * 32}px`;
  rows.style.transform = `translateY(${state.start * 32}px)`;
  const active = new Set(state.rows.map(row => row.id));
  for (const [id, node] of nodes) if (!active.has(id)) { node.element.remove(); nodes.delete(id); }
  let previous: HTMLElement | undefined;
  for (const row of state.rows) {
    let node = nodes.get(row.id);
    if (!node) { node = { element: document.createElement("div"), value: undefined }; node.element.className = "row"; nodes.set(row.id, node); }
    if (node.value !== row) { node.element.textContent = `${row.label} · value ${row.value}`; node.value = row; }
    const before = previous ? previous.nextSibling : rows.firstChild;
    if (before !== node.element) rows.insertBefore(node.element, before);
    previous = node.element;
  }
  status.textContent = `${state.totalCount.toLocaleString()} cached · ${state.rows.length} presented · ${frames} bridge frames`;
  requestViewport();
});
let scheduled = false;
scroll.addEventListener("scroll", () => {
  if (scheduled) return;
  scheduled = true;
  requestAnimationFrame(() => { scheduled = false; requestViewport(); });
});
document.querySelector<HTMLButtonElement>("#update")!.addEventListener("click", () => { void view.update().catch(error => { status.textContent = String(error); }); });
window.addEventListener("pagehide", () => view.dispose(), { once: true });
