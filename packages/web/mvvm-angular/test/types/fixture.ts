import type { MvvmProjection } from "@webuitoolkit/mvvm";
import {
  ANGULAR_MVVM_STORE,
  AngularMvvmStore,
  AngularMvvmStoreDirective,
  provideAngularMvvmStore,
} from "../../dist/esm/index.js";

declare const projection: MvvmProjection;
const store = new AngularMvvmStore(projection);
const amount: number | undefined = store.property(1)() as number | undefined;
const provider = provideAngularMvvmStore(store);
const directive = new AngularMvvmStoreDirective();
directive.store = store;
directive.wutMvvmOwnsStore = true;
directive.ngOnDestroy();
void amount;
void provider;
void ANGULAR_MVVM_STORE;
