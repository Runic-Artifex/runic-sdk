export { BridgeError, BridgeOperationUncertainError, type BridgeErrorKind } from "./errors.js";
export { bridgeWire, decodeBridgeValidation, type BridgeValidationMessage, type BridgeValidationState } from "./wire.js";
export { waitForBridge, type RunicBridgeClient } from "./transport.js";
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
  BridgeStreamOperation,
} from "./operations.js";
export type { BridgeInteractionContext, InteractionDefinition, InteractionSurface } from "./interactions.js";

/** A generated content reference: a logical .NET View presented in a ViewModel slot. */
export interface ViewReference<TClient = unknown> {
  readonly kind: string;
  connect(): Promise<TClient>;
}
