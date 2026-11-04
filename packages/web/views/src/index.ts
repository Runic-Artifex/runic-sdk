/// <reference lib="esnext.disposable" preserve="true" />
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
export { describeBridgeError, isBridgeError } from "./describe-error.js";
export { onBridgeDiagnostic, type BridgeDiagnostic, type BridgeDiagnosticCode, type BridgeDiagnosticListener } from "./diagnostics.js";
export { bridgeFailure, bridgeSuccess, isBridgeOutcome, matchCase, type BridgeOutcome } from "./outcome.js";
export type { BridgeValidationMessage, BridgeValidationState } from "./validation.js";
export {
  configureTranslatedText,
  onWindowLocale,
  resolveText,
  subscribeText,
  textLocale,
  textVersion,
  windowLocale,
  type TextReference,
  type TextTranslations,
} from "./translated-text.js";
export { waitForBridge, type RunicBridgeClient, type WaitForBridgeOptions } from "./transport.js";
export {
  blobsFeature,
  upload,
  type BlobRef,
  type HostUploadRequest,
  type HostUploads,
  type RunicBlob,
  type UploadOptions,
  type UploadProgress,
} from "./blobs.js";
export { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "./connection-state.js";
export { installErrorReporting, type ClientErrorReport, type ClientErrorSource, type ErrorReportingOptions } from "./error-reporting.js";
export {
  createCommandController,
  createViewController,
  preloadView,
  type PreloadViewOptions,
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
export type { BridgeField, BridgeFieldConstraints, BridgeFieldKind } from "./field.js";
export { fieldErrorsAt, fieldValidatingAt, type BridgeRowFields } from "./field-paths.js";
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
export type { BridgeViewRequestContext, BridgeViewRequestHandler, ViewRequestSurface } from "./requests.js";
export {
  commitAllFields,
  createFieldController,
  FieldError,
  type FieldCommit,
  type FieldController,
  type FieldDefinition,
  type FieldErrorKind,
  type FieldOptions,
  type FieldSource,
  type FieldState,
} from "./field-controller.js";
export { attachField, bindField, commitFields, type BoundField, type FieldElement } from "./dom-field.js";
export {
  createFormController,
  formFieldList,
  formStateOf,
  submitFields,
  type FormController,
  type FormField,
  type FormFieldController,
  type FormFields,
  type FormFieldState,
  type FormOptions,
  type FormSubmitOptions,
  type FormState,
  type FormSubmitResult,
} from "./form-controller.js";
export {
  createTextFieldController,
  type TextFieldController,
  type TextFieldControllerOptions,
  type TextFieldSource,
  type TextFieldState,
} from "./text-field.js";
export {
  collectionViewport,
  createCollectionViewportController,
  type CollectionViewport,
  type CollectionViewportController,
  type CollectionViewportOptions,
} from "./viewport.js";
export {
  createQueryCollection,
  maximumQueryRange,
  normalizeQuery,
  type QueryCollection,
  type QueryCollectionQuery,
  type QueryCollectionSnapshot,
  type QueryCollectionState,
  type QueryRow,
  type RunicFilter,
  type RunicQuery,
  type RunicSortKey,
} from "./query.js";
export {
  createQueryViewportController,
  createRowSizes,
  type QueryViewport,
  type QueryViewportController,
  type QueryViewportItem,
  type QueryViewportOptions,
  type RowSizeEstimate,
  type RowSizes,
} from "./query-viewport.js";

export {
  regionCollectionsFeature,
  regionEntries,
  selectedRegion,
  type RegionCollection,
  type RegionEntry,
  type RegionOutcome,
  type RegionSlot,
} from "./regions.js";

/**
 * A generated content reference: a View presented in a content slot, such as a ViewModel slot of .NET
 * `PageReference`. Generated `{Name}PageReference` types satisfy it.
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
export {
  createMenuBarController,
  splitAccessKey,
  type MenuBarController,
  type MenuBarControllerOptions,
  type MenuBarEntry,
  type MenuBarInvokeResult,
  type MenuBarItem,
  type MenuBarMenu,
  type MenuBarRequest,
  type MenuBarSeparator,
  type MenuBarState,
} from "./menu-bar.js";
export { shortcutFromEvent } from "./shortcut-recording.js";
