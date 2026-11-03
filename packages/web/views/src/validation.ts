import * as bridgeWire from "./wire.js";

export interface BridgeValidationMessage {
  readonly path: readonly (string | number)[];
  /** The message; the fallback of `text`. */
  readonly message: string;
  readonly code?: string;
  readonly severity?: string;
  /**
   * The message as a text reference, which `resolveText`, `useText` and the framework helpers show in the window's
   * language, with `message` as the fallback (a producer of 0.16 or later, `translation-text/1`). Absent when the
   * model gave none.
   */
  readonly text?: bridgeWire.TextReference;
}

/**
 * `validation-pending/1` (specs/application/README.md, "Validation"): the producer reports validation in progress
 * at `validation.pending`.
 */
export const validationPendingFeature = "validation-pending/1";

/**
 * `translation-text/1` (specs/application/README.md, "Translated text"): validation errors may carry `text`, and the
 * producer pushes the window's locale.
 */
export const translationTextFeature = "translation-text/1";

export interface BridgeValidationState {
  readonly hasErrors: boolean;
  readonly truncated: boolean;
  readonly errors: readonly BridgeValidationMessage[];
  /**
   * The paths whose validation is in progress, such as an asynchronous rule that is still checking (a producer of
   * 0.16 or later, `validation-pending/1`). Absent when nothing is pending.
   */
  readonly pending?: readonly (readonly (string | number)[])[];
  /** `true` when the producer left out or cut pending paths because of its limits. Absent otherwise. */
  readonly pendingTruncated?: boolean;
}

/** Decodes the `validation` state of a ViewModel with validation. */
export function decodeBridgeValidation(value: unknown): BridgeValidationState {
  return bridgeWire.object(value, state => ({
    hasErrors: bridgeWire.boolean(state["hasErrors"]),
    truncated: bridgeWire.boolean(state["truncated"]),
    errors: bridgeWire.array(state["errors"], value => bridgeWire.object(value, error => ({
      path: bridgeWire.array(error["path"], part => typeof part === "string" ? part : bridgeWire.integer(part, 0, Number.MAX_SAFE_INTEGER)),
      message: bridgeWire.string(error["message"]),
      ...(error["code"] === undefined ? {} : { code: bridgeWire.string(error["code"]) }),
      ...(error["severity"] === undefined ? {} : { severity: bridgeWire.string(error["severity"]) }),
      // translation-text/1: a malformed text leaves the error with its message.
      ...(bridgeWire.isTextReference(error["text"]) ? { text: error["text"] } : {}),
    }))),
    ...(state["pending"] === undefined ? {} : {
      pending: bridgeWire.array(state["pending"], path => bridgeWire.array(path, part =>
        typeof part === "string" ? part : bridgeWire.integer(part, 0, Number.MAX_SAFE_INTEGER))),
    }),
    ...(state["pendingTruncated"] === undefined ? {} : { pendingTruncated: bridgeWire.boolean(state["pendingTruncated"]) }),
  }));
}
