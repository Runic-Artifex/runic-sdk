// A generated View with one interaction, `Interaction<DiscardInput, bool> ConfirmDiscard`, and its typed mock (#1319).
import type { BridgeInteractionContext, ViewClient } from "@runic-artifex/views";
import { connectView, defineInteractions } from "@runic-artifex/views/generated";
import { mockTypedView } from "@runic-artifex/views/generated/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { createMockBridge, installMockBridge, type MockTypedInteraction, type MockTypedView } from "@runic-artifex/views/mock";

export type DiscardState = { readonly title: string };
export type DiscardInput = { readonly title: string };
// The shapes the generator emits for the View's client and mock.
export interface DiscardClient extends ViewClient<DiscardState> {
  readonly interactions: {
    readonly confirmDiscard: {
      handle(handler: (input: DiscardInput, context: BridgeInteractionContext) => boolean | Promise<boolean>): () => void;
    };
  };
}
export interface DiscardMock extends MockTypedView<DiscardState, DiscardClient> {
  readonly interactions: { readonly confirmDiscard: MockTypedInteraction<DiscardInput, boolean> };
}
const contract = "Tests.DiscardViewModel:I1";

/** Installs a mock bridge with the View. */
export function mockDiscard(): DiscardMock {
  const bridge = installMockBridge(createMockBridge());
  return mockTypedView(bridge, {
    kind: "discard", route: "discard", contract, defaults: {},
    fields: { title: { encode: value => value, decode: value => wire.string(value) } },
    interactions: { confirmDiscard: { encodeInput: value => value, decodeOutput: value => wire.boolean(value) } },
  }, { state: { title: "a" } }) as DiscardMock;
}

/** Connects a client of the View, as a generated `connectDiscard()` does. */
export function connectDiscard(): Promise<DiscardClient> {
  return connectView<DiscardState>({
    contract, route: "discard", members: { title: wire.string },
    interactions: defineInteractions({
      confirmDiscard: {
        contract: `${contract}:interaction:ConfirmDiscard`,
        decodeInput: value => wire.object(value, input => ({ title: wire.string(input["title"]) })),
        encodeOutput: value => value,
      },
    }),
  }) as Promise<unknown> as Promise<DiscardClient>;
}

/** Waits until `condition` holds, in real time. */
export async function until(condition: () => boolean, message: string): Promise<void> {
  for (let round = 0; !condition(); round++) {
    if (round > 1000) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 2));
  }
}
