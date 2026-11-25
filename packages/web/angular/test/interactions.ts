// A generated View with two interactions, `Interaction<DiscardInput, bool> ConfirmDiscard` and
// `Interaction<Unit, bool> ConfirmQuit`, and its typed mock (#1319).
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
    readonly confirmQuit: {
      handle(handler: (input: void, context: BridgeInteractionContext) => boolean | Promise<boolean>): () => void;
    };
  };
}
export interface DiscardMock extends MockTypedView<DiscardState, DiscardClient> {
  readonly interactions: {
    readonly confirmDiscard: MockTypedInteraction<DiscardInput, boolean>;
    readonly confirmQuit: MockTypedInteraction<void, boolean>;
  };
}
const contract = "Tests.DiscardViewModel:I1";

/** Installs a mock bridge with the View. */
export function mockDiscard(): DiscardMock {
  const bridge = installMockBridge(createMockBridge());
  return mockTypedView(bridge, {
    kind: "discard", route: "discard", contract, defaults: {},
    fields: { title: { encode: value => value, decode: value => wire.string(value) } },
    interactions: {
      confirmDiscard: { encodeInput: value => value, decodeOutput: value => wire.boolean(value) },
      confirmQuit: { encodeInput: () => null, decodeOutput: value => wire.boolean(value) },
    },
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
      confirmQuit: { contract: `${contract}:interaction:ConfirmQuit`, decodeInput: () => undefined, encodeOutput: value => value },
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

/**
 * Sends a request once the client's handler is registered: after a client change the mock may still answer
 * `unhandled`, or reach the old client's presentation, which answers `cancelled`, until the registrations arrived, so it
 * asks again until `shown()` holds. Resolves with the
 * pending reply, wrapped so that awaiting this does not wait for the answer.
 */
export async function requestShown<TInput, TOutput>(
  interaction: MockTypedInteraction<TInput, TOutput>, input: TInput, shown: () => boolean,
  // React waits inside act(), so that each render happens while it waits.
  wait: (condition: () => boolean, message: string) => Promise<void> = until,
): Promise<{ readonly reply: ReturnType<MockTypedInteraction<TInput, TOutput>["request"]> }> {
  for (let attempt = 0; attempt < 100; attempt++) {
    let settled = false;
    const reply = interaction.request(input);
    void reply.then(() => { settled = true; });
    await wait(() => shown() || settled, "The request was neither shown nor answered.");
    if (shown()) return { reply };
    const { kind } = await reply;
    if (kind !== "unhandled" && kind !== "cancelled") return { reply };
  }
  throw new Error("The client never handled the request.");
}

/** Lets the mock's requests reach the page's handler. */
export const delivered = () => new Promise<void>(resolve => setTimeout(resolve, 10));
