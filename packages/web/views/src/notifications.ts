// Subscriber notifications of View state (README, "Notifications"). The runtime applies each push at once, but
// delivers the state it produced to subscribers once per animation frame, so a burst of frames renders once. A page
// that is hidden gets no animation frames, so a timer delivers them instead, and an environment without animation
// frames gets them at the end of the current task. Anything a caller awaits delivers the
// pending notifications first (`flushNotifications`), and so does the mock Bridge after each push.
//
// The queue is page-wide, shared by every copy of this package, so one flush delivers the notifications of every route.

interface NotificationQueue {
  readonly pending: Set<() => void>;
  cancel: (() => void) | undefined;
}

const queueKey = Symbol.for("runic.views.notifications");
// A frame requested while the page was visible still arrives if the page is hidden before it: the timer delivers it.
const frameFallback = 100;
// A hidden page: about one display refresh.
const timerDelay = 16;

function queue(): NotificationQueue {
  const page = globalThis as unknown as Record<symbol, NotificationQueue | undefined>;
  return page[queueKey] ??= { pending: new Set(), cancel: undefined };
}

type FrameHost = {
  readonly requestAnimationFrame?: (callback: () => void) => number;
  readonly cancelAnimationFrame?: (handle: number) => void;
  readonly document?: { readonly visibilityState?: string };
};

function schedule(run: () => void): () => void {
  const host = globalThis as FrameHost;
  const request = host.requestAnimationFrame;
  if (typeof request !== "function") {
    // No rendering to pace, as in a test runner or a worker without frames: the end of the current task.
    let active = true;
    queueMicrotask(() => { if (active) run(); });
    return () => { active = false; };
  }
  if (host.document?.visibilityState === "hidden") {
    const timer = setTimeout(run, timerDelay);
    return () => clearTimeout(timer);
  }
  let frame = 0;
  const timer = setTimeout(() => { host.cancelAnimationFrame?.(frame); run(); }, frameFallback);
  frame = request.call(globalThis, () => { clearTimeout(timer); run(); });
  return () => { clearTimeout(timer); host.cancelAnimationFrame?.(frame); };
}

/** Delivers the notifications of `deliver` with the next animation frame, or at the next flush. */
export function deferNotification(deliver: () => void): void {
  const notifications = queue();
  notifications.pending.add(deliver);
  notifications.cancel ??= schedule(flushNotifications);
}

/**
 * Delivers every pending notification now. A notification that a subscriber causes while they are delivered waits for
 * the next frame.
 */
export function flushNotifications(): void {
  const notifications = queue();
  notifications.cancel?.();
  notifications.cancel = undefined;
  if (notifications.pending.size === 0) return;
  const due = [...notifications.pending];
  notifications.pending.clear();
  for (const deliver of due) deliver();
}
