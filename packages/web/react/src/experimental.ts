// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { createElement, useCallback, useEffect, useRef, useState, type HTMLAttributes, type ReactNode } from "react";
import {
  defineHostedRegion,
  hostedRegionTag,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
  type RunicHostedRegionElement,
} from "@runic-artifex/views/experimental";

export type {
  HostedRegionSizing, HostedRegionState, HostedRegionStateChangeDetail, RunicHostedRegionElement,
} from "@runic-artifex/views/experimental";

/** How a region's loading goes, for its fallback and failure content. */
export interface HostedRegionStatus {
  /** Whether the region is `loading` and its frame has loaded without the guest's page connecting. */
  readonly stalled: boolean;
  /** The host application's reason for `failed`, or `null`. */
  readonly failureReason: string | null;
  /**
   * Asks the host application to load a `failed` region again; it decides. Returns whether the request was sent: false
   * unless the region is `failed`.
   */
  readonly retry: () => boolean;
}

/** Props of {@link RunicHostedRegion}. Other HTML attributes, such as `className`, `style` and `id`, go to the element. */
export interface RunicHostedRegionProps extends Omit<HTMLAttributes<HTMLElement>, "children" | "title"> {
  /** The region's name, which the host application's policy embeds a guest's Window in. */
  readonly name: string;
  /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
  readonly sizing?: HostedRegionSizing;
  /** The region's accessible name; without it, the guest page's title. */
  readonly label?: string;
  /**
   * What the region shows while no guest page shows in it, unless `stalled` or `failed` content shows instead: content,
   * or a function of the region's state and status.
   */
  readonly fallback?: ReactNode | ((state: HostedRegionState, status: HostedRegionStatus) => ReactNode);
  /** What a stalled region shows in place of its `loading` fallback; without it, the fallback shows. */
  readonly stalled?: ReactNode;
  /**
   * What a `failed` region shows, such as its reason and a button that calls `retry`: content, or a function of the
   * region's status. Without it, the fallback shows.
   */
  readonly failed?: ReactNode | ((status: HostedRegionStatus) => ReactNode);
  /** The region's state changed, or only whether it is stalled or the reason it failed. */
  readonly onStateChange?: (state: HostedRegionState, previous: HostedRegionState, detail: HostedRegionStateChangeDetail) => void;
  /** The guest page's title as the host passed it on, or `null`. */
  readonly onTitleChange?: (title: string | null) => void;
}

/**
 * A region where the host application shows a hosted guest's Window (`<runic-hosted-region>`), in a frame the page never
 * gets a reference to:
 *
 * ```tsx
 * <RunicHostedRegion name="check" style={{ height: 420 }}
 *   fallback={state => state === "ended" ? <RunAgain /> : <p>Starting the check…</p>}
 *   stalled={<p>The check is taking longer than usual…</p>}
 *   failed={({ failureReason, retry }) => <p>The check could not start ({failureReason}). <button onClick={retry}>Try again</button></p>} />
 * ```
 *
 * Like the element, a stalled region without `stalled` content shows its fallback for `loading`, and a failed one
 * without `failed` content its fallback for `failed`.
 */
export function RunicHostedRegion({
  name, sizing, label, fallback, stalled, failed, onStateChange, onTitleChange, className, ...attributes
}: RunicHostedRegionProps): ReactNode {
  defineHostedRegion();
  const element = useRef<RunicHostedRegionElement | null>(null);
  const [region, setRegion] = useState<Omit<HostedRegionStateChangeDetail, "previous">>({ state: "waiting", stalled: false, reason: null });
  const retry = useCallback(() => element.current?.retry() ?? false, []);
  const handlers = useRef({ onStateChange, onTitleChange });
  handlers.current = { onStateChange, onTitleChange };
  useEffect(() => {
    const host = element.current;
    if (!host) return undefined;
    setRegion({ state: host.state, stalled: host.stalled, reason: host.failureReason });
    const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
      const { state, previous, stalled, reason } = event.detail;
      setRegion({ state, stalled, reason });
      handlers.current.onStateChange?.(state, previous, event.detail);
    };
    const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => handlers.current.onTitleChange?.(event.detail.title);
    host.addEventListener("runicregionstatechange", stateChanged);
    host.addEventListener("runicregiontitlechange", titleChanged);
    return () => {
      host.removeEventListener("runicregionstatechange", stateChanged);
      host.removeEventListener("runicregiontitlechange", titleChanged);
    };
  }, []);
  const status: HostedRegionStatus = { stalled: region.stalled, failureReason: region.reason, retry };
  // Each content goes to the element's slot of that name, and the element picks the one that shows: stalled, then
  // loading, then fallback; failed, then fallback. Content that is not given has no slot child, so the next one shows.
  const slotted = (slot: string, content: ReactNode) =>
    content === undefined || content === null ? null : createElement("div", { slot, style: { display: "contents" } }, content);
  return createElement(hostedRegionTag, {
    // React 18 sets a custom element's className as an attribute of that name; `class` is the attribute in 18 and 19.
    ...attributes, ...(className === undefined ? {} : { class: className }), ref: element, name, ...(sizing === undefined ? {} : { sizing }), ...(label === undefined ? {} : { label }),
  },
  slotted("fallback", typeof fallback === "function" ? fallback(region.state, status) : fallback),
  slotted("stalled", stalled),
  slotted("failed", typeof failed === "function" ? failed(status) : failed));
}
