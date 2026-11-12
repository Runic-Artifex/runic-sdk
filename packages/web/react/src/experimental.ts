// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { createElement, useEffect, useRef, useState, type HTMLAttributes, type ReactNode } from "react";
import {
  defineHostedRegion,
  hostedRegionTag,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
  type RunicHostedRegionElement,
} from "@runic-artifex/views/experimental";

export type { HostedRegionSizing, HostedRegionState, RunicHostedRegionElement } from "@runic-artifex/views/experimental";

/** Props of {@link RunicHostedRegion}. Other HTML attributes, such as `className`, `style` and `id`, go to the element. */
export interface RunicHostedRegionProps extends Omit<HTMLAttributes<HTMLElement>, "children" | "title"> {
  /** The region's name, which the host application's policy embeds a guest's Window in. */
  readonly name: string;
  /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
  readonly sizing?: HostedRegionSizing;
  /** The region's accessible name; without it, the guest page's title. */
  readonly label?: string;
  /** What the region shows while no guest page shows in it: content, or a function of the region's state. */
  readonly fallback?: ReactNode | ((state: HostedRegionState) => ReactNode);
  readonly onStateChange?: (state: HostedRegionState, previous: HostedRegionState) => void;
  /** The guest page's title as the host passed it on, or `null`. */
  readonly onTitleChange?: (title: string | null) => void;
}

/**
 * A region where the host application shows a hosted guest's Window (`<runic-hosted-region>`), in a frame the page never
 * gets a reference to:
 *
 * ```tsx
 * <RunicHostedRegion name="check" style={{ height: 420 }}
 *   fallback={state => state === "ended" ? <RunAgain /> : <p>Starting the check…</p>} />
 * ```
 */
export function RunicHostedRegion({ name, sizing, label, fallback, onStateChange, onTitleChange, className, ...attributes }: RunicHostedRegionProps): ReactNode {
  defineHostedRegion();
  const element = useRef<RunicHostedRegionElement | null>(null);
  const [state, setState] = useState<HostedRegionState>("waiting");
  const handlers = useRef({ onStateChange, onTitleChange });
  handlers.current = { onStateChange, onTitleChange };
  useEffect(() => {
    const region = element.current;
    if (!region) return undefined;
    setState(region.state);
    const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
      setState(event.detail.state);
      handlers.current.onStateChange?.(event.detail.state, event.detail.previous);
    };
    const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => handlers.current.onTitleChange?.(event.detail.title);
    region.addEventListener("runicregionstatechange", stateChanged);
    region.addEventListener("runicregiontitlechange", titleChanged);
    return () => {
      region.removeEventListener("runicregionstatechange", stateChanged);
      region.removeEventListener("runicregiontitlechange", titleChanged);
    };
  }, []);
  const content = typeof fallback === "function" ? fallback(state) : fallback;
  return createElement(hostedRegionTag, {
    // React 18 sets a custom element's className as an attribute of that name; `class` is the attribute in 18 and 19.
    ...attributes, ...(className === undefined ? {} : { class: className }), ref: element, name, ...(sizing === undefined ? {} : { sizing }), ...(label === undefined ? {} : { label }),
  }, content === undefined || content === null ? null : createElement("div", { slot: "fallback", style: { display: "contents" } }, content));
}
