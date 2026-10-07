// Type-checked with the generated clients: named enum types are literal
// unions, so a switch over every case is exhaustive.
import type { CodegenShapeState } from "./obj/bridge-frontend/generated/codegenShape.js";
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

// A generic DTO member declared T is nullable only where the C# type
// argument is: CodegenSlot<string> versus CodegenSlot<string?>.
export function requiredValue(state: CodegenShapeState): string {
  return state.required.value;
}

export const optionalSlot: CodegenShapeState["optional"] = { value: null, fallback: null, items: [null, "item"] };

// @ts-expect-error CodegenSlot<string> does not accept null.
export const requiredSlot: CodegenShapeState["required"] = { value: null, fallback: null, items: [] };
