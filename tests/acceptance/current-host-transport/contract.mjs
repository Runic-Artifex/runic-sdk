import { Schema } from "effect";

const Uuid = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i),
).annotations({ identifier: "Uuid" });
const Revision = Schema.Int.pipe(Schema.nonNegative()).annotations({ identifier: "Revision" });
const SetupViewId = Schema.Literal("Welcome", "Destination", "Features", "Installing", "Complete");
const FeatureId = Schema.Literal("core", "desktop-shortcut", "examples");

const DestinationSelection = Schema.Struct({
  selectionId: Uuid,
  displayName: Schema.String,
  availableBytes: Schema.Int.pipe(Schema.nonNegative()),
});

const SetupSnapshot = Schema.Struct({
  viewId: SetupViewId,
  revision: Revision,
  destination: Schema.optional(DestinationSelection),
  selectedFeatures: Schema.Array(FeatureId),
  activeOperationId: Schema.optional(Uuid),
  canNavigateBack: Schema.Boolean,
  canNavigateNext: Schema.Boolean,
});

const commands = [
  {
    tag: "InitializeApplication",
    schema: Schema.TaggedStruct("InitializeApplication", {}),
    receipt: "ApplicationInitialized",
    startsOperation: false,
    cancellable: false,
    advancesRevision: false,
  },
  {
    tag: "SelectDestination",
    schema: Schema.TaggedStruct("SelectDestination", {
      currentSelectionId: Schema.optional(Uuid),
    }),
    receipt: "DestinationSelected",
    startsOperation: false,
    cancellable: false,
    advancesRevision: true,
  },
  {
    tag: "Navigate",
    schema: Schema.TaggedStruct("Navigate", {
      target: SetupViewId,
      expectedRevision: Revision,
    }),
    receipt: "NavigationAccepted",
    startsOperation: false,
    cancellable: false,
    advancesRevision: true,
  },
  {
    tag: "StartInstallation",
    schema: Schema.TaggedStruct("StartInstallation", {
      destinationSelectionId: Uuid,
      selectedFeatures: Schema.Array(FeatureId),
    }),
    receipt: "InstallationStarted",
    startsOperation: true,
    cancellable: true,
    advancesRevision: true,
  },
  {
    tag: "CancelOperation",
    schema: Schema.TaggedStruct("CancelOperation", { operationId: Uuid }),
    receipt: "OperationCancellationAccepted",
    startsOperation: false,
    cancellable: false,
    advancesRevision: false,
  },
];

const receipts = [
  { tag: "ApplicationInitialized", schema: Schema.TaggedStruct("ApplicationInitialized", { snapshot: SetupSnapshot }) },
  { tag: "DestinationSelected", schema: Schema.TaggedStruct("DestinationSelected", { destination: DestinationSelection, revision: Revision }) },
  { tag: "NavigationAccepted", schema: Schema.TaggedStruct("NavigationAccepted", { snapshot: SetupSnapshot }) },
  { tag: "InstallationStarted", schema: Schema.TaggedStruct("InstallationStarted", { commandId: Uuid, operationId: Uuid, revision: Revision }) },
  { tag: "OperationCancellationAccepted", schema: Schema.TaggedStruct("OperationCancellationAccepted", { operationId: Uuid, accepted: Schema.Boolean, revision: Revision }) },
];

const events = [
  { tag: "SnapshotReplaced", schema: Schema.TaggedStruct("SnapshotReplaced", { snapshot: SetupSnapshot }) },
  { tag: "NavigationChanged", schema: Schema.TaggedStruct("NavigationChanged", { viewId: SetupViewId, revision: Revision }) },
  { tag: "OperationProgress", schema: Schema.TaggedStruct("OperationProgress", { operationId: Uuid, completed: Schema.Int.pipe(Schema.nonNegative()), total: Schema.Int.pipe(Schema.positive()), message: Schema.optional(Schema.String) }) },
  { tag: "OperationCompleted", schema: Schema.TaggedStruct("OperationCompleted", { operationId: Uuid, revision: Revision }) },
  { tag: "OperationFailed", schema: Schema.TaggedStruct("OperationFailed", { operationId: Uuid, error: Schema.String, revision: Revision }) },
  { tag: "OperationCancelled", schema: Schema.TaggedStruct("OperationCancelled", { operationId: Uuid, revision: Revision }) },
];

const errors = [
  "TransportUnavailable",
  "TransportClosed",
  "ProtocolVersionMismatch",
  "ProtocolDecodeError",
  "CommandRejected",
  "StaleRevision",
  "OperationFailed",
  "OperationCancelled",
  "OperationTimedOut",
].map((tag) => ({
  tag,
  schema: Schema.TaggedStruct(tag, {
    message: Schema.String,
    retryable: Schema.Boolean,
  }),
}));

export default {
  formatVersion: 1,
  protocol: { identity: "runic.artifex.setup", version: 1 },
  csharp: { namespace: "RunicToolkit.Setup.Contract", contractName: "Setup" },
  limits: {
    maxFrameBytes: 262144,
    maxDepth: 32,
    maxStringBytes: 65536,
    maxCollectionItems: 4096,
    maxPendingCommands: 64,
  },
  schemas: {
    SetupSnapshot,
    DestinationSelection,
  },
  commands,
  receipts,
  events,
  errors,
};
