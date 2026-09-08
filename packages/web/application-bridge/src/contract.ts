import { Schema } from "effect";
import { BridgeErrorSchema, type BridgeError } from "./errors.js";

export const UuidSchema = Schema.String.pipe(
  Schema.check(Schema.isPattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/)),
);
export const RevisionSchema = Schema.Int.pipe(Schema.check(Schema.isGreaterThanOrEqualTo(0)));
export const SequenceSchema = Schema.Int.pipe(Schema.check(Schema.isGreaterThan(0)));
const HostSequenceSchema = Schema.Int.pipe(Schema.check(Schema.isGreaterThanOrEqualTo(0)));

export interface ApplicationContract<
  Command,
  Receipt,
  HostEvent,
  Snapshot,
  Failure = BridgeError,
  CommandEncoded = unknown,
  ReceiptEncoded = unknown,
  HostEventEncoded = unknown,
  SnapshotEncoded = unknown,
  FailureEncoded = unknown,
> {
  readonly identity: string;
  readonly version: number;
  /** SHA-256 of the generated canonical wire contract. */
  readonly fingerprint: string;
  readonly command: Schema.Codec<Command, CommandEncoded, never>;
  readonly receipt: Schema.Codec<Receipt, ReceiptEncoded, never>;
  readonly event: Schema.Codec<HostEvent, HostEventEncoded, never>;
  readonly snapshot: Schema.Codec<Snapshot, SnapshotEncoded, never>;
  readonly error: Schema.Codec<Failure, FailureEncoded, never>;
}

export interface ApplicationBridgeCommand<
  Command extends Schema.Codec<any, any, never, never>,
  Receipt extends Schema.Codec<any, any, never, never>,
> {
  readonly schema: Command;
  readonly receipt: Receipt;
  readonly startsOperation: boolean;
  readonly cancellable: boolean;
  readonly advancesRevision: boolean;
}

export interface ApplicationBridgeDefinition<
  Snapshot extends Schema.Codec<any, any, never, never> = Schema.Codec<any, any, never, never>,
  Commands extends readonly ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, Schema.Codec<any, any, never, never>>[] =
    readonly ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, Schema.Codec<any, any, never, never>>[],
  Events extends readonly Schema.Codec<any, any, never, never>[] = readonly Schema.Codec<any, any, never, never>[],
  Errors extends readonly Schema.Codec<any, any, never, never>[] = readonly Schema.Codec<any, any, never, never>[],
> {
  readonly protocol: Readonly<{ identity: string; version: number }>;
  readonly csharp: Readonly<{ namespace: string; contractName: string }>;
  readonly limits?: Readonly<{
    maxFrameBytes?: number;
    maxDepth?: number;
    maxStringBytes?: number;
    maxCollectionItems?: number;
    maxPendingCommands?: number;
  }>;
  readonly snapshot: Snapshot;
  readonly commands: Commands;
  readonly events: Events;
  readonly errors?: Errors;
}

type CommandType<Item> = Item extends ApplicationBridgeCommand<infer Command, Schema.Codec<any, any, never, never>>
  ? Schema.Schema.Type<Command>
  : never;
type ReceiptType<Item> = Item extends ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, infer Receipt>
  ? Schema.Schema.Type<Receipt>
  : never;
type EventType<Items extends readonly Schema.Codec<any, any, never, never>[]> = Schema.Schema.Type<Items[number]>;
type CommandEncoded<Item> = Item extends ApplicationBridgeCommand<infer Command, Schema.Codec<any, any, never, never>>
  ? Schema.Codec.Encoded<Command>
  : never;
type ReceiptEncoded<Item> = Item extends ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, infer Receipt>
  ? Schema.Codec.Encoded<Receipt>
  : never;
type EventEncoded<Items extends readonly Schema.Codec<any, any, never, never>[]> = Schema.Codec.Encoded<Items[number]>;
type ErrorType<Items extends readonly Schema.Codec<any, any, never, never>[]> = Schema.Schema.Type<Items[number]>;
type ErrorEncoded<Items extends readonly Schema.Codec<any, any, never, never>[]> = Schema.Codec.Encoded<Items[number]>;

export const bridge = Object.freeze({
  command<Command extends Schema.Codec<any, any, never, never>, Receipt extends Schema.Codec<any, any, never, never>>(
    schema: Command,
    metadata: Readonly<{
      receipt: Receipt;
      startsOperation?: boolean;
      cancellable?: boolean;
      advancesRevision?: boolean;
    }>,
  ): ApplicationBridgeCommand<Command, Receipt> {
    return Object.freeze({
      schema,
      receipt: metadata.receipt,
      startsOperation: metadata.startsOperation ?? false,
      cancellable: metadata.cancellable ?? false,
      advancesRevision: metadata.advancesRevision ?? false,
    });
  },
});

export function defineApplicationBridgeContract<
  const Snapshot extends Schema.Codec<any, any, never, never>,
  const Commands extends readonly ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, Schema.Codec<any, any, never, never>>[],
  const Events extends readonly Schema.Codec<any, any, never, never>[],
  const Errors extends readonly Schema.Codec<any, any, never, never>[] = readonly [],
