// The app-facing API. Generated clients import their runtime from
// `@runic-artifex/views/generated`; framework bindings import their plumbing from
// `@runic-artifex/views/bindings`.
export {
  BridgeError,
  BridgeOperationUncertainError,
  type BridgeOperationUncertainErrorOptions,
  type BridgeErrorKind,
  type BridgeErrorOptions,
  type BridgeErrorReason,
  type BridgeFailureDetail,
} from "./errors.js";
export { onBridgeDiagnostic, type BridgeDiagnostic, type BridgeDiagnosticCode, type BridgeDiagnosticListener } from "./diagnostics.js";
export { bridgeFailure, bridgeSuccess, isBridgeOutcome, matchCase, type BridgeOutcome } from "./outcome.js";
export type { BridgeValidationMessage, BridgeValidationState } from "./validation.js";
export { waitForBridge, type RunicBridgeClient, type WaitForBridgeOptions } from "./transport.js";
export { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "./connection-state.js";
export {
  createCommandController,
  createViewController,
  type BridgeOutcomeFailure,
  type CommandController,
  type CommandState,
  type ViewConnector,
  type ViewController,
  type ViewControllerOptions,
  type ViewControllerState,
  type ViewSource,
} from "./controller.js";
export type { BridgeConnectOptions, FieldBaseline, FieldWriteOptions, FieldWriteReceipt, ViewClient } from "./connection.js";
export {
  createLatestOperationController,
  createOperationController,
  type LatestOperationController,
  type LatestOperationIntent,
  type LatestOperationState,
  type OperationState,
  type OperationController,
  type OperationControllerOptions,
} from "./operation-controller.js";
export type {
  BridgeOperation,
  BridgeOperationCancelKind,
  BridgeOperationCancelResult,
  BridgeOperationDeliveryFailure,
  BridgeOperationDeliveryFailureKind,
  BridgeOperationStatus,
  BridgeOperationStatusKind,
  BridgeOperationStartOptions,
  BridgeOperationStatusOf,
  BridgeOperationStreamItem,
  BridgeOperationStreamPage,
  BridgeOperationWaitOptions,
  BridgeStreamOperation,
} from "./operations.js";
export type { BridgeInteractionContext, InteractionSurface } from "./interactions.js";
export {
  collectionViewport,
  createCollectionViewportController,
  type CollectionViewport,
  type CollectionViewportController,
  type CollectionViewportOptions,
} from "./viewport.js";

/**
 * A generated content reference: a logical .NET View presented in a ViewModel slot, the
 * counterpart of .NET `PageReference`. Generated `{Name}PageReference` types satisfy it.
 */
export interface PageReference<TClient = unknown> {
  /** The View kind, which selects the component in a `ViewOutlet` registry. */
  readonly kind: string;
  connect(): Promise<TClient>;
}
export {
  formatShortcut,
  isApplePlatform,
  matchesShortcut,
  parseShortcut,
  shortcutText,
  type Shortcut,
  type ShortcutEvent,
  type ShortcutModifier,
  type ShortcutOptions,
} from "./shortcuts.js";
