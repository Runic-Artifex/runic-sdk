import { Effect, Schema } from "effect";
import { transportError, type DesktopTransportError } from "./errors.js";

export const wireProfile = "webui-compat/52f9e75" as const;
export const applicationBridgeCapability = "runic.desktop.application-bridge/1" as const;
export const applicationBridgeReceiver = "__runicDesktopReceiveApplicationBridgeFrame" as const;

export const DesktopBootstrapSchema = Schema.Struct({
  product: Schema.Literal("Runic Desktop"),
  profile: Schema.Literal(wireProfile),
  endpoint: Schema.String,
  token: Schema.Number.pipe(Schema.check(Schema.isInt()), Schema.check(Schema.isBetween({ minimum: 0, maximum: 0xffff_ffff }))),
  sessionCredential: Schema.String,
});

export type DesktopBootstrap = typeof DesktopBootstrapSchema.Type;

export interface RunicDesktopGlobal extends DesktopBootstrap {}

declare global {
  // Installed by the surface-local /runic-desktop.js bootstrap asset.
  var runicDesktop: RunicDesktopGlobal | undefined;
}

export function decodeDesktopBootstrap(value: unknown): Effect.Effect<DesktopBootstrap, DesktopTransportError> {
  return Schema.decodeUnknownEffect(DesktopBootstrapSchema, { onExcessProperty: "error" })(value).pipe(
    Effect.mapError(() => transportError(
      "ConfigurationInvalid",
      "bootstrap-invalid",
      "The Runic Desktop browser bootstrap is missing or invalid.",
    )),
  );
}
