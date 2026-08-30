import { Context, Effect, Layer, PubSub, Stream } from "effect";
import { asTransportError, type DesktopTransportError } from "./errors.js";
import {
  createDesktopFrameChannel,
  type DesktopFrameChannelOptions,
  type FrameChannel,
  type FrameChannelState,
  type ReconnectableFrameChannel,
} from "./transport.js";

export interface DesktopTransportService {
  readonly channel: FrameChannel;
  readonly send: (bytes: Uint8Array) => Effect.Effect<void, DesktopTransportError>;
  readonly reconnect: Effect.Effect<void, DesktopTransportError>;
  readonly frames: Stream.Stream<Uint8Array, DesktopTransportError>;
  readonly states: Stream.Stream<FrameChannelState>;
}

export const DesktopTransport = Context.GenericTag<DesktopTransportService>(
  "@runic-artifex/desktop/DesktopTransport",
);

export function DesktopTransportLive(
  options: DesktopFrameChannelOptions = {},
): Layer.Layer<DesktopTransportService, DesktopTransportError> {
  return Layer.scoped(
    DesktopTransport,
    Effect.gen(function*() {
      const frames = yield* PubSub.dropping<Uint8Array>(256);
      const states = yield* PubSub.sliding<FrameChannelState>(16);
      const channel = yield* Effect.try({
        try: () => createDesktopFrameChannel(options),
        catch: asTransportError,
      });
      const unsubscribe = channel.subscribe((event) => {
        if (event._tag === "Frame") {
          Effect.runSync(PubSub.publish(frames, event.bytes));
        } else {
          Effect.runSync(PubSub.publish(states, event.state));
        }
      });
      yield* Effect.addFinalizer(() => Effect.gen(function*() {
        unsubscribe();
        yield* Effect.promise(() => channel.close("Runic Desktop transport scope closed"));
        yield* PubSub.shutdown(frames);
        yield* PubSub.shutdown(states);
      }));

      const service: DesktopTransportService = {
        channel,
        send: (bytes) => Effect.tryPromise({
          try: () => channel.send(bytes),
          catch: asTransportError,
        }),
        reconnect: interruptibleReconnect(channel),
        frames: Stream.fromPubSub(frames),
        states: Stream.fromPubSub(states),
      };
      return service;
    }),
  );
}

function interruptibleReconnect(
  channel: ReconnectableFrameChannel,
): Effect.Effect<void, DesktopTransportError> {
  return Effect.async<void, DesktopTransportError>((resume) => {
    let active = true;
    channel.reconnect().then(
      () => { if (active) resume(Effect.void); },
      (error: unknown) => { if (active) resume(Effect.fail(asTransportError(error))); },
    );
    return Effect.sync(() => {
      active = false;
      void channel.close("Runic Desktop reconnect interrupted");
    });
  });
}
