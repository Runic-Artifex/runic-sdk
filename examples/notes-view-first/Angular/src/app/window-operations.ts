import { inject, Injectable, type Signal } from "@angular/core";
import type { ViewClient } from "@runic-artifex/views";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";

interface DisposableView { dispose(): void; }

/** Connects a page whose disposal waits for its queued window operations. */
export function injectPage<C extends ViewClient>(page: Signal<{ connect(): Promise<C> }>) {
  const operations = inject(WindowOperations);
  return injectView(page, { release: client => operations.release(client) });
}
interface Lease { pending: number; releaseRequested: boolean; disposed: boolean; }

/** App-local experiment. One instance belongs to this browser window. */
@Injectable({ providedIn: "root" })
export class WindowOperations {
  readonly ordered = new URLSearchParams(location.search).get("mode") !== "baseline";
  readonly dispatchProbe = new URLSearchParams(location.search).get("mode") === "dispatch-probe";
  private tail: Promise<void> = Promise.resolve();
  private readonly leases = new WeakMap<DisposableView, Lease>();

  run<V extends DisposableView, T>(view: V | undefined, action: (view: V) => Promise<T>): Promise<T> {
    if (!view) return Promise.reject(new Error("The view is not connected."));
    if (!this.ordered) return action(view);

    const lease = this.lease(view);
    if (lease.releaseRequested) return Promise.reject(new Error("The view has left its component."));
    lease.pending++;
    const call = this.tail.then(() => action(view));
    this.tail = call.then(() => undefined, () => undefined);
    return call.finally(() => {
      lease.pending--;
      this.disposeWhenReleased(view, lease);
    });
  }

  /**
   * Starts a command before admitting later calls, without waiting for it to complete. The
   * dispatch probe uses it for Save; navigation uses it because a guard may wait for a later call.
   */
  runDispatched<V extends DisposableView, T>(view: V | undefined, action: (view: V) => Promise<T>): Promise<T> {
    if (!view) return Promise.reject(new Error("The view is not connected."));
    const lease = this.lease(view);
    if (lease.releaseRequested) return Promise.reject(new Error("The view has left its component."));
    lease.pending++;
    const dispatch = this.tail.then(() => ({ completion: action(view) }));
    this.tail = dispatch.then(() => undefined, () => undefined);
    return dispatch.then(({ completion }) => completion).finally(() => {
      lease.pending--;
      this.disposeWhenReleased(view, lease);
    });
  }

  release(view: DisposableView): void {
    if (!this.ordered) { view.dispose(); return; }
    const lease = this.lease(view);
    lease.releaseRequested = true;
    this.disposeWhenReleased(view, lease);
  }

  private lease(view: DisposableView): Lease {
    let lease = this.leases.get(view);
    if (!lease) {
      lease = { pending: 0, releaseRequested: false, disposed: false };
      this.leases.set(view, lease);
    }
    return lease;
  }

  private disposeWhenReleased(view: DisposableView, lease: Lease): void {
    if (!lease.releaseRequested || lease.pending || lease.disposed) return;
    lease.disposed = true;
    view.dispose();
  }
}
