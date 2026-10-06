export {
  BridgeError,
  BridgeOperationUncertainError,
  type BridgeErrorKind,
  type BridgeErrorOptions,
  type BridgeFailureDetail,
} from "./errors.js";
export { onBridgeDiagnostic, type BridgeDiagnostic, type BridgeDiagnosticListener } from "./diagnostics.js";
export { bridgeWire, decodeBridgeValidation, type BridgeValidationMessage, type BridgeValidationState } from "./wire.js";
export { waitForBridge, type RunicBridgeClient, type WaitForBridgeOptions } from "./transport.js";
export { defineCollection, type BridgeCollectionDefinition } from "./collections.js";
export { collectionViewport, type CollectionViewport } from "./viewport.js";
export {
  connectView,
  viewReferences,
  type FieldBaseline,
  type FieldWriteOptions,
  type FieldWriteReceipt,
  type ViewClient,
  type ViewConnection,
  type ViewConnectOptions,
} from "./connection.js";
export type {
  BridgeOperation,
  BridgeOperationCancelKind,
  BridgeOperationCancelResult,
  BridgeOperationDeliveryKind,
  BridgeOperationStatus,
  BridgeOperationStatusKind,
  BridgeOperationStreamItem,
  BridgeOperationStreamPage,
  BridgeOperationWaitOptions,
  BridgeStreamOperation,
} from "./operations.js";
export type { BridgeInteractionContext, InteractionDefinition, InteractionSurface } from "./interactions.js";

/** A generated content reference: a logical .NET View presented in a ViewModel slot. */
export interface ViewReference<TClient = unknown> {
  readonly kind: string;
  connect(): Promise<TClient>;
}

// CI measurement: views-only change, do not merge.
