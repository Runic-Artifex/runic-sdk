import { mount, unmount } from "svelte";
import { startSvelteMvvmApplication } from "@webuitoolkit/mvvm-svelte";

import App from "./TodoApp.svelte";
import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { exposeTodoReconnect, reportStartupFailure } from "../../shared/runtime";

const demo = demoFromDocument();

try {
  const application = demo === "simple"
    ? await startSvelteMvvmApplication({ contract: SimpleTodoContract })
    : await startSvelteMvvmApplication({ contract: AdvancedTodoContract });
  const target = document.querySelector<HTMLElement>("#app")!;
  target.replaceChildren();
  const app = mount(App, {
    target,
    props: { demo, model: application.store, todo: application.contract },
  });
  target.querySelector(".alert-secondary")?.remove();
  application.addCleanup(() => unmount(app));
  exposeTodoReconnect(application);
} catch (error) {
  reportStartupFailure(error);
}
