import { createApp } from "vue";
import { createVueMvvmAdapter } from "@webuitoolkit/mvvm-vue";

import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { connectTodo, reportStartupFailure } from "../../shared/runtime";
import AdvancedTodo from "./AdvancedTodo.vue";
import SimpleTodo from "./SimpleTodo.vue";

const demo = demoFromDocument();

try {
  const connection = await connectTodo(demo);
  const adapter = createVueMvvmAdapter(connection.projection);
  const todo = demo === "simple"
    ? new SimpleTodoContract(connection.projection)
    : new AdvancedTodoContract(connection.projection);
  const root = demo === "simple" ? SimpleTodo : AdvancedTodo;
  const app = createApp(root, { adapter, todo });
  app.mount("#app");

  globalThis.addEventListener("pagehide", () => {
    app.unmount();
    adapter.dispose();
    void connection.dispose();
  }, { once: true });
} catch (error) {
  reportStartupFailure(error);
}
