export * from "./todo-contract.g";

export type TodoDemo = "simple" | "advanced";

export function demoFromDocument(): TodoDemo {
  return document.body.dataset.demo === "advanced" ? "advanced" : "simple";
}
