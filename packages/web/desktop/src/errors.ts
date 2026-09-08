import { Schema } from "effect";

const fields = {
  category: Schema.String,
  code: Schema.String,
  message: Schema.String,
  retryable: Schema.Boolean,
  correlationId: Schema.String,
} as const;

export class ConfigurationInvalid extends Schema.TaggedError<ConfigurationInvalid>()(
  "ConfigurationInvalid",
  fields,
) {}

export class TransportUnavailable extends Schema.TaggedError<TransportUnavailable>()(
  "TransportUnavailable",
  fields,
) {}

export class TransportClosed extends Schema.TaggedError<TransportClosed>()(
  "TransportClosed",
  fields,
) {}

export class AuthenticationDenied extends Schema.TaggedError<AuthenticationDenied>()(
  "AuthenticationDenied",
  fields,
) {}

export class CapabilityDenied extends Schema.TaggedError<CapabilityDenied>()(
  "CapabilityDenied",
  fields,
) {}

export class InvalidFrame extends Schema.TaggedError<InvalidFrame>()(
  "InvalidFrame",
  fields,
) {}

export class LimitExceeded extends Schema.TaggedError<LimitExceeded>()(
  "LimitExceeded",
  fields,
) {}

export const DesktopTransportErrorSchema = Schema.Union([
  ConfigurationInvalid,
  TransportUnavailable,
  TransportClosed,
  AuthenticationDenied,
  CapabilityDenied,
  InvalidFrame,
  LimitExceeded,
]);

export type DesktopTransportError = typeof DesktopTransportErrorSchema.Type;

const constructors = {
  ConfigurationInvalid,
  TransportUnavailable,
  TransportClosed,
  AuthenticationDenied,
  CapabilityDenied,
  InvalidFrame,
  LimitExceeded,
} as const;

const categories = {
  ConfigurationInvalid: "invalidArgument",
  TransportUnavailable: "unavailable",
  TransportClosed: "transportClosed",
  AuthenticationDenied: "authenticationDenied",
  CapabilityDenied: "capabilityDenied",
  InvalidFrame: "invalidFrame",
  LimitExceeded: "limitExceeded",
} as const;

export function transportError(
  tag: keyof typeof constructors,
  code: string,
  message: string,
  retryable = false,
  correlationId = createCorrelationId(),
): DesktopTransportError {
  const Constructor = constructors[tag];
  return new Constructor({
    category: categories[tag],
    code,
    message: sanitize(message),
    retryable,
    correlationId,
  }) as DesktopTransportError;
}

export function asTransportError(value: unknown): DesktopTransportError {
  if (Schema.is(DesktopTransportErrorSchema)(value)) return value;
  return transportError(
    "TransportUnavailable",
    "transport-unavailable",
    "The Runic Desktop transport is unavailable.",
    true,
  );
}

function sanitize(message: string): string {
  const normalized = message.replace(/[\r\n\t]/g, " ").trim();
  return (normalized.length === 0 ? "The Runic Desktop transport failed." : normalized).slice(0, 512);
}

function createCorrelationId(): string {
  return globalThis.crypto.randomUUID().replaceAll("-", "");
}
