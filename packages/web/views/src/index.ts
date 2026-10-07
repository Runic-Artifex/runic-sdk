// The app-facing API. Generated clients import their runtime from
// `@runic-artifex/views/generated`.
export {
  BridgeError,
  BridgeOperationUncertainError,
  type BridgeErrorKind,
  type BridgeErrorOptions,
  type BridgeFailureDetail,
} from "./errors.js";
export { onBridgeDiagnostic, type BridgeDiagnostic, type BridgeDiagnosticListener } from "./diagnostics.js";
export { bridgeFailure, bridgeSuccess, isBridgeOutcome, matchCase, type BridgeOutcome } from "./outcome.js";
export type { BridgeValidationMessage, BridgeValidationState } from "./validation.js";
export { waitForBridge, type RunicBridgeClient, type WaitForBridgeOptions } from "./transport.js";
export {
  collectionViewport,
  createCollectionViewportController,
  type CollectionViewport,
  type CollectionViewportController,
  type CollectionViewportOptions,
} from "./viewport.js";
export {
  createCommandController,
  createViewController,
  isViewClient,
  viewSourceIdentity,
  type BridgeOutcomeFailure,
  type CommandController,
  type CommandState,
  type ViewConnector,
  type ViewController,
  type ViewControllerOptions,
  type ViewControllerState,
  type ViewSource,
} from "./controller.js";
export type { FieldBaseline, FieldWriteOptions, FieldWriteReceipt, ViewClient } from "./connection.js";
export type {
  BridgeOperation,
  BridgeOperationCancelKind,
  BridgeOperationCancelResult,
  BridgeOperationDelivery,
  BridgeOperationDeliveryKind,
  BridgeOperationStatus,
  BridgeOperationStatusKind,
  BridgeOperationStatusOf,
  BridgeOperationStreamItem,
  BridgeOperationStreamPage,
  BridgeOperationWaitOptions,
  BridgeStreamOperation,
} from "./operations.js";
export type { BridgeInteractionContext, InteractionSurface } from "./interactions.js";

/** A generated content reference: a logical .NET View presented in a ViewModel slot. */
export interface ViewReference<TClient = unknown> {
  readonly kind: string;
  connect(): Promise<TClient>;
}
