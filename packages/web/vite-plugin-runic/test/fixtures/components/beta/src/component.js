// The beta guest's component: Vue, mounted in the element's shadow root within the element's instance.
import { createApp, defineComponent, h, ref } from "vue";
import { provideRunicInstance, useView } from "@runic-artifex/vue";
import { defaultInstance } from "@runic-artifex/views";
import { counter } from "../../counter.js";
import "./style.css";

const Counter = defineComponent({
  setup() {
    const view = useView(counter);
    const owner = ref("");
    return () => h("div", null, [
      h("span", { class: "label" }, `beta ${view.state.value?.count ?? "…"}`),
      h("button", { onClick: () => { owner.value = String(defaultInstance().bridge?.name); } }, "owner"),
      h("output", null, owner.value),
    ]);
  },
});

export default function mount({ container, instance }) {
  const app = createApp(defineComponent({ setup() { provideRunicInstance(instance); return () => h(Counter); } }));
  app.mount(container);
  return () => app.unmount();
}
