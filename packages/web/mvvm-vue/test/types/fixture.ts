import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmProperty,
  type JsonValue,
  type MvvmProjectedCommandInvocation,
  type MvvmProjection,
  type MvvmProjectionSnapshot,
} from "@webuitoolkit/mvvm";
import type { ComputedRef, ShallowRef } from "vue";
import {
  createVueMvvmAdapter,
  toVueMvvmCollection,
  toVueMvvmCommand,
  toVueMvvmCommandFacade,
  toVueMvvmProperty,
  toVueMvvmValidation,
  useVueMvvmCollection,
  useVueMvvmCommand,
  useVueMvvmCommandFacade,
  useVueMvvmProperty,
  useVueMvvmValidation,
  type VueMvvmAdapter,
} from "../../dist/esm/index.js";

declare const projection: MvvmProjection;
const adapter: VueMvvmAdapter = createVueMvvmAdapter(projection);

const state: Readonly<ShallowRef<MvvmProjectionSnapshot>> = adapter.state;
const property: ComputedRef<JsonValue | undefined> = adapter.property(1);
const collection: ComputedRef<readonly JsonValue[] | undefined> = adapter.collection(2);
const validation: ComputedRef<readonly string[] | undefined> = adapter.validation(3);
const invocation: MvvmProjectedCommandInvocation = adapter.execute(4, { argument: 7 });
const amountHandle = new MvvmProperty<number>(projection, 1);
const itemsHandle = new MvvmCollection<{ readonly id: string }>(projection, 2);
const submitHandle = new MvvmCommandWithArgument<number, string>(projection, 4);
const typedAmount: ComputedRef<number | undefined> =
  toVueMvvmProperty(adapter, amountHandle);
const typedItems: ComputedRef<readonly { readonly id: string }[]> =
  toVueMvvmCollection(adapter, itemsHandle);
const typedCommand = toVueMvvmCommand(adapter, submitHandle);
const commandFacade = toVueMvvmCommandFacade(adapter, submitHandle);
const typedValidation = toVueMvvmValidation(adapter, amountHandle);
const injectedAmount = useVueMvvmProperty(amountHandle);
const injectedItems = useVueMvvmCollection(itemsHandle);
const injectedCommand = useVueMvvmCommand(submitHandle);
const injectedCommandFacade = useVueMvvmCommandFacade(submitHandle);
const injectedValidation = useVueMvvmValidation(amountHandle);
void submitHandle.execute(7);

void state;
void property;
void collection;
void validation;
void invocation;
void typedAmount;
void typedItems;
void typedCommand;
void commandFacade.execute(7).completion;
void typedValidation;
void injectedAmount;
void injectedItems;
void injectedCommand;
void injectedCommandFacade.result.value;
void injectedValidation;

// @ts-expect-error state is exposed as a read-only ref.
adapter.state.value = projection.snapshot;
// @ts-expect-error protocol member identifiers are numeric.
adapter.property("amount");
// @ts-expect-error protocol values cannot contain undefined.
adapter.setProperty(1, undefined);
// @ts-expect-error generated command arguments stay strongly typed.
submitHandle.execute("7");
