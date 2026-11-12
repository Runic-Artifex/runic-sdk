// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { defineComponent, h, ref, type PropType, type SlotsType, type VNodeChild } from "vue";
import {
  defineHostedRegion,
  hostedRegionTag,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
} from "@runic-artifex/views/experimental";

export type { HostedRegionSizing, HostedRegionState, RunicHostedRegionElement } from "@runic-artifex/views/experimental";

/**
 * A region where the host application shows a hosted guest's Window (`<runic-hosted-region>`), in a frame the page never
 * gets a reference to. The `fallback` slot, scoped with the region's `state`, shows while no guest page shows in it:
 *
 * ```vue
 * <RunicHostedRegion name="check" style="height: 420px" @state-change="onState">
 *   <template #fallback="{ state }"><RunAgain v-if="state === 'ended'" /><p v-else>Starting the check…</p></template>
 * </RunicHostedRegion>
 * ```
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
    stateChange: (_state: HostedRegionState, _previous: HostedRegionState) => true,
    /** The guest page's title as the host passed it on, or `null`. */
    titleChange: (_title: string | null) => true,
  },
  slots: Object as SlotsType<{ fallback?: (props: { readonly state: HostedRegionState }) => VNodeChild }>,
  setup(props, { emit, slots }) {
    defineHostedRegion();
    const state = ref<HostedRegionState>("waiting");
    const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
      state.value = event.detail.state;
      emit("stateChange", event.detail.state, event.detail.previous);
    };
    const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => emit("titleChange", event.detail.title);
    return () => {
      const fallback = slots.fallback?.({ state: state.value });
      return h(hostedRegionTag, {
        name: props.name,
        ...(props.sizing === undefined ? {} : { sizing: props.sizing }),
        ...(props.label === undefined ? {} : { label: props.label }),
        onRunicregionstatechange: stateChanged,
        onRunicregiontitlechange: titleChanged,
      }, fallback === undefined ? [] : [h("div", { slot: "fallback", style: { display: "contents" } }, [fallback])]);
    };
  },
});
