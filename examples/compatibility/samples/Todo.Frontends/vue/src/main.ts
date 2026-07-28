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
const mock = import.meta.env.MODE === "mock"
  ? await import("../../shared/todo.mock")
  : undefined;
mock?.markTodoMockMode();
const ownerOptions = mock === undefined
  ? {}
  : { channelFactory: () => mock.createTodoMockChannel(demo) };

try {
  const application = demo === "simple"
    ? await startVueMvvmApplication({
        contract: SimpleTodoContract,
        ...ownerOptions,
      })
    : await startVueMvvmApplication({
        contract: AdvancedTodoContract,
        ...ownerOptions,
      });
  const todo = application.contract;
  const root = demo === "simple" ? SimpleTodo : AdvancedTodo;
  const app = createApp(root, { adapter: application.adapter, todo });
  app.use(createVueMvvmApplicationPlugin(application));
  app.mount("#app");
  exposeTodoReconnect(application);
} catch (error) {
  reportStartupFailure(error);
}
