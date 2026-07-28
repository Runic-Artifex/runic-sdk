import { createRoot } from "react-dom/client";
import {
  ReactMvvmProvider,
  startReactMvvmApplication,
} from "@webuitoolkit/mvvm-react";

import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { exposeTodoReconnect, reportStartupFailure } from "../../shared/runtime";
import { AdvancedTodo } from "./advanced/AdvancedTodo";
import { SimpleTodo } from "./simple/SimpleTodo";

const demo = demoFromDocument();

try {
  const application = demo === "simple"
    ? await startReactMvvmApplication({ contract: SimpleTodoContract })
    : await startReactMvvmApplication({ contract: AdvancedTodoContract });
  const root = createRoot(document.querySelector("#app")!);
  root.render(
    <ReactMvvmProvider store={application.store}>
      {application.contract instanceof SimpleTodoContract
        ? <SimpleTodo todo={application.contract} />
        : <AdvancedTodo todo={application.contract} />}
    </ReactMvvmProvider>,
  );
  application.addCleanup(() => root.unmount());
  exposeTodoReconnect(application);
} catch (error) {
  reportStartupFailure(error);
}
