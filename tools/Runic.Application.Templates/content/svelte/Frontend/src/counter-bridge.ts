import { Effect } from "effect";
import {
  CsWebUiApplicationBridgeLive,
  MockApplicationBridge,
  createApplicationBridgeController,
  createCsWebUiFrameChannel,
} from "@runic-artifex/application-bridge";
import { createSvelteApplicationBridge } from "@runic-artifex/svelte";
import {
  createRunicToolkitDevtoolsObserver,
  preserveRunicToolkitHmrResource,
} from "virtual:runic-toolkit/client";
import {
  CounterContract,
  type CounterCommand,
  type CounterEvent,
  type CounterReceipt,
  type CounterSnapshot,
} from "./counter-contract";

let count = 0;
let revision = 0;
const history = [0];
const snapshot = (): CounterSnapshot => ({ count, revision, history: [...history] });
const mock = MockApplicationBridge<CounterCommand, CounterReceipt, CounterEvent, CounterSnapshot>({
  initialize: () => Effect.succeed(snapshot()),
  dispatch: (command, publish) => {
    if (command._tag === "IncrementCounter") {
      count += command.step;
      history.push(count);
    } else if (command._tag === "ResetCounter") {
      count = 0;
      history.splice(0, history.length, 0);
    }
    revision++;
    const current = snapshot();
    return publish({ _tag: "CounterChanged", snapshot: current }).pipe(
      Effect.as({ _tag: command._tag === "ResetCounter" ? "CounterReset" : "CounterIncremented", snapshot: current }),
    );
  },
});

export const counterBridge = preserveRunicToolkitHmrResource("counter-bridge", () =>
  createSvelteApplicationBridge(
    createApplicationBridgeController(
      CounterContract,
      import.meta.env.MODE === "mock"
        ? mock
        : CsWebUiApplicationBridgeLive(CounterContract, createCsWebUiFrameChannel()),
    ),
    {
      reduce: (_snapshot, event) => event.snapshot,
      observer: createRunicToolkitDevtoolsObserver(),
      inspectSnapshot: (snapshot) => ({ revision: snapshot.revision }),
    },
  ));
