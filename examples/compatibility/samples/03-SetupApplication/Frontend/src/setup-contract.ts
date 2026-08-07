import { Schema } from "effect";
import { defineApplicationContract } from "@runic-artifex/application-bridge";

export const Uuid = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i),
);
export const SetupViewId = Schema.Literal("Welcome", "Destination", "Features", "Installing", "Complete");
export const FeatureId = Schema.Literal("core", "desktop-shortcut", "examples");
export const DestinationSelection = Schema.Struct({
  selectionId: Uuid,
  displayName: Schema.String,
  availableBytes: Schema.Int.pipe(Schema.nonNegative()),
});
export const SetupSnapshot = Schema.Struct({
  viewId: SetupViewId,
  revision: Schema.Int.pipe(Schema.nonNegative()),
  destination: Schema.optional(DestinationSelection),
  selectedFeatures: Schema.Array(FeatureId),
  activeOperationId: Schema.optional(Uuid),
  canNavigateBack: Schema.Boolean,
  canNavigateNext: Schema.Boolean,
});
export const SetupCommand = Schema.Union(
  Schema.TaggedStruct("InitializeApplication", {}),
  Schema.TaggedStruct("SelectDestination", { currentSelectionId: Schema.optional(Uuid) }),
  Schema.TaggedStruct("Navigate", {
    target: SetupViewId,
    expectedRevision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("StartInstallation", {
    destinationSelectionId: Uuid,
    selectedFeatures: Schema.Array(FeatureId),
  }),
  Schema.TaggedStruct("CancelOperation", { operationId: Uuid }),
);
export const SetupReceipt = Schema.Union(
  Schema.TaggedStruct("ApplicationInitialized", { snapshot: SetupSnapshot }),
  Schema.TaggedStruct("DestinationSelected", {
    destination: DestinationSelection,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("NavigationAccepted", { snapshot: SetupSnapshot }),
  Schema.TaggedStruct("InstallationStarted", {
    commandId: Uuid,
    operationId: Uuid,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("OperationCancellationAccepted", {
    operationId: Uuid,
    accepted: Schema.Boolean,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
);
export const SetupEvent = Schema.Union(
  Schema.TaggedStruct("SnapshotReplaced", { snapshot: SetupSnapshot }),
  Schema.TaggedStruct("NavigationChanged", {
    viewId: SetupViewId,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("OperationProgress", {
    operationId: Uuid,
    completed: Schema.Int.pipe(Schema.nonNegative()),
    total: Schema.Int.pipe(Schema.positive()),
    message: Schema.optional(Schema.String),
  }),
  Schema.TaggedStruct("OperationCompleted", {
    operationId: Uuid,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("OperationFailed", {
    operationId: Uuid,
    error: Schema.String,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
  Schema.TaggedStruct("OperationCancelled", {
    operationId: Uuid,
    revision: Schema.Int.pipe(Schema.nonNegative()),
  }),
);

export const SetupContract = defineApplicationContract({
  identity: "runic.artifex.setup",
  version: 1,
  command: SetupCommand,
  receipt: SetupReceipt,
  event: SetupEvent,
  snapshot: SetupSnapshot,
  initialize: { _tag: "InitializeApplication" } as const,
});

export type SetupViewId = typeof SetupViewId.Type;
export type FeatureId = typeof FeatureId.Type;
export type DestinationSelection = typeof DestinationSelection.Type;
export type SetupSnapshot = typeof SetupSnapshot.Type;
export type SetupCommand = typeof SetupCommand.Type;
export type SetupReceipt = typeof SetupReceipt.Type;
export type SetupEvent = typeof SetupEvent.Type;
