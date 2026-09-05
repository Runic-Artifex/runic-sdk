import type {
  CustomerInput,
  CustomerRow,
  CustomerSnapshot,
  ValidationIssue,
} from "./application.bridge.generated";

export interface EditorState {
  snapshot?: CustomerSnapshot;
  baseline?: CustomerRow;
  draft?: CustomerInput;
  issues: readonly ValidationIssue[];
}
export const emptyEditor: EditorState = { issues: [] };
export const dirty = (state: EditorState): boolean =>
  Boolean(
    state.draft &&
    state.baseline &&
    ["name", "email", "company"].some(
      (key) => state.draft![key as "name"] !== state.baseline![key as "name"],
    ),
  );
export function selectCustomer(state: EditorState, id: string): EditorState {
  const row = state.snapshot?.customers.find((customer) => customer.id === id);
  return row
    ? { ...state, baseline: row, draft: { ...row }, issues: [] }
    : state;
}
export function receiveSnapshot(
  state: EditorState,
  snapshot: CustomerSnapshot,
): EditorState {
  // A progress event can arrive before the receipt's promise is observed.
  if (state.snapshot && snapshot.generation < state.snapshot.generation)
    return state;
  const row =
    snapshot.customers.find((customer) => customer.id === state.draft?.id) ??
    snapshot.customers[0];
  const matchesSavedDraft =
    row &&
    state.draft &&
    snapshot.save.status === "saved" &&
    row.version > state.draft.version &&
    row.name === state.draft.name.trim() &&
    row.email === state.draft.email.trim() &&
    row.company === state.draft.company.trim();
  const replaceDraft = !dirty(state) || matchesSavedDraft;
  return {
    snapshot,
    baseline: replaceDraft ? row : state.baseline,
    draft: replaceDraft && row ? { ...row } : state.draft,
    issues:
      snapshot.save.status === "failed"
        ? snapshot.save.issues
        : replaceDraft
          ? []
          : state.issues,
  };
}
export function importContact(
  text: string,
  draft: CustomerInput,
): CustomerInput {
  if (new TextEncoder().encode(text).length > 4096)
    throw new Error("Choose a contact JSON file smaller than 4 KB.");
  const value: unknown = JSON.parse(text);
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("The contact must be a JSON object.");
  const contact = value as Record<string, unknown>;
  for (const field of ["name", "email", "company"]) {
    if (typeof contact[field] !== "string" || contact[field].length > 1000)
      throw new Error(
        `The contact needs a ${field} text field (up to 1000 characters).`,
      );
  }
  // Import only editable fields. Files cannot change the record identity/version.
  return {
    ...draft,
    name: contact.name as string,
    email: contact.email as string,
    company: contact.company as string,
  };
}
