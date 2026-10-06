// Type-checked with the generated clients: named enum types are literal
// unions, so a switch over every case is exhaustive.
import type { DocumentedLayout, DocumentedState } from "./obj/bridge-frontend/generated/documented.js";

export function describeLayout(state: DocumentedState): string {
  const layout: DocumentedLayout = state.layout;
  switch (layout) {
    case "List": return `one row each: ${state.item.title}`;
    case "Grid": return `a grid: ${state.item.title}`;
    default: {
      const unreachable: never = layout;
      return unreachable;
    }
  }
}