>(
  definition: ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors>,
): ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors> {
  if (definition.protocol.identity.length === 0 ||
      !Number.isSafeInteger(definition.protocol.version) || definition.protocol.version < 1 ||
      definition.csharp.namespace.length === 0 || definition.csharp.contractName.length === 0) {
    throw new TypeError("An Application Bridge definition requires a protocol and C# projection.");
  }
  return Object.freeze({ ...definition }) as ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors>;
}

export function materializeApplicationBridgeContract<
  const Snapshot extends Schema.Codec<any, any, never, never>,
  const Commands extends readonly ApplicationBridgeCommand<Schema.Codec<any, any, never, never>, Schema.Codec<any, any, never, never>>[],
  const Events extends readonly Schema.Codec<any, any, never, never>[],
  const Errors extends readonly Schema.Codec<any, any, never, never>[],
>(
  definition: ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors>,
  fingerprint: string,
): ApplicationContract<
  CommandType<Commands[number]>,
  ReceiptType<Commands[number]>,
  EventType<Events>,
  Schema.Schema.Type<Snapshot>,
  BridgeError | ErrorType<Errors>,
  CommandEncoded<Commands[number]>,
  ReceiptEncoded<Commands[number]>,
  EventEncoded<Events>,
  Schema.Codec.Encoded<Snapshot>,
  Schema.Codec.Encoded<typeof BridgeErrorSchema> | ErrorEncoded<Errors>
> {
  if (!/^[0-9a-f]{64}$/.test(fingerprint)) {
    throw new TypeError("An Application Bridge contract requires a generated SHA-256 fingerprint.");
  }
  const commandSchemas = definition.commands.map((item) => item.schema);
  const receiptSchemas = [...new Set(definition.commands.map((item) => item.receipt))];
  const domainErrors = definition.errors ?? [];
  return Object.freeze({
    identity: definition.protocol.identity,
    version: definition.protocol.version,
    fingerprint,
    command: union(commandSchemas),
    receipt: union(receiptSchemas),
    event: union(definition.events),
    snapshot: definition.snapshot,
    error: union([BridgeErrorSchema, ...domainErrors]) as unknown as Schema.Codec<
      BridgeError | ErrorType<Errors>,
      Schema.Codec.Encoded<typeof BridgeErrorSchema> | ErrorEncoded<Errors>,
      never
    >,
  });
}

function union<const Schemas extends readonly Schema.Codec<any, any, never, never>[]>(
  schemas: Schemas,
): Schema.Codec<Schema.Schema.Type<Schemas[number]>, Schema.Codec.Encoded<Schemas[number]>, never> {
  if (schemas.length === 0) {
    return Schema.Never as unknown as Schema.Codec<
      Schema.Schema.Type<Schemas[number]>,
      Schema.Codec.Encoded<Schemas[number]>,
      never
    >;
  }
  if (schemas.length === 1) return schemas[0]!;
  return Schema.Union([schemas[0]!, schemas[1]!, ...schemas.slice(2)]) as unknown as Schema.Codec<
    Schema.Schema.Type<Schemas[number]>,
    Schema.Codec.Encoded<Schemas[number]>,
    never
  >;
}

export const ClientEnvelopeSchema = Schema.Struct({
  protocol: Schema.String,
  version: Schema.Int.pipe(Schema.check(Schema.isGreaterThan(0))),
  contractFingerprint: Schema.String.pipe(Schema.check(Schema.isPattern(/^[0-9a-f]{64}$/))),
  connectionEpoch: Schema.Int.pipe(Schema.check(Schema.isGreaterThanOrEqualTo(0))),
  kind: Schema.Literals(["initialize", "dispatch", "cancelOperation", "uiReady", "uiRendered"]),
  commandId: UuidSchema,
  sessionId: Schema.optional(UuidSchema),
  expectedRevision: Schema.optional(RevisionSchema),
  payload: Schema.Unknown,
});

export type ClientEnvelope = typeof ClientEnvelopeSchema.Type;

export const HostEnvelopeSchema = Schema.Struct({
  protocol: Schema.String,
  version: Schema.Int.pipe(Schema.check(Schema.isGreaterThan(0))),
  contractFingerprint: Schema.String.pipe(Schema.check(Schema.isPattern(/^[0-9a-f]{64}$/))),
  connectionEpoch: Schema.Int.pipe(Schema.check(Schema.isGreaterThanOrEqualTo(0))),
  kind: Schema.Literals(["snapshot", "receipt", "event", "error"]),
  sessionId: UuidSchema,
  sequence: HostSequenceSchema,
  revision: RevisionSchema,
  commandId: Schema.optional(UuidSchema),
  operationId: Schema.optional(UuidSchema),
  payload: Schema.Unknown,
});

export type HostEnvelope = typeof HostEnvelopeSchema.Type;
