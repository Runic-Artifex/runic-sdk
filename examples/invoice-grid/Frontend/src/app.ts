// An editable invoice: every address member and every cell of the lines is a field of its own, bound to a generated
// descriptor (InvoiceFields.address.street, InvoiceFields.lines.at(id).quantity). Each field keeps its draft while
// the user types, writes it after a pause or on blur, shows the validation messages .NET reports for it, and stops
// with a conflict when someone else changed the same cell meanwhile. No command per cell: the ViewModel's
// [RunicRowEditor] method applies a line's changed cell.
import { bindField, type BoundField } from "@runic-artifex/views";
import { connectInvoice, InvoiceFields, type InvoiceClient, type InvoiceState } from "./generated/invoice.js";

const status = document.querySelector<HTMLElement>("#status")!;
const body = document.querySelector<HTMLTableSectionElement>("#lines")!;
const total = document.querySelector<HTMLElement>("#total")!;
const add = document.querySelector<HTMLButtonElement>("#add")!;

type Field = BoundField<InvoiceState, InvoiceClient, unknown>;

// A field's messages, and a conflict with the choice to keep the draft (retry) or take the other value (reset).
function annotate(cell: HTMLElement, field: Field): () => void {
  const errors = cell.appendChild(document.createElement("div"));
  errors.className = "cell-error";
  const conflict = cell.appendChild(document.createElement("div"));
  conflict.className = "conflict";
  const show = () => {
    const state = field.current;
    errors.textContent = [...state.errors, ...state.error instanceof Error && state.conflict === undefined ? [state.error.message] : []].join(" ");
    conflict.replaceChildren();
    if (state.conflict === undefined) return;
    conflict.append(`Changed meanwhile to ${field.format(state.conflict.value)}. `);
    const keep = conflict.appendChild(document.createElement("button"));
    keep.textContent = "Keep mine";
    keep.onclick = () => field.retry();
    const take = conflict.appendChild(document.createElement("button"));
    take.textContent = "Take theirs";
    take.onclick = () => field.reset();
  };
  show();
  return field.subscribe(show);
}

try {
  const invoice = await connectInvoice();
  const fields: Field[] = [];
  const bind = <T>(element: HTMLInputElement, descriptor: Parameters<typeof bindField<InvoiceClient, T>>[2]) => {
    const field = bindField(element, invoice, descriptor, { commit: "pause", locale: navigator.languages }) as Field;
    fields.push(field);
    return field;
  };
  for (const [id, descriptor] of [["street", InvoiceFields.address.street], ["postal-code", InvoiceFields.address.postalCode],
    ["city", InvoiceFields.address.city]] as const) {
    const input = document.querySelector<HTMLInputElement>(`#${id}`)!;
    annotate(input.parentElement!, bind(input, descriptor));
  }

  // One row per line key; a row's cells stay bound while the line exists, also when it moves.
  const rows = new Map<number, { readonly element: HTMLTableRowElement; readonly dispose: () => void }>();
  function render(state: InvoiceState): void {
    total.textContent = state.total;
    add.disabled = !state.canAddLine;
    const keys = new Set(state.lines.map(line => line.id));
    for (const [id, row] of rows) if (!keys.has(id)) { row.dispose(); row.element.remove(); rows.delete(id); }
    for (const line of state.lines) {
      let row = rows.get(line.id);
      if (row === undefined) {
        const element = document.createElement("tr");
        const cells = InvoiceFields.lines.at(line.id);
        const bound: Field[] = [];
        const cleanups: (() => void)[] = [];
        for (const [descriptor, numeric, label] of [[cells.product, false, "Product"], [cells.quantity, true, "Quantity"],
          [cells.price, true, "Price"]] as const) {
          const cell = element.appendChild(document.createElement("td"));
          if (numeric) cell.className = "number";
          const input = cell.appendChild(document.createElement("input"));
          input.setAttribute("aria-label", `${label} of line ${line.id}`);
          if (numeric) input.inputMode = "decimal";
          const field = bindField(input, invoice, descriptor as Parameters<typeof bindField<InvoiceClient, unknown>>[2],
            { commit: "pause", locale: navigator.languages }) as Field;
          bound.push(field);
          cleanups.push(annotate(cell, field));
        }
        const remove = element.appendChild(document.createElement("td")).appendChild(document.createElement("button"));
        remove.textContent = "Remove";
        remove.onclick = () => { void invoice.removeLine(line.id); };
        row = {
          element,
          dispose() { for (const cleanup of cleanups) cleanup(); for (const field of bound) field.dispose(); },
        };
        rows.set(line.id, row);
      }
      body.appendChild(row.element);
    }
  }
  invoice.subscribe(render);
  add.onclick = () => { void invoice.addLine(); };
  status.textContent = "Connected to the .NET ViewModel.";
} catch (error) {
  status.textContent = `Connection failed: ${String(error)}`;
}
