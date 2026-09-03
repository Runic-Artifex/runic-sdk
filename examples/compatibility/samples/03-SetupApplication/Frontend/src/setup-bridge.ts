import { Effect } from "effect";
import {
  ApplicationBridgeLive,
  MockApplicationBridge,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import SetupContract from "./application.bridge.generated";
import {
  type DestinationSelection,
  type SetupCommand,
  type SetupEvent,
  type SetupReceipt,
  type SetupSnapshot,
} from "./application.bridge";

const destination: DestinationSelection = {
  selectionId: "7e510a78-3c9a-4bed-8c31-2d93e5bbb835",
  displayName: "Recommended local destination",
  availableBytes: 12_000_000_000,
};
let snapshot: SetupSnapshot = {
  viewId: "Welcome",
  revision: 0,
  selectedFeatures: ["core"],
  canNavigateBack: false,
  canNavigateNext: true,
};

const mock = MockApplicationBridge<SetupCommand, SetupReceipt, SetupEvent, SetupSnapshot>({
  initialize: () => Effect.succeed(snapshot),
  dispatch: (command, publish) => Effect.gen(function*() {
    if (command._tag === "SelectDestination") {
      snapshot = { ...snapshot, destination, revision: snapshot.revision + 1, canNavigateNext: true };
      return { _tag: "DestinationSelected", destination, revision: snapshot.revision } as const;
    }
    if (command._tag === "Navigate") {
      snapshot = {
        ...snapshot,
        viewId: command.target,
        revision: snapshot.revision + 1,
        canNavigateBack: command.target !== "Welcome" && command.target !== "Complete",
        canNavigateNext: command.target !== "Installing" && command.target !== "Complete",
      };
      return { _tag: "NavigationAccepted", snapshot } as const;
    }
    if (command._tag === "StartInstallation") {
      const operationId = crypto.randomUUID();
      snapshot = {
        ...snapshot,
        viewId: "Installing",
        revision: snapshot.revision + 1,
        selectedFeatures: command.selectedFeatures,
        activeOperationId: operationId,
        canNavigateBack: false,
        canNavigateNext: false,
      };
      yield* publish({ _tag: "OperationProgress", operationId, completed: 2, total: 5, message: "Installing components" });
      yield* publish({ _tag: "OperationProgress", operationId, completed: 5, total: 5, message: "Finalizing" });
      snapshot = { ...snapshot, viewId: "Complete", revision: snapshot.revision + 1, activeOperationId: undefined };
      yield* publish({ _tag: "OperationCompleted", operationId, revision: snapshot.revision });
      return { _tag: "InstallationStarted", commandId: crypto.randomUUID(), operationId, revision: snapshot.revision - 1 } as const;
    }
    if (command._tag === "InitializeApplication") {
      return { _tag: "ApplicationInitialized", snapshot } as const;
    }
    return { _tag: "OperationCancellationAccepted", operationId: command.operationId, accepted: true, revision: snapshot.revision } as const;
  }),
  cancel: (operationId) => {
    snapshot = { ...snapshot, viewId: "Features", revision: snapshot.revision + 1, activeOperationId: undefined };
    return Effect.void.pipe(Effect.annotateLogs("operationId", operationId));
  },
});

export const setupBridge = createApplicationBridgeController(
  SetupContract,
  import.meta.env.MODE === "mock"
    ? mock
    : ApplicationBridgeLive(SetupContract, createDesktopFrameChannel()),
);
