import { Injectable } from "@angular/core";

interface DisposableView { dispose(): void; }
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

  /** Diagnostic only: starts a command before admitting later calls. */
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
