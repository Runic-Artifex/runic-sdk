export type BridgeIrLiteral = string | number | boolean | null;

export interface BridgeIrConstraints {
  readonly minimum?: number;
  readonly maximum?: number;
  readonly exclusiveMinimum?: number;
  readonly exclusiveMaximum?: number;
  readonly multipleOf?: number;
  readonly minLength?: number;
  readonly maxLength?: number;
  readonly pattern?: string;
  readonly minItems?: number;
  readonly maxItems?: number;
  readonly uniqueItems?: boolean;
}

export type BridgeIrNode =
  | Readonly<{ kind: "string"; constraints?: BridgeIrConstraints }>
  | Readonly<{ kind: "number"; constraints?: BridgeIrConstraints }>
  | Readonly<{ kind: "integer"; constraints?: BridgeIrConstraints }>
  | Readonly<{ kind: "boolean" }>
  | Readonly<{ kind: "null" }>
  | Readonly<{ kind: "literal"; value: BridgeIrLiteral }>
  | Readonly<{ kind: "array"; items: BridgeIrNode; constraints?: BridgeIrConstraints }>
  | Readonly<{
      kind: "tuple";
      elements: readonly Readonly<{ type: BridgeIrNode; optional: boolean }>[];
      rest?: BridgeIrNode;
      constraints?: BridgeIrConstraints;
    }>
  | Readonly<{
      kind: "object";
      properties: Readonly<Record<string, Readonly<{ type: BridgeIrNode; optional: boolean }>>>;
    }>
  | Readonly<{ kind: "record"; keyPattern?: string; values: BridgeIrNode }>
  | Readonly<{ kind: "union"; members: readonly BridgeIrNode[] }>
  | Readonly<{ kind: "ref"; name: string }>;

export interface BridgeIr {
  readonly format: "runic.application-bridge-ir";
  readonly formatVersion: 1;
  readonly fingerprint: Readonly<{
    algorithm: "sha256";
    scope: "wire";
    value: string;
  }>;
  readonly wire: Readonly<{
    protocol: Readonly<{ identity: string; version: number }>;
    envelopeVersion: 1;
    limits: Readonly<{
      maxFrameBytes: number;
      maxDepth: number;
      maxStringBytes: number;
      maxCollectionItems: number;
      maxPendingCommands: number;
    }>;
    initialize?: string;
    snapshot: string;
    definitions: Readonly<Record<string, BridgeIrNode>>;
    commands: readonly Readonly<{
      name: string;
      receipt: string;
      startsOperation: boolean;
      cancellable: boolean;
      advancesRevision: boolean;
    }>[];
    events: readonly string[];
    errors: readonly string[];
  }>;
  readonly csharp: Readonly<{ namespace: string; contractName: string }>;
  readonly documentation: Readonly<Record<string, string>>;
}
