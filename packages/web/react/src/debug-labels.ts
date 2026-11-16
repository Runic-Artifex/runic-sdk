// The React DevTools labels of the Runic hooks (#1041), in the development build only (`__RUNIC_DEVELOPMENT__`): each hook
// passes its value and one of these functions to `useDebugValue`, and React calls the function only while React
// DevTools inspects the component, so a render pays nothing for the text. The production build does not contain this
// module.
import { describeViewClient } from "@runic-artifex/views/bindings";

// A short, cheap preview of a value: no traversal of objects or arrays.
export function preview(value: unknown): string {
  if (typeof value === "string") return JSON.stringify(value.length > 40 ? `${value.slice(0, 39)}…` : value);
  if (value === null || typeof value !== "object") return typeof value === "function" ? "ƒ" : String(value);
  if (Array.isArray(value)) return `Array(${value.length})`;
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? "Invalid Date" : value.toISOString();
  return "{…}";
}

function errorText(error: unknown): string {
  if (error instanceof Error) return `${error.name}: ${error.message}`;
  const message = (error as { message?: unknown } | null | undefined)?.message;
  return typeof message === "string" ? message : preview(error);
}

/** `NotesViewModel · notes · rev 12`, or the View's connection phase without a client. */
export function viewLabel(view: { readonly client: unknown; readonly pending: boolean; readonly error: unknown }): string {
  if (view.error !== undefined) return `failed: ${errorText(view.error)}`;
  if (view.client === undefined) return view.pending ? "connecting" : "no View";
  return clientLabel(view.client);
}

/** The View of a client, or `View` for a client of the production build of views. */
export function clientLabel(client: unknown): string {
  const view = describeViewClient(client);
  if (view === undefined) return "View";
  return `${view.view} · ${view.route}${view.revision === undefined ? "" : ` · rev ${view.revision}`}${view.released ? " · released" : ""}`;
}

/** `title = "Draft" · editing · 2 errors`. */
export function fieldLabel(name: string, field: {
  readonly value: unknown; readonly editing: boolean; readonly error: unknown; readonly errors?: readonly string[];
  readonly conflict?: unknown; readonly validating?: boolean;
}): string {
  const parts = [`${name} = ${preview(field.value)}`];
  if (field.editing) parts.push("editing");
  if (field.validating === true) parts.push("validating");
  if (field.conflict !== undefined) parts.push("conflict");
  if (field.error !== undefined) parts.push(`failed: ${errorText(field.error)}`);
  const errors = field.errors?.length ?? 0;
  if (errors > 0) parts.push(errors === 1 ? "1 error" : `${errors} errors`);
  return parts.join(" · ");
}

/** `running`, `failed: …`, `declared failure` or `idle`. */
export function commandLabel(command: { readonly pending: boolean; readonly error: unknown; readonly failure: unknown }): string {
  if (command.pending) return "running";
  if (command.error !== undefined) return `failed: ${errorText(command.error)}`;
  return command.failure !== undefined ? "declared failure" : "idle";
}

/** The operation's status: `starting`, `running 40%`, `cancelling`, `succeeded`, `failed: …`, `cancelled`, `idle`. */
export function operationLabel(operation: {
  readonly pending: boolean; readonly admitting: boolean; readonly status: { readonly kind: string } | undefined;
  readonly progress: number | undefined; readonly error: unknown; readonly cancelled: boolean;
  readonly cancellationRequested: boolean; readonly cancelling: boolean; readonly blocked?: boolean;
}): string {
  const suffix = operation.blocked === true ? " · blocked" : "";
  if (operation.admitting) return `starting${suffix}`;
  if (operation.pending) {
    const state = operation.cancelling || operation.cancellationRequested ? "cancelling"
      : operation.progress === undefined ? "running" : `running ${Math.round(operation.progress * 100)}%`;
    return state + suffix;
  }
  if (operation.cancelled) return `cancelled${suffix}`;
  if (operation.error !== undefined) return `failed: ${errorText(operation.error)}${suffix}`;
  return (operation.status?.kind ?? "idle") + suffix;
}
