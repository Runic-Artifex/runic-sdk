import { Schema } from "effect";
import { BridgeErrorSchema, type BridgeError } from "./errors.js";

export const UuidSchema = Schema.String.pipe(
  Schema.pattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i),
);
export const RevisionSchema = Schema.Int.pipe(Schema.nonNegative());
export const SequenceSchema = Schema.Int.pipe(Schema.positive());
const HostSequenceSchema = Schema.Int.pipe(Schema.nonNegative());

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
  readonly command: Schema.Schema<Command, CommandEncoded, never>;
  readonly receipt: Schema.Schema<Receipt, ReceiptEncoded, never>;
  readonly event: Schema.Schema<HostEvent, HostEventEncoded, never>;
  readonly snapshot: Schema.Schema<Snapshot, SnapshotEncoded, never>;
  readonly error: Schema.Schema<Failure, FailureEncoded, never>;
  readonly initialize: Command;
}

export interface ApplicationBridgeCommand<
  Command extends Schema.Schema.AnyNoContext,
  Receipt extends Schema.Schema.AnyNoContext,
> {
  readonly schema: Command;
  readonly receipt: Receipt;
  readonly startsOperation: boolean;
  readonly cancellable: boolean;
  readonly advancesRevision: boolean;
}

export interface ApplicationBridgeDefinition<
  Snapshot extends Schema.Schema.AnyNoContext = Schema.Schema.AnyNoContext,
  Commands extends readonly ApplicationBridgeCommand<Schema.Schema.AnyNoContext, Schema.Schema.AnyNoContext>[] =
    readonly ApplicationBridgeCommand<Schema.Schema.AnyNoContext, Schema.Schema.AnyNoContext>[],
  Events extends readonly Schema.Schema.AnyNoContext[] = readonly Schema.Schema.AnyNoContext[],
  Errors extends readonly Schema.Schema.AnyNoContext[] = readonly Schema.Schema.AnyNoContext[],
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
  readonly initialize?: CommandType<Commands[number]>;
}

type CommandType<Item> = Item extends ApplicationBridgeCommand<infer Command, Schema.Schema.AnyNoContext>
  ? Schema.Schema.Type<Command>
  : never;
type ReceiptType<Item> = Item extends ApplicationBridgeCommand<Schema.Schema.AnyNoContext, infer Receipt>
  ? Schema.Schema.Type<Receipt>
  : never;
type EventType<Items extends readonly Schema.Schema.AnyNoContext[]> = Schema.Schema.Type<Items[number]>;
type CommandEncoded<Item> = Item extends ApplicationBridgeCommand<infer Command, Schema.Schema.AnyNoContext>
  ? Schema.Schema.Encoded<Command>
  : never;
type ReceiptEncoded<Item> = Item extends ApplicationBridgeCommand<Schema.Schema.AnyNoContext, infer Receipt>
  ? Schema.Schema.Encoded<Receipt>
  : never;
type EventEncoded<Items extends readonly Schema.Schema.AnyNoContext[]> = Schema.Schema.Encoded<Items[number]>;
type ErrorType<Items extends readonly Schema.Schema.AnyNoContext[]> = Schema.Schema.Type<Items[number]>;
type ErrorEncoded<Items extends readonly Schema.Schema.AnyNoContext[]> = Schema.Schema.Encoded<Items[number]>;

export const bridge = Object.freeze({
  command<Command extends Schema.Schema.AnyNoContext, Receipt extends Schema.Schema.AnyNoContext>(
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
  const Snapshot extends Schema.Schema.AnyNoContext,
  const Commands extends readonly ApplicationBridgeCommand<Schema.Schema.AnyNoContext, Schema.Schema.AnyNoContext>[],
  const Events extends readonly Schema.Schema.AnyNoContext[],
  const Errors extends readonly Schema.Schema.AnyNoContext[] = readonly [],
>(
  definition: ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors> &
    (Commands extends readonly [] ? object : Readonly<{ initialize: CommandType<Commands[number]> }>),
): ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors> {
  if (definition.protocol.identity.length === 0 ||
      !Number.isSafeInteger(definition.protocol.version) || definition.protocol.version < 1 ||
      definition.csharp.namespace.length === 0 || definition.csharp.contractName.length === 0) {
    throw new TypeError("An Application Bridge definition requires a protocol and C# projection.");
  }
  return Object.freeze({ ...definition }) as ApplicationBridgeDefinition<Snapshot, Commands, Events, Errors>;
}

export function materializeApplicationBridgeContract<
  const Snapshot extends Schema.Schema.AnyNoContext,
  const Commands extends readonly ApplicationBridgeCommand<Schema.Schema.AnyNoContext, Schema.Schema.AnyNoContext>[],
  const Events extends readonly Schema.Schema.AnyNoContext[],
  const Errors extends readonly Schema.Schema.AnyNoContext[],
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
  Schema.Schema.Encoded<Snapshot>,
  Schema.Schema.Encoded<typeof BridgeErrorSchema> | ErrorEncoded<Errors>
> {
  if (!/^[0-9a-f]{64}$/.test(fingerprint)) {
    throw new TypeError("An Application Bridge contract requires a generated SHA-256 fingerprint.");
  }
  if (definition.commands.length > 0 && definition.initialize === undefined) {
    throw new TypeError("A materialized Application Bridge contract with commands requires an initialize command.");
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
    error: union([BridgeErrorSchema, ...domainErrors]) as unknown as Schema.Schema<
      BridgeError | ErrorType<Errors>,
      Schema.Schema.Encoded<typeof BridgeErrorSchema> | ErrorEncoded<Errors>,
      never
    >,
    initialize: definition.initialize as CommandType<Commands[number]>,
  });
}

function union<const Schemas extends readonly Schema.Schema.AnyNoContext[]>(
  schemas: Schemas,
): Schema.Schema<Schema.Schema.Type<Schemas[number]>, Schema.Schema.Encoded<Schemas[number]>, never> {
  if (schemas.length === 0) {
    return Schema.Never as unknown as Schema.Schema<
      Schema.Schema.Type<Schemas[number]>,
      Schema.Schema.Encoded<Schemas[number]>,
      never
    >;
  }
  if (schemas.length === 1) return schemas[0]!;
  return Schema.Union(schemas[0]!, schemas[1]!, ...schemas.slice(2)) as unknown as Schema.Schema<
    Schema.Schema.Type<Schemas[number]>,
    Schema.Schema.Encoded<Schemas[number]>,
    never
  >;
}

export const ClientEnvelopeSchema = Schema.Struct({
  protocol: Schema.String,
  version: Schema.Int.pipe(Schema.positive()),
  contractFingerprint: Schema.String.pipe(Schema.pattern(/^[0-9a-f]{64}$/)),
  connectionEpoch: Schema.Int.pipe(Schema.nonNegative()),
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
  contractFingerprint: Schema.String.pipe(Schema.pattern(/^[0-9a-f]{64}$/)),
  connectionEpoch: Schema.Int.pipe(Schema.nonNegative()),
  kind: Schema.Literal("snapshot", "receipt", "event", "error"),
  sessionId: UuidSchema,
  sequence: HostSequenceSchema,
  revision: RevisionSchema,
  commandId: Schema.optional(UuidSchema),
  operationId: Schema.optional(UuidSchema),
  payload: Schema.Unknown,
});

export type HostEnvelope = typeof HostEnvelopeSchema.Type;
