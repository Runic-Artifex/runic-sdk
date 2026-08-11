import { m } from "virtual:runic-translations/setup";
import {
  decodeTextReference,
  formatTextReference,
  type DecodedTextReference,
} from "virtual:runic-translations/setup/transport";

export { m };

const transportHandlers = {
  "Validation.Required": (
    inputs: Readonly<Record<string, unknown>>,
    options?: { locale?: string },
  ) => m["Validation.Required"]({ field: String(inputs.field) }, options),
};

export function localizeReference(value: unknown, locale: string): string {
  const decoded = decodeTextReference(value);
  if (decoded.ok) {
    return formatTextReference(decoded.value, transportHandlers, { locale });
  }
  return fallbackForSkew(value) ?? `Unlocalizable server message (${decoded.reason ?? "invalid"})`;
}

function fallbackForSkew(value: unknown): string | undefined {
  if (!isRecord(value)) return undefined;
  return typeof value.fallbackText === "string" ? value.fallbackText : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export type { DecodedTextReference };
