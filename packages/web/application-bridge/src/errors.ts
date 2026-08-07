import { Schema } from "effect";

const publicFields = {
  message: Schema.String,
  retryable: Schema.Boolean,
} as const;

export class TransportUnavailable extends Schema.TaggedError<TransportUnavailable>()(
  "TransportUnavailable",
  publicFields,
) {}
export class TransportClosed extends Schema.TaggedError<TransportClosed>()(
  "TransportClosed",
  publicFields,
) {}
export class ProtocolVersionMismatch extends Schema.TaggedError<ProtocolVersionMismatch>()(
  "ProtocolVersionMismatch",
  publicFields,
) {}
export class ProtocolDecodeError extends Schema.TaggedError<ProtocolDecodeError>()(
  "ProtocolDecodeError",
  publicFields,
) {}
export class CommandRejected extends Schema.TaggedError<CommandRejected>()(
  "CommandRejected",
  publicFields,
) {}
export class StaleRevision extends Schema.TaggedError<StaleRevision>()(
  "StaleRevision",
  publicFields,
) {}
export class OperationFailed extends Schema.TaggedError<OperationFailed>()(
  "OperationFailed",
  publicFields,
) {}
export class OperationCancelled extends Schema.TaggedError<OperationCancelled>()(
  "OperationCancelled",
  publicFields,
) {}
export class OperationTimedOut extends Schema.TaggedError<OperationTimedOut>()(
  "OperationTimedOut",
  publicFields,
) {}

export const BridgeErrorSchema = Schema.Union(
  TransportUnavailable,
  TransportClosed,
  ProtocolVersionMismatch,
  ProtocolDecodeError,
  CommandRejected,
  StaleRevision,
  OperationFailed,
  OperationCancelled,
  OperationTimedOut,
);

export type BridgeError = typeof BridgeErrorSchema.Type;

const constructors = {
  TransportUnavailable,
  TransportClosed,
  ProtocolVersionMismatch,
  ProtocolDecodeError,
  CommandRejected,
  StaleRevision,
  OperationFailed,
  OperationCancelled,
  OperationTimedOut,
} as const;

export function bridgeError(
  tag: keyof typeof constructors,
  message: string,
  retryable = false,
): BridgeError {
  const Constructor = constructors[tag];
  return new Constructor({ message: sanitizeMessage(message), retryable }) as BridgeError;
}

function sanitizeMessage(message: string): string {
  const normalized = message.replace(/[\r\n\t]/g, " ").trim();
  return normalized.length === 0
    ? "The application bridge request failed."
    : normalized.slice(0, 512);
}
