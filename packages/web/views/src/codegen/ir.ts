// The Views contract IR, format runic.views.contract-ir/1 (specs/application/contract-ir.md and its JSON Schema).

export const CONTRACT_IR_FORMAT = "runic.views.contract-ir/1";

export type Direction = "to-page" | "from-page" | "both";

export type ScalarKind =
  | "boolean" | "int8" | "uint8" | "int16" | "uint16" | "int32" | "uint32" | "int64" | "uint64" | "big-integer"
  | "float32" | "float64" | "decimal" | "string" | "uuid" | "local-date" | "local-time" | "date-time" | "offset-date-time"
  | "duration" | "text-reference" | "blob" | "upload";

export interface CustomTypeScript { readonly type: string; readonly decode: string; readonly encode: string }

export type TypeRef =
  | { readonly kind: ScalarKind; readonly nullable?: true }
  | { readonly kind: "named"; readonly ref: string; readonly nullable?: true }
  | { readonly kind: "list"; readonly element: TypeRef; readonly nullable?: true }
  | { readonly kind: "map"; readonly value: TypeRef; readonly nullable?: true }
  | { readonly kind: "json"; readonly jsonNull: boolean; readonly nullable?: true }
  | { readonly kind: "custom"; readonly codec: string; readonly typescript: CustomTypeScript; readonly nullable?: true };

export interface Value { readonly direction: Direction; readonly type: TypeRef; readonly untyped?: true }

export interface NameHints { readonly source: string; readonly base: string; readonly annotatedBase?: string; readonly qualifiers: readonly string[] }

export interface EnumCase { readonly wire: string; readonly value: string; readonly doc?: string }
export interface UnionCase { readonly tag: string; readonly object: string | null }
export interface ObjectMember { readonly wire: string; readonly type: TypeRef; readonly optional?: true; readonly absent?: true; readonly doc?: string }

export type TypeDefinition =
  | { readonly kind: "enum"; readonly name: NameHints; readonly doc?: string; readonly cases: readonly EnumCase[] }
  | { readonly kind: "object"; readonly name: NameHints; readonly doc?: string; readonly members: readonly ObjectMember[] }
  | { readonly kind: "union"; readonly name: NameHints; readonly doc?: string; readonly cases: readonly UnionCase[] };

export interface PageRef { readonly view: string; readonly name: string; readonly kind: string }

export interface Constraints {
  readonly required?: true; readonly minLength?: number; readonly maxLength?: number; readonly min?: number | string;
  readonly max?: number | string; readonly minExclusive?: true; readonly maxExclusive?: true; readonly pattern?: string;
}

export interface FieldPath { readonly path: readonly string[]; readonly constraints?: Constraints }

interface MemberBase { readonly name: string; readonly wire: string; readonly doc?: string; readonly developmentOnly?: true }

export interface ValueMember extends MemberBase {
  readonly kind: "value" | "selection";
  readonly value: Value;
  readonly settable?: true;
  readonly declaringType?: string;
  readonly checked?: true;
  readonly constraints?: Constraints;
  readonly fieldPaths?: readonly FieldPath[];
  readonly text?: true;
  readonly appendOnlyText?: true;
  readonly collectionKey?: string;
  readonly rowEditor?: true;
}

export interface ContentMember extends MemberBase {
  readonly kind: "content";
  readonly pages: readonly PageRef[];
  readonly nullable?: true;
  readonly region?: true;
}

export interface ContentListMember extends MemberBase {
  readonly kind: "content-list";
  readonly pages: readonly PageRef[];
  readonly nullable?: true;
}

export interface RegionCollectionMember extends MemberBase {
  readonly kind: "region-collection";
  readonly pages: readonly PageRef[];
  readonly pageCanClose: boolean;
}

export interface QueryCollectionMember extends MemberBase {
  readonly kind: "query-collection";
  readonly rows: Value;
  readonly defaultKey?: string;
}

export type ViewMember = ValueMember | ContentMember | ContentListMember | RegionCollectionMember | QueryCollectionMember;

export interface Command {
  readonly name: string; readonly wire: string; readonly doc?: string; readonly parameter?: string;
  readonly input?: Value; readonly result?: Value; readonly failure?: Value;
  readonly async?: true; readonly stream?: true; readonly executionState?: true; readonly keyed?: true;
  readonly scoped?: true; readonly developmentOnly?: true;
}

export interface Interaction {
  readonly name: string; readonly wire: string; readonly doc?: string; readonly contract: string;
  readonly input: Value; readonly output: Value;
}

export interface ViewRequest {
  readonly name: string; readonly wire: string; readonly doc?: string; readonly contract: string; readonly input: Value;
}

export interface View {
  readonly id: string; readonly name: string; readonly route: string; readonly fingerprint: string;
  readonly kinds: readonly string[]; readonly doc?: string; readonly developmentOnly?: true; readonly owner?: string;
  readonly memberErrors?: true; readonly validation?: true; readonly uploads?: true;
  readonly sharedClients?: readonly string[];
  readonly members: readonly ViewMember[]; readonly commands: readonly Command[];
  readonly interactions?: readonly Interaction[]; readonly requests?: readonly ViewRequest[];
}

export type Published = { readonly member: string } | { readonly command: string };

export interface SharedClientMember {
  readonly name: string; readonly doc?: string; readonly settable?: true;
  readonly published: Readonly<Record<string, Published>>;
}

export interface SharedClient {
  readonly id: string; readonly name: string; readonly interface: string; readonly doc?: string; readonly owner?: string;
  readonly views: readonly string[]; readonly members: readonly SharedClientMember[];
}

export interface Location { readonly file: string; readonly line: number; readonly column: number }

export interface ClientModule { readonly unit: string; readonly specifier: string }

export interface ContractIr {
  readonly format: string;
  readonly producer: { readonly language: string; readonly tool: string; readonly unit: string; readonly clientLibrary?: true };
  readonly clientModules?: readonly ClientModule[];
  readonly views: readonly View[];
  readonly sharedClients?: readonly SharedClient[];
  readonly types: Readonly<Record<string, TypeDefinition>>;
  readonly sources: Readonly<Record<string, Location>>;
}
