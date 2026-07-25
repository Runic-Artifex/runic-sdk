import {
  Directive,
  Input,
  type OnDestroy,
} from "@angular/core";

import {
  AngularMvvmDirectiveLifetime,
  type AngularMvvmStore,
} from "./store.js";

/**
 * Standalone DataContext-style directive whose lifetime can own one supplied
 * store. The ownership kernel is separately browser-testable under strict CSP.
 */
@Directive({
  selector: "[wutMvvmStore]",
  standalone: true,
  exportAs: "wutMvvmStore",
})
export class AngularMvvmStoreDirective implements OnDestroy {
  private readonly lifetime = new AngularMvvmDirectiveLifetime();

  @Input()
  public set wutMvvmOwnsStore(value: boolean) {
    this.lifetime.wutMvvmOwnsStore = value;
  }

  public get wutMvvmOwnsStore(): boolean {
    return this.lifetime.wutMvvmOwnsStore;
  }

  @Input({ alias: "wutMvvmStore", required: true })
  public set store(value: AngularMvvmStore) {
    this.lifetime.store = value;
  }

  public get dataContext(): AngularMvvmStore {
    return this.lifetime.dataContext;
  }

  public ngOnDestroy(): void {
    this.lifetime.destroy();
  }
}
