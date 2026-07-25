import type {
  JsonValue,
  MvvmProjection,
  MvvmProjectionSnapshot,
} from "@webuitoolkit/mvvm";
import {
  createSvelteMvvmStore,
  disposeSvelteMvvmStoreOnDestroy,
  type SvelteDestroyRegistrar,
  type SvelteMvvmStore,
} from "@webuitoolkit/mvvm-svelte";
import type { Readable } from "svelte/store";

declare const projection: MvvmProjection;

const store: SvelteMvvmStore = createSvelteMvvmStore(projection, {
  ownsProjection: true,
});
const readable: Readable<MvvmProjectionSnapshot> = store;
const value: JsonValue | undefined = store.property(1);
const unsubscribe = readable.subscribe((snapshot) => {
  const current: JsonValue | undefined = snapshot.properties.get(1);
  void current;
});
const register: SvelteDestroyRegistrar = (cleanup) => cleanup();

disposeSvelteMvvmStoreOnDestroy(store, register);
void store.setProperty(1, value ?? null);
void store.execute<{ readonly submissions: number }>(2).completion;
unsubscribe();
