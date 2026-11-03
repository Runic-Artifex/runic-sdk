import { createQueryViewportController, type RunicFilter, type RunicQuery } from "@runic-artifex/views";
import { connectCatalog } from "./src/generated/catalog.js";

const catalog = await connectCatalog();
const products = catalog.queries.products;
const scroll = document.querySelector<HTMLDivElement>("#scroll")!;
const spacer = document.querySelector<HTMLDivElement>("#spacer")!;
const status = document.querySelector<HTMLDivElement>("#status")!;
const search = document.querySelector<HTMLInputElement>("#search")!;
const sort = document.querySelector<HTMLSelectElement>("#sort")!;
const inStock = document.querySelector<HTMLInputElement>("#inStock")!;
const goTo = document.querySelector<HTMLInputElement>("#goto")!;

// Rows are laid out by their measured heights; 56 px until a row was measured.
const viewport = createQueryViewportController(products, { estimateSize: 56, overscan: 6 });
// Rendered rows by key, or by position while a row is a placeholder, so a row keeps its element when it moves.
const nodes = new Map<string, { element: HTMLDivElement; index: number; stop: () => void; content: unknown }>();
const keyOf = (item: { readonly key: string | undefined; readonly index: number }) => item.key ?? `p${item.index}`;

function render(): void {
  const current = viewport.current;
  spacer.style.height = `${current.totalSize}px`;
  const shown = new Set(current.items.map(keyOf));
  for (const [key, node] of nodes) if (!shown.has(key)) { node.stop(); node.element.remove(); nodes.delete(key); }
  for (const item of current.items) {
    let node = nodes.get(keyOf(item));
    if (!node) {
      const element = spacer.appendChild(document.createElement("div"));
      node = { element, index: item.index, stop: viewport.observe(element, item.index), content: undefined };
      nodes.set(keyOf(item), node);
    } else if (node.index !== item.index) {
      // The row moved: its height now measures its new position.
      node.stop();
      node.index = item.index;
      node.stop = viewport.observe(node.element, item.index);
    }
    node.element.style.top = `${item.top}px`;
    node.element.className = `row${item.row ? "" : " placeholder"}${item.stale ? " stale" : ""}`;
    if (node.content === item.row) continue;
    node.content = item.row;
    const product = item.row;
    node.element.innerHTML = product
      ? `<button data-restock="${product.id}">Restock</button><b>#${product.id} ${product.name}</b> · ${product.category} · ${product.price} € · ${product.stock} in stock<small>${product.notes}</small>`
      : item.status === "error" ? "Could not load this row." : "Loading…";
  }
  status.textContent = `${current.total === null ? "Counting…" : `${current.total.toLocaleString()} products`}${current.loading ? " · loading" : ""}`
    + (current.error ? ` · ${current.error.message}` : "");
}
viewport.subscribe(render);
viewport.attach(scroll);
render();

// The query is ViewModel state: another page of the window sees the same sort and filter.
function query(): RunicQuery {
  const filters: RunicFilter[] = [];
  if (search.value.trim()) filters.push({ field: "name", op: "contains", value: search.value.trim() });
  if (inStock.checked) filters.push({ field: "inStock", op: "custom" });
  const key = sort.value;
  return { sort: key ? [{ field: key.replace("-", ""), descending: key.startsWith("-") }] : [], filters };
}
let typing: ReturnType<typeof setTimeout> | undefined;
const apply = () => { void products.setQuery(query()).catch(error => { status.textContent = String(error); }); };
search.addEventListener("input", () => { clearTimeout(typing); typing = setTimeout(apply, 250); });
sort.addEventListener("change", apply);
inStock.addEventListener("change", apply);
document.querySelector<HTMLFormElement>("#query")!.addEventListener("submit", event => event.preventDefault());
// Scroll to a key: .NET finds its position in the current query.
goTo.addEventListener("change", async () => {
  if (!(await viewport.scrollToKey(goTo.value))) status.textContent = `#${goTo.value} is not in this view.`;
});
spacer.addEventListener("click", event => {
  const id = (event.target as HTMLElement).dataset["restock"];
  if (id) void catalog.restock(Number(id));
});
document.querySelector<HTMLButtonElement>("#add")!.addEventListener("click", () => { void catalog.add(); });
window.addEventListener("pagehide", () => { viewport.dispose(); catalog.dispose(); }, { once: true });
