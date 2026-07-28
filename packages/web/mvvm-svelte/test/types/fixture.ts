import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmReadonlyProperty,
  type JsonValue,
  type MvvmProjection,
  type MvvmProjectionSnapshot,
} from "@webuitoolkit/mvvm";
import {
  createSvelteMvvmStore,
  createSvelteMvvmCommandFacade,
  derivedMvvmCollection,
  derivedMvvmCommand,
  derivedMvvmProperty,
  derivedMvvmValidation,
  disposeSvelteMvvmStoreOnDestroy,
  type SvelteDestroyRegistrar,
  type SvelteMvvmStore,
} from "@webuitoolkit/mvvm-svelte";
import { toSvelteMvvmRune } from "@webuitoolkit/mvvm-svelte/runes";
import type { Readable } from "svelte/store";

declare const projection: MvvmProjection;

const store: SvelteMvvmStore = createSvelteMvvmStore(projection, {
  ownsProjection: true,
});
const readable: Readable<MvvmProjectionSnapshot> = store;
const amountHandle = new MvvmReadonlyProperty<number>(projection, 1);
const itemsHandle = new MvvmCollection<{ readonly id: string }>(projection, 2);
const submitHandle = new MvvmCommandWithArgument<number, string>(projection, 3);
const typedAmount: Readable<number | undefined> = derivedMvvmProperty(store, amountHandle);
const typedItems: Readable<readonly { readonly id: string }[]> =
  derivedMvvmCollection(store, itemsHandle);
const typedCommand = derivedMvvmCommand(store, submitHandle);
const commandFacade = createSvelteMvvmCommandFacade(store, submitHandle);
const commandRune = toSvelteMvvmRune(commandFacade);
const typedValidation: Readable<readonly string[]> =
  derivedMvvmValidation(store, amountHandle);
const value: JsonValue | undefined = store.property(1);
const unsubscribe = readable.subscribe((snapshot) => {
  const current: JsonValue | undefined = snapshot.properties.get(1);
  void current;
});
const register: SvelteDestroyRegistrar = (cleanup) => cleanup();

disposeSvelteMvvmStoreOnDestroy(store, register);
void store.setProperty(1, value ?? null);
void store.execute<{ readonly submissions: number }>(2).completion;
void submitHandle.execute(4);
void typedAmount;
void typedItems;
void typedCommand;
void commandFacade.execute(4).completion;
void commandRune.current.status;
void typedValidation;
// @ts-expect-error generated command arguments stay strongly typed.
submitHandle.execute("4");
unsubscribe();
