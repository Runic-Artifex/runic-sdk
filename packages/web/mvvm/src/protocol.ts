export const PROTOCOL_IDENTITY = "runic.toolkit.mvvm/1" as const;
export const PROTOCOL_VERSION = 1 as const;

export const CAPABILITIES = Object.freeze([
  "cancellation",
  "collections",
  "commandResults",
  "patches",
  "validation",
] as const);

export const FAULT_CODES = Object.freeze([
  "protocol.unsupported",
  "request.invalid",
  "member.unknown",
  "revision.stale",
  "limit.exceeded",
  "request.cancelled",
  "request.timeout",
  "session.closed",
] as const);

/** Hard v1 ceilings. Negotiated limits may only lower the applicable values. */
export const PROTOCOL_LIMITS = Object.freeze({
  maxFrameBytes: 1_048_576,
  maxJsonDepth: 32,
  maxStringBytes: 65_536,
  maxPropertyNameBytes: 128,
  maxPropertiesPerObject: 4_096,
  maxArrayItems: 10_000,
  maxContractBytes: 128,
  capabilityTokenCharacters: 43,
  maxCapabilities: 5,
  maxSanitizedMessageBytes: 256,
  maxSessions: 16,
  maxPendingRequests: 64,
  maxSnapshotMembers: 4_096,
  maxPatchChanges: 1_024,
  maxCollectionItems: 10_000,
  maxInsertedOrReplacedItems: 10_000,
  maxValidationErrors: 32,
  maxCommandTimeoutMilliseconds: 300_000,
  defaultCommandTimeoutMilliseconds: 30_000,
} as const);

export type ProtocolVersion = typeof PROTOCOL_VERSION;
export type CapabilityName = (typeof CAPABILITIES)[number];
export type FaultCode = (typeof FAULT_CODES)[number];
export type Revision = bigint;
export type Uuid = string;
export type CapabilityToken = string;
export type ContractIdentifier = string;
export type MemberIdentifier = number;

export type JsonPrimitive = null | boolean | number | string;
export type JsonObject = Readonly<{ [key: string]: JsonValue }>;
export type JsonValue = JsonPrimitive | JsonObject | readonly JsonValue[];
export type EmptyPayload = Readonly<Record<string, never>>;

export interface ProtocolLimits {
  readonly maxFrameBytes: number;
  readonly maxJsonDepth: number;
  readonly maxSessions: number;
  readonly maxPendingRequests: number;
  readonly maxSnapshotMembers: number;
  readonly maxPatchChanges: number;
  readonly maxCollectionItems: number;
  readonly commandTimeoutMilliseconds: number;
}

export interface ProtocolParseLimits {
  readonly maxFrameBytes: number;
  readonly maxJsonDepth: number;
  readonly maxStringBytes: number;
  readonly maxPropertyNameBytes: number;
  readonly maxPropertiesPerObject: number;
  readonly maxArrayItems: number;
}

export interface PropertySnapshotMember {
  readonly type: "property";
  readonly member: MemberIdentifier;
  readonly value: JsonValue;
}

export interface CollectionSnapshotMember {
  readonly type: "collection";
  readonly member: MemberIdentifier;
  readonly items: readonly JsonValue[];
}

export interface CommandSnapshotMember {
  readonly type: "command";
  readonly member: MemberIdentifier;
  readonly canExecute: boolean;
  readonly isExecuting: boolean;
}

export interface ValidationSnapshotMember {
  readonly type: "validation";
  readonly member: MemberIdentifier;
  readonly errors: readonly string[];
}

export type SnapshotMember =
  | PropertySnapshotMember
  | CollectionSnapshotMember
  | CommandSnapshotMember
  | ValidationSnapshotMember;

export interface SnapshotState {
  readonly revision: Revision;
  readonly members: readonly SnapshotMember[];
}

export interface PropertyPatchChange {
  readonly type: "property";
  readonly member: MemberIdentifier;
  readonly value: JsonValue;
}

export type CollectionPatchOperation = "insert" | "remove" | "replace" | "reset";

export interface CollectionPatchChange {
  readonly type: "collection";
  readonly member: MemberIdentifier;
  readonly operation: CollectionPatchOperation;
  readonly index: number;
  readonly items: readonly JsonValue[];
}

export interface CollectionMovePatchChange {
  readonly type: "collectionMove";
  readonly member: MemberIdentifier;
  readonly from: number;
  readonly to: number;
  readonly count: number;
}

export interface CommandPatchChange {
  readonly type: "command";
  readonly member: MemberIdentifier;
  readonly canExecute: boolean;
  readonly isExecuting: boolean;
}

export interface ValidationPatchChange {
  readonly type: "validation";
  readonly member: MemberIdentifier;
  readonly errors: readonly string[];
}

export type PatchChange =
  | PropertyPatchChange
  | CollectionPatchChange
  | CollectionMovePatchChange
  | CommandPatchChange
  | ValidationPatchChange;

