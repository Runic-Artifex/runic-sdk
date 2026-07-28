import { createTodoMockChannel, markTodoMockMode } from "../../shared/todo.mock";
import { reportStartupFailure } from "../../shared/runtime";
import { bootstrapTodoApplication } from "./application";

markTodoMockMode();
try {
  await bootstrapTodoApplication(createTodoMockChannel);
} catch (error) {
  reportStartupFailure(error);
}
