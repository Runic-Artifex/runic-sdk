import { Schema } from "effect";
import {
  bridge,
  defineApplicationBridgeContract,
} from "../../../web/packages/application-bridge/dist/esm/index.js";

const Uuid = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/),
).annotations({ identifier: "Uuid" });
const Revision = Schema.Int.pipe(Schema.nonNegative()).annotations({ identifier: "Revision" });
const SetupViewId = Schema.Literal("Welcome", "Destination", "Features", "Installing", "Complete");
const FeatureId = Schema.Literal("core", "desktop-shortcut", "examples");
const DestinationSelection = Schema.Struct({
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

const InitializeApplication = Schema.TaggedStruct("InitializeApplication", {});
const SelectDestination = Schema.TaggedStruct("SelectDestination", { currentSelectionId: Schema.optional(Uuid) });
const Navigate = Schema.TaggedStruct("Navigate", { target: SetupViewId, expectedRevision: Revision });
const StartInstallation = Schema.TaggedStruct("StartInstallation", {
  destinationSelectionId: Uuid,
  selectedFeatures: Schema.Array(FeatureId),
});
const CancelOperation = Schema.TaggedStruct("CancelOperation", { operationId: Uuid });
const ApplicationInitialized = Schema.TaggedStruct("ApplicationInitialized", { snapshot: SetupSnapshot });
const DestinationSelected = Schema.TaggedStruct("DestinationSelected", { destination: DestinationSelection, revision: Revision });
const NavigationAccepted = Schema.TaggedStruct("NavigationAccepted", { snapshot: SetupSnapshot });
const InstallationStarted = Schema.TaggedStruct("InstallationStarted", { commandId: Uuid, operationId: Uuid, revision: Revision });
const OperationCancellationAccepted = Schema.TaggedStruct("OperationCancellationAccepted", { operationId: Uuid, accepted: Schema.Boolean, revision: Revision });

const SnapshotReplaced = Schema.TaggedStruct("SnapshotReplaced", { snapshot: SetupSnapshot });
const NavigationChanged = Schema.TaggedStruct("NavigationChanged", { viewId: SetupViewId, revision: Revision });
const OperationProgress = Schema.TaggedStruct("OperationProgress", {
  operationId: Uuid,
  completed: Schema.Int.pipe(Schema.nonNegative()),
  total: Schema.Int.pipe(Schema.positive()),
  message: Schema.optional(Schema.String),
});
const OperationCompleted = Schema.TaggedStruct("OperationCompleted", { operationId: Uuid, revision: Revision });
const InstallationFailed = Schema.TaggedStruct("OperationFailed", { operationId: Uuid, error: Schema.String, revision: Revision });
const InstallationCancelled = Schema.TaggedStruct("OperationCancelled", { operationId: Uuid, revision: Revision });

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.setup", version: 1 },
  csharp: { namespace: "Runic.Application.Setup.Contract", contractName: "Setup" },
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
