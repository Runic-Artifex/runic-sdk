import { Effect } from "effect";
import {
  ApplicationBridgeLive,
  MockApplicationBridge,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import { createSvelteApplicationBridge } from "@runic-artifex/svelte";
import {
  createRunicDevtoolsObserver,
  preserveRunicHmrResource,
} from "virtual:runic/client";
import {
  SetupContract,
  type DestinationSelection,
  type SetupCommand,
  type SetupEvent,
  type SetupReceipt,
  type SetupSnapshot,
} from "./setup-contract";

const destination: DestinationSelection = {
  selectionId: "7e510a78-3c9a-4bed-8c31-2d93e5bbb835",
  displayName: "Recommended local destination",
  availableBytes: 12_000_000_000,
};
let mockSnapshot: SetupSnapshot = initialSnapshot();

const mock = MockApplicationBridge<SetupCommand, SetupReceipt, SetupEvent, SetupSnapshot>({
  initialize: () => Effect.succeed(mockSnapshot),
  dispatch: (command, publish) => Effect.gen(function*() {
    if (command._tag === "SelectDestination") {
      mockSnapshot = {
        ...mockSnapshot,
        destination,
        revision: mockSnapshot.revision + 1,
        canNavigateNext: true,
      };
      return {
        _tag: "DestinationSelected",
        destination,
        revision: mockSnapshot.revision,
      } as const;
    }
    if (command._tag === "Navigate") {
      mockSnapshot = {
        ...mockSnapshot,
        viewId: command.target,
        revision: mockSnapshot.revision + 1,
        canNavigateBack: command.target !== "Welcome" && command.target !== "Complete",
        canNavigateNext: command.target !== "Installing" && command.target !== "Complete",
      };
      return { _tag: "NavigationAccepted", snapshot: mockSnapshot } as const;
    }
    if (command._tag === "StartInstallation") {
      const operationId = crypto.randomUUID();
      mockSnapshot = {
        ...mockSnapshot,
        viewId: "Installing",
        revision: mockSnapshot.revision + 1,
        selectedFeatures: command.selectedFeatures,
        activeOperationId: operationId,
        canNavigateBack: false,
        canNavigateNext: false,
      };
      yield* publish({
        _tag: "OperationProgress",
        operationId,
        completed: 5,
        total: 5,
        message: "Finalizing",
      });
      mockSnapshot = {
        ...mockSnapshot,
        viewId: "Complete",
        revision: mockSnapshot.revision + 1,
        activeOperationId: undefined,
      };
      yield* publish({
        _tag: "OperationCompleted",
        operationId,
        revision: mockSnapshot.revision,
      });
      return {
        _tag: "InstallationStarted",
        commandId: crypto.randomUUID(),
        operationId,
        revision: mockSnapshot.revision - 1,
      } as const;
    }
    if (command._tag === "InitializeApplication") {
      return { _tag: "ApplicationInitialized", snapshot: mockSnapshot } as const;
    }
    return {
      _tag: "OperationCancellationAccepted",
      operationId: command.operationId,
      accepted: true,
      revision: mockSnapshot.revision,
    } as const;
  }),
  cancel: () => Effect.void,
});

export const setupBridge = preserveRunicHmrResource("sveltekit-setup-bridge", () =>
  createSvelteApplicationBridge(
    createApplicationBridgeController(
      SetupContract,
      import.meta.env.MODE === "mock"
        ? mock
        : ApplicationBridgeLive(SetupContract, createDesktopFrameChannel()),
    ),
    {
      reduce: reduceSetupEvent,
      observer: createRunicDevtoolsObserver(),
      inspectSnapshot: (snapshot) => ({ revision: snapshot.revision }),
    },
  ));

export function initialSnapshot(): SetupSnapshot {
  return {
    viewId: "Welcome",
    revision: 0,
    selectedFeatures: ["core"],
    canNavigateBack: false,
    canNavigateNext: true,
  };
}

function reduceSetupEvent(
  current: SetupSnapshot | undefined,
  event: SetupEvent,
): SetupSnapshot | undefined {
  if (event._tag === "SnapshotReplaced") return event.snapshot;
  if (current === undefined) return current;
  if (event._tag === "NavigationChanged") {
    return { ...current, viewId: event.viewId, revision: event.revision };
  }
  if (event._tag === "OperationCompleted") {
    return {
      ...current,
      viewId: "Complete",
      revision: event.revision,
      activeOperationId: undefined,
      canNavigateBack: false,
      canNavigateNext: false,
    };
  }
  if (event._tag === "OperationCancelled" || event._tag === "OperationFailed") {
    return {
      ...current,
      viewId: "Features",
      revision: event.revision,
      activeOperationId: undefined,
      canNavigateBack: true,
      canNavigateNext: true,
    };
  }
  return current;
}
