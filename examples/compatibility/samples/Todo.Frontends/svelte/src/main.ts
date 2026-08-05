import { mount, unmount } from "svelte";
import { startSvelteMvvmApplication } from "@runic-artifex/mvvm-svelte";

import App from "./TodoApp.svelte";
import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { exposeTodoReconnect, reportStartupFailure } from "../../shared/runtime";

const demo = demoFromDocument();
const mock = import.meta.env.MODE === "mock"
  ? await import("../../shared/todo.mock")
  : undefined;
mock?.markTodoMockMode();
const ownerOptions = mock === undefined
  ? {}
  : { channelFactory: () => mock.createTodoMockChannel(demo) };

try {
  const application = demo === "simple"
    ? await startSvelteMvvmApplication({
        contract: SimpleTodoContract,
        ...ownerOptions,
      })
    : await startSvelteMvvmApplication({
        contract: AdvancedTodoContract,
        ...ownerOptions,
      });
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
