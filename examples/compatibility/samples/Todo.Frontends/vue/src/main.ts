import { createApp } from "vue";
import {
  createVueMvvmApplicationPlugin,
  startVueMvvmApplication,
} from "@webuitoolkit/mvvm-vue";

import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { exposeTodoReconnect, reportStartupFailure } from "../../shared/runtime";
import AdvancedTodo from "./AdvancedTodo.vue";
import SimpleTodo from "./SimpleTodo.vue";

const demo = demoFromDocument();

try {
  const application = demo === "simple"
    ? await startVueMvvmApplication({ contract: SimpleTodoContract })
    : await startVueMvvmApplication({ contract: AdvancedTodoContract });
  const todo = application.contract;
  const root = demo === "simple" ? SimpleTodo : AdvancedTodo;
  const app = createApp(root, { adapter: application.adapter, todo });
  app.use(createVueMvvmApplicationPlugin(application));
  app.mount("#app");
  exposeTodoReconnect(application);
} catch (error) {
  reportStartupFailure(error);
}
