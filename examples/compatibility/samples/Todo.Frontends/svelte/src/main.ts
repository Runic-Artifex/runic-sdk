import { mount, unmount } from "svelte";
import { createSvelteMvvmStore } from "@webuitoolkit/mvvm-svelte";

import App from "./TodoApp.svelte";
import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { connectTodo, reportStartupFailure } from "../../shared/runtime";

const demo = demoFromDocument();

try {
  const connection = await connectTodo(demo);
  const model = createSvelteMvvmStore(connection.projection);
  const todo = demo === "simple"
    ? new SimpleTodoContract(connection.projection)
    : new AdvancedTodoContract(connection.projection);
  const target = document.querySelector<HTMLElement>("#app")!;
  target.replaceChildren();
  const app = mount(App, {
    target,
    props: { demo, model, todo },
  });
  target.querySelector(".alert-secondary")?.remove();
  globalThis.addEventListener("pagehide", () => {
    void unmount(app);
    model.dispose();
    void connection.dispose();
  }, { once: true });
} catch (error) {
  reportStartupFailure(error);
}