export interface ClientHandshakeMessage {
  readonly v: ProtocolVersion;
  readonly kind: "handshake";
  readonly request: Uuid;
  readonly payload: {
    readonly supportedVersions: readonly [ProtocolVersion];
    readonly capabilities: readonly CapabilityName[];
  };
}

export interface ClientOpenMessage {
  readonly v: ProtocolVersion;
  readonly kind: "open";
  readonly contract: ContractIdentifier;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly payload: EmptyPayload;
}

export interface ClientSetPropertyMessage {
  readonly v: ProtocolVersion;
  readonly kind: "setProperty";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly baseRevision: Revision;
  readonly capability: CapabilityToken;
  readonly payload: {
    readonly member: MemberIdentifier;
    readonly value: JsonValue;
  };
}

export interface ClientExecuteMessage {
  readonly v: ProtocolVersion;
  readonly kind: "execute";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly baseRevision: Revision;
  readonly capability: CapabilityToken;
  readonly payload: {
    readonly member: MemberIdentifier;
    readonly argument?: JsonValue;
  };
}

export interface ClientCancelMessage {
  readonly v: ProtocolVersion;
  readonly kind: "cancel";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly capability: CapabilityToken;
  readonly payload: { readonly targetRequest: Uuid };
}

export interface ClientAckMessage {
  readonly v: ProtocolVersion;
  readonly kind: "ack";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly capability: CapabilityToken;
  readonly payload: { readonly revision: Revision };
}

export interface ClientRequestSnapshotMessage {
  readonly v: ProtocolVersion;
  readonly kind: "requestSnapshot";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly capability: CapabilityToken;
  readonly payload: EmptyPayload;
}

export interface ClientCloseMessage {
  readonly v: ProtocolVersion;
  readonly kind: "close";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly capability: CapabilityToken;
  readonly payload: { readonly reason?: string };
}

export type ClientMessage =
  | ClientHandshakeMessage
  | ClientOpenMessage
  | ClientSetPropertyMessage
  | ClientExecuteMessage
  | ClientCancelMessage
  | ClientAckMessage
  | ClientRequestSnapshotMessage
  | ClientCloseMessage;

export interface HostHandshakeResultMessage {
  readonly v: ProtocolVersion;
  readonly kind: "handshakeResult";
  readonly request: Uuid;
  readonly payload: {
    readonly selectedVersion: ProtocolVersion;
    readonly capabilities: readonly CapabilityName[];
    readonly limits: ProtocolLimits;
  };
}

export interface HostOpenedMessage {
  readonly v: ProtocolVersion;
  readonly kind: "opened";
  readonly contract: ContractIdentifier;
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly capability: CapabilityToken;
  readonly payload: { readonly snapshot: SnapshotState };
}

export interface SetPropertyResultPayload {
  readonly operation: "setProperty";
  readonly revision: Revision;
}

export interface ExecuteResultPayload {
  readonly operation: "execute";
  readonly revision: Revision;
  readonly value?: JsonValue;
}

export interface CancelResultPayload {
  readonly operation: "cancel";
  readonly revision: Revision;
  readonly targetRequest: Uuid;
  readonly accepted: boolean;
}

export interface AckResultPayload {
  readonly operation: "ack";
  readonly revision: Revision;
}

export type ResultPayload =
  | SetPropertyResultPayload
  | ExecuteResultPayload
  | CancelResultPayload
  | AckResultPayload;

export interface HostResultMessage {
  readonly v: ProtocolVersion;
  readonly kind: "result";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly payload: ResultPayload;
}

export interface HostSnapshotMessage {
  readonly v: ProtocolVersion;
  readonly kind: "snapshot";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly payload: SnapshotState;
}

export interface HostPatchMessage {
  readonly v: ProtocolVersion;
  readonly kind: "patch";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly payload: {
    readonly fromRevision: Revision;
    readonly toRevision: Revision;
    readonly changes: readonly PatchChange[];
  };
}

export interface FaultPayload {
  readonly code: FaultCode;
  readonly message: string;
  readonly retryable: boolean;
  readonly currentRevision?: Revision;
  readonly snapshotRequired?: boolean;
}

export interface HostPreSessionFaultMessage {
  readonly v: ProtocolVersion;
  readonly kind: "fault";
  readonly request: Uuid;
  readonly payload: FaultPayload;
}

export interface HostSessionFaultMessage {
  readonly v: ProtocolVersion;
  readonly kind: "fault";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly payload: FaultPayload;
}

export interface HostClosedMessage {
  readonly v: ProtocolVersion;
  readonly kind: "closed";
  readonly session: Uuid;
  readonly view: Uuid;
  readonly request: Uuid;
  readonly payload: { readonly revision: Revision; readonly reason: string };
}

export type HostMessage =
  | HostHandshakeResultMessage
  | HostOpenedMessage
  | HostResultMessage
  | HostSnapshotMessage
  | HostPatchMessage
  | HostPreSessionFaultMessage
  | HostSessionFaultMessage
  | HostClosedMessage;
