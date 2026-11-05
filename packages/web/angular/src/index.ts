export {
  ViewOutlet,
  RegionOutlets,
  RunicViewOutlet,
  type RegionOutletsMode,
  type RegionPanelAttributes,
  type ViewOutletMissingContext,
  type ViewRegistry,
} from "./view-outlet.js";
export { injectView, type InjectViewOptions, type ViewHandle } from "./inject-view.js";
export { injectViewResource, type InjectViewResourceOptions, type ViewResource } from "./inject-view-resource.js";
export { injectCommand, type CommandHandle, type InjectCommandOptions } from "./inject-command.js";
export { injectOperation, type InjectOperationOptions, type OperationHandle } from "./inject-operation.js";
export { injectField, type FieldHandle, type FieldView, type InjectFieldOptions } from "./inject-field.js";
export { injectForm, type FormFieldHandle, type FormHandle, type InjectFormOptions } from "./inject-form.js";
export { commitAllFields, type FormState, type FormSubmitOptions, type FormSubmitResult } from "@runic-artifex/views";
export { injectTextField, type InjectTextFieldOptions, type TextFieldHandle, type TextFieldView } from "./inject-text-field.js";
export {
  injectLatestOperation,
  type InjectLatestOperationOptions,
  type LatestOperationHandle,
} from "./inject-latest-operation.js";
export { injectCollectionViewport, type InjectCollectionViewportOptions } from "./inject-collection-viewport.js";
export { injectQueryCollection, type InjectQueryCollectionOptions, type QueryCollectionHandle } from "./inject-query-collection.js";
export { injectBridgeConnection, type BridgeConnectionHandle, type InjectBridgeConnectionOptions } from "./inject-bridge-connection.js";
export { injectText, injectWindowLocale, RunicTextPipe, textLocaleSignal, type InjectTextOptions } from "./inject-text.js";
export { injectAppearance, type InjectAppearanceOptions } from "./inject-appearance.js";
export {
  configureTranslatedText, resolveText, syncDocumentLocale, textDirection, windowLocale, type DocumentLocaleOptions, type TextReference,
  type TextTranslations,
} from "@runic-artifex/views";
export {
  appearance, onAppearance, type Appearance, type AppearanceColorScheme, type AppearanceContrast, type AppearanceSystemColors,
  type HostAppearance,
} from "@runic-artifex/views";
export { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";
// blobs/1: uploads from the page and the blob values of state.
export { upload, type BlobRef, type RunicBlob, type UploadOptions, type UploadProgress } from "@runic-artifex/views";
export {
  regionEntries,
  selectedRegion,
  type RegionCollection,
  type RegionEntry,
  type RegionOutcome,
  type RegionSlot,
} from "@runic-artifex/views";
export {
  installBackNavigation,
  installNavigationPath,
  navigateBack,
  onNavigationPath,
  openNavigationPath,
  type NavigationPathChange,
  type NavigationPathOptions,
  type NavigationReply,
} from "@runic-artifex/views";
export {
  injectMenuBar,
  type InjectMenuBarOptions,
  type MenuBarEntry,
  type MenuBarHandle,
  type MenuBarInvokeResult,
  type MenuBarItem,
  type MenuBarMenu,
  type MenuBarRequest,
  type MenuBarSeparator,
  type MenuBarState,
} from "./inject-menu-bar.js";
