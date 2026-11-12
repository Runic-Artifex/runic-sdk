export { default as ViewOutlet } from "./ViewOutlet.svelte";
export { default as RegionOutlets } from "./RegionOutlets.svelte";
export type { RegionPanelAttributes } from "./region-panel.js";
export type { ViewRegistry } from "./view-registry.js";
export { useSuspenseView, useView, type SuspenseViewHandle, type UseViewOptions, type ViewHandle } from "./use-view.svelte.js";
export { preloadView, type PreloadViewOptions } from "@runic-artifex/views";
export { useCommand, type CommandHandle } from "./use-command.svelte.js";
export { useOperation, type OperationHandle } from "./use-operation.svelte.js";
export { useTextField, type TextFieldHandle, type TextFieldView, type UseTextFieldOptions } from "./use-text-field.svelte.js";
export { useField, type FieldElement, type FieldHandle } from "./use-field.svelte.js";
export { useForm, type FormFieldHandle, type FormHandle, type UseFormOptions } from "./use-form.svelte.js";
export { commitAllFields, type FormState, type FormSubmitOptions, type FormSubmitResult } from "@runic-artifex/views";
export { useLatestOperation, type LatestOperationHandle } from "./use-latest-operation.svelte.js";
export { useCollectionViewport, type CollectionViewportHandle } from "./use-collection-viewport.svelte.js";
export { useQueryCollection, type QueryCollectionHandle } from "./use-query-collection.svelte.js";
export { useBridgeConnection, type BridgeConnectionHandle } from "./use-bridge-connection.svelte.js";
export { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";
export {
  useText, useTextLocale, useWindowLocale, type TextHandle, type TextLocaleHandle, type WindowLocaleHandle,
} from "./use-text.svelte.js";
export { useAppearance, type AppearanceHandle } from "./use-appearance.svelte.js";
export { getRunicInstance, setRunicInstance } from "./instance.js";
export { createRunicInstance, pageInstance, type RunicInstance, type RunicInstanceOptions } from "@runic-artifex/views";
export {
  configureFieldErrorText, configureTranslatedText, fieldErrorText, resolveText, syncDocumentLocale, textDirection,
  windowLocale, type DocumentLocaleOptions, type FieldErrorArguments, type FieldErrorReason, type FieldErrorText,
  type TextReference, type TextTranslations,
} from "@runic-artifex/views";
export {
  appearance, onAppearance, type Appearance, type AppearanceColorScheme, type AppearanceContrast, type AppearanceSystemColors,
  type HostAppearance,
} from "@runic-artifex/views";
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
  FieldError,
  type BridgeField,
  type BridgeFieldConstraints,
  type BridgeFieldKind,
  type FieldBaseline,
  type FieldDefinition,
  type FieldErrorKind,
  type FieldOptions,
  type FieldState,
} from "@runic-artifex/views";
export { useMenuBar, type MenuBarHandle, type UseMenuBarOptions } from "./use-menu-bar.svelte.js";
export type {
  MenuBarEntry,
  MenuBarInvokeResult,
  MenuBarItem,
  MenuBarMenu,
  MenuBarRequest,
  MenuBarSeparator,
  MenuBarState,
} from "@runic-artifex/views";
