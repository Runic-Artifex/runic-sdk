import type {
  JsonValue,
  MvvmProjectedCommandInvocation,
  MvvmProjection,
  MvvmProjectionSnapshot,
} from "@webuitoolkit/mvvm";
import type { ComputedRef, ShallowRef } from "vue";
import {
  createVueMvvmAdapter,
  type VueMvvmAdapter,
} from "../../dist/esm/index.js";

declare const projection: MvvmProjection;
const adapter: VueMvvmAdapter = createVueMvvmAdapter(projection);

const state: Readonly<ShallowRef<MvvmProjectionSnapshot>> = adapter.state;
const property: ComputedRef<JsonValue | undefined> = adapter.property(1);
const collection: ComputedRef<readonly JsonValue[] | undefined> = adapter.collection(2);
const validation: ComputedRef<readonly string[] | undefined> = adapter.validation(3);
const invocation: MvvmProjectedCommandInvocation = adapter.execute(4, { argument: 7 });

void state;
void property;
void collection;
void validation;
void invocation;

// @ts-expect-error state is exposed as a read-only ref.
adapter.state.value = projection.snapshot;
// @ts-expect-error protocol member identifiers are numeric.
adapter.property("amount");
// @ts-expect-error protocol values cannot contain undefined.
adapter.setProperty(1, undefined);
