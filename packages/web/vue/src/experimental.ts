// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { computed, defineComponent, h, ref, type PropType, type SlotsType, type VNodeChild } from "vue";
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

/** How a region's loading goes: the props of its `failed` slot, and of its `fallback` slot with `state`. */
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

/**
 * A region where the host application shows a hosted guest's Window (`<runic-hosted-region>`), in a frame the page never
 * gets a reference to. The `fallback` slot, scoped with the region's `state` and status, shows while no guest page shows
 * in it; the `stalled` slot shows in its place while the region is stalled, and the `failed` slot, scoped with the
 * status, while it is failed:
 *
 * ```vue
 * <RunicHostedRegion name="check" style="height: 420px" @state-change="onState">
 *   <template #fallback="{ state }"><RunAgain v-if="state === 'ended'" /><p v-else>Starting the check…</p></template>
 *   <template #stalled><p>The check is taking longer than usual…</p></template>
 *   <template #failed="{ failureReason, retry }">
 *     <p>The check could not start ({{ failureReason }}). <button @click="retry()">Try again</button></p>
 *   </template>
 * </RunicHostedRegion>
 * ```
 *
 * Like the element, a stalled region without a `stalled` slot shows its `fallback` slot for `loading`, and a failed one
 * without a `failed` slot its `fallback` slot for `failed`. A template ref reads `state`, `stalled` and `failureReason`,
 * and calls `retry()`.
 */
export const RunicHostedRegion = defineComponent({
  name: "RunicHostedRegion",
  props: {
    /** The region's name, which the host application's policy embeds a guest's Window in. */
    name: { type: String, required: true },
    /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
    sizing: { type: String as PropType<HostedRegionSizing>, default: undefined },
    /** The region's accessible name; without it, the guest page's title. */
    label: { type: String, default: undefined },
  },
  emits: {
    /** The region's state changed, or only whether it is stalled or the reason it failed. */
    stateChange: (_state: HostedRegionState, _previous: HostedRegionState, _detail: HostedRegionStateChangeDetail) => true,
    /** The guest page's title as the host passed it on, or `null`. */
    titleChange: (_title: string | null) => true,
  },
  slots: Object as SlotsType<{
    fallback?: (props: HostedRegionStatus & { readonly state: HostedRegionState }) => VNodeChild;
    stalled?: () => VNodeChild;
    failed?: (props: HostedRegionStatus) => VNodeChild;
  }>,
  setup(props, { emit, slots, expose }) {
    defineHostedRegion();
    const element = ref<RunicHostedRegionElement>();
    const state = ref<HostedRegionState>("waiting");
    const stalled = ref(false);
    const failureReason = ref<string | null>(null);
    const retry = () => element.value?.retry() ?? false;
    expose({ state: computed(() => state.value), stalled: computed(() => stalled.value), failureReason: computed(() => failureReason.value), retry });
    const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
      state.value = event.detail.state;
      stalled.value = event.detail.stalled;
      failureReason.value = event.detail.reason;
      emit("stateChange", event.detail.state, event.detail.previous, event.detail);
    };
    const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => emit("titleChange", event.detail.title);
    // Each slot goes to the element's slot of that name, and the element picks the one that shows: stalled, then
    // loading, then fallback; failed, then fallback. A slot that is not given has no slot child, so the next one shows.
    const slotted = (slot: string, content: VNodeChild) =>
      content === undefined ? [] : [h("div", { slot, style: { display: "contents" } }, [content])];
    return () => {
      const status: HostedRegionStatus = { stalled: stalled.value, failureReason: failureReason.value, retry };
      return h(hostedRegionTag, {
        ref: element,
        name: props.name,
        ...(props.sizing === undefined ? {} : { sizing: props.sizing }),
        ...(props.label === undefined ? {} : { label: props.label }),
        onRunicregionstatechange: stateChanged,
        onRunicregiontitlechange: titleChanged,
      }, [
        ...slotted("fallback", slots.fallback?.({ state: state.value, ...status })),
        ...slotted("stalled", slots.stalled?.()),
        ...slotted("failed", slots.failed?.(status)),
      ]);
    };
  },
});
