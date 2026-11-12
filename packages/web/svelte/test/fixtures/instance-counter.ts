import type { TextReference, ViewClient, ViewConnector } from "@runic-artifex/views";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";

export type CounterState = { readonly count: number; readonly status: TextReference };
export type InstanceCounter = ViewConnector<ViewClient<CounterState>>;

const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1_000), status: bridgeWire.textReference };
export const instanceCounter: InstanceCounter = {
  connect: () => connectView<CounterState>({ contract: "Tests.Counter:fp-1", route: "counter", members }),
};
