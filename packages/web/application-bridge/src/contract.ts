import { Schema } from "effect";
import { BridgeErrorSchema, type BridgeError } from "./errors.js";

export const UuidSchema = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i),
);
export const RevisionSchema = Schema.Int.pipe(Schema.nonNegative());
export const SequenceSchema = Schema.Int.pipe(Schema.positive());

export interface ApplicationContract<Command, Receipt, HostEvent, Snapshot> {
  readonly identity: string;
  readonly version: number;
  readonly command: Schema.Schema<Command, any>;
  readonly receipt: Schema.Schema<Receipt, any>;
  readonly event: Schema.Schema<HostEvent, any>;
  readonly snapshot: Schema.Schema<Snapshot, any>;
  readonly error?: Schema.Schema<BridgeError, any>;
  readonly initialize: Command;
}

export function defineApplicationContract<Command, Receipt, HostEvent, Snapshot>(
  contract: ApplicationContract<Command, Receipt, HostEvent, Snapshot>,
): ApplicationContract<Command, Receipt, HostEvent, Snapshot> {
  if (contract.identity.length === 0 || !Number.isSafeInteger(contract.version) || contract.version < 1) {
    throw new TypeError("An Application Bridge contract requires a stable identity and positive version.");
  }
  return Object.freeze({
    ...contract,
    error: contract.error ?? BridgeErrorSchema as Schema.Schema<BridgeError, any>,
  });
}

export const ClientEnvelopeSchema = Schema.Struct({
  protocol: Schema.String,
  version: Schema.Int.pipe(Schema.positive()),
  kind: Schema.Literal("initialize", "dispatch", "cancelOperation", "uiReady", "uiRendered"),
  commandId: UuidSchema,
  sessionId: Schema.optional(UuidSchema),
  expectedRevision: Schema.optional(RevisionSchema),
  payload: Schema.Unknown,
});

export type ClientEnvelope = typeof ClientEnvelopeSchema.Type;

export const HostEnvelopeSchema = Schema.Struct({
  protocol: Schema.String,
  version: Schema.Int.pipe(Schema.positive()),
  kind: Schema.Literal("snapshot", "receipt", "event", "error"),
  sessionId: UuidSchema,
  sequence: SequenceSchema,
  revision: RevisionSchema,
  commandId: Schema.optional(UuidSchema),
  operationId: Schema.optional(UuidSchema),
  payload: Schema.Unknown,
});

export type HostEnvelope = typeof HostEnvelopeSchema.Type;
