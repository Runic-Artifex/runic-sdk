import { Schema } from "effect";
import { bridge, defineApplicationBridgeContract } from "@runic-artifex/application-bridge";

export const Uuid = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/),
).annotations({ identifier: "Uuid" });
const Revision = Schema.Int.pipe(Schema.nonNegative()).annotations({ identifier: "Revision" });
export const SetupViewId = Schema.Literal("Welcome", "Destination", "Features", "Installing", "Complete");
export const FeatureId = Schema.Literal("core", "desktop-shortcut", "examples");
export const DestinationSelection = Schema.Struct({
  selectionId: Uuid,
  displayName: Schema.String,
  availableBytes: Schema.Int.pipe(Schema.nonNegative()),
}).annotations({ identifier: "DestinationSelection" });
export const SetupSnapshot = Schema.Struct({
  viewId: SetupViewId,
  revision: Revision,
  destination: Schema.optional(DestinationSelection),
  selectedFeatures: Schema.Array(FeatureId),
  activeOperationId: Schema.optional(Uuid),
  canNavigateBack: Schema.Boolean,
  canNavigateNext: Schema.Boolean,
}).annotations({ identifier: "SetupSnapshot" });

export const InitializeApplication = Schema.TaggedStruct("InitializeApplication", {});
export const SelectDestination = Schema.TaggedStruct("SelectDestination", { currentSelectionId: Schema.optional(Uuid) });
export const Navigate = Schema.TaggedStruct("Navigate", { target: SetupViewId, expectedRevision: Revision });
export const StartInstallation = Schema.TaggedStruct("StartInstallation", { destinationSelectionId: Uuid, selectedFeatures: Schema.Array(FeatureId) });
export const CancelOperation = Schema.TaggedStruct("CancelOperation", { operationId: Uuid });
export const ApplicationInitialized = Schema.TaggedStruct("ApplicationInitialized", { snapshot: SetupSnapshot });
export const DestinationSelected = Schema.TaggedStruct("DestinationSelected", { destination: DestinationSelection, revision: Revision });
export const NavigationAccepted = Schema.TaggedStruct("NavigationAccepted", { snapshot: SetupSnapshot });
export const InstallationStarted = Schema.TaggedStruct("InstallationStarted", { commandId: Uuid, operationId: Uuid, revision: Revision });
export const OperationCancellationAccepted = Schema.TaggedStruct("OperationCancellationAccepted", { operationId: Uuid, accepted: Schema.Boolean, revision: Revision });
export const SnapshotReplaced = Schema.TaggedStruct("SnapshotReplaced", { snapshot: SetupSnapshot });
export const NavigationChanged = Schema.TaggedStruct("NavigationChanged", { viewId: SetupViewId, revision: Revision });
export const OperationProgress = Schema.TaggedStruct("OperationProgress", { operationId: Uuid, completed: Schema.Int.pipe(Schema.nonNegative()), total: Schema.Int.pipe(Schema.positive()), message: Schema.optional(Schema.String) });
export const OperationCompleted = Schema.TaggedStruct("OperationCompleted", { operationId: Uuid, revision: Revision });
export const InstallationFailed = Schema.TaggedStruct("OperationFailed", { operationId: Uuid, error: Schema.String, revision: Revision });
export const InstallationCancelled = Schema.TaggedStruct("OperationCancelled", { operationId: Uuid, revision: Revision });

export const SetupCommand = Schema.Union(InitializeApplication, SelectDestination, Navigate, StartInstallation, CancelOperation);
export const SetupReceipt = Schema.Union(ApplicationInitialized, DestinationSelected, NavigationAccepted, InstallationStarted, OperationCancellationAccepted);
export const SetupEvent = Schema.Union(SnapshotReplaced, NavigationChanged, OperationProgress, OperationCompleted, InstallationFailed, InstallationCancelled);

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.setup", version: 1 },
  csharp: { namespace: "Runic.Examples.Setup.Contract", contractName: "Setup" },
  snapshot: SetupSnapshot,
  commands: [
    bridge.command(InitializeApplication, { receipt: ApplicationInitialized }),
    bridge.command(SelectDestination, { receipt: DestinationSelected, advancesRevision: true }),
    bridge.command(Navigate, { receipt: NavigationAccepted, advancesRevision: true }),
    bridge.command(StartInstallation, { receipt: InstallationStarted, startsOperation: true, cancellable: true, advancesRevision: true }),
    bridge.command(CancelOperation, { receipt: OperationCancellationAccepted }),
  ],
  events: [SnapshotReplaced, NavigationChanged, OperationProgress, OperationCompleted, InstallationFailed, InstallationCancelled],
  errors: [],
  initialize: { _tag: "InitializeApplication" },
});

export type SetupViewId = typeof SetupViewId.Type;
export type FeatureId = typeof FeatureId.Type;
export type DestinationSelection = typeof DestinationSelection.Type;
export type SetupSnapshot = typeof SetupSnapshot.Type;
export type SetupCommand = typeof SetupCommand.Type;
export type SetupReceipt = typeof SetupReceipt.Type;
export type SetupEvent = typeof SetupEvent.Type;
