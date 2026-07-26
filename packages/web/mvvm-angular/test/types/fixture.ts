import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmReadonlyProperty,
  type MvvmProjection,
} from "@webuitoolkit/mvvm";
import type { Signal } from "@angular/core";
import {
  ANGULAR_MVVM_STORE,
  AngularMvvmStore,
  AngularMvvmStoreDirective,
  provideAngularMvvmStore,
} from "../../dist/esm/index.js";

declare const projection: MvvmProjection;
const store = new AngularMvvmStore(projection);
const amount: number | undefined = store.property(1)() as number | undefined;
const amountHandle = new MvvmReadonlyProperty<number>(projection, 1);
const itemsHandle = new MvvmCollection<{ readonly id: string }>(projection, 2);
const submitHandle = new MvvmCommandWithArgument<number, string>(projection, 3);
const typedAmount: Signal<number | undefined> = store.property(amountHandle);
const typedItems: Signal<readonly { readonly id: string }[]> =
  store.collection(itemsHandle);
const typedCommand = store.command(submitHandle);
const typedValidation = store.validation(amountHandle);
void submitHandle.execute(7);
// @ts-expect-error generated command arguments stay strongly typed.
submitHandle.execute("7");
const provider = provideAngularMvvmStore(store);
const directive = new AngularMvvmStoreDirective();
directive.store = store;
directive.wutMvvmOwnsStore = true;
directive.ngOnDestroy();
void amount;
void provider;
void ANGULAR_MVVM_STORE;
void typedAmount;
void typedItems;
void typedCommand;
void typedValidation;
