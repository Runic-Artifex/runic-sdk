import type { MvvmProjectionSnapshot } from "@webuitoolkit/mvvm";

export * from "./todo-contract.g";

export type TodoDemo = "simple" | "advanced";

export function demoFromDocument(): TodoDemo {
  return document.body.dataset.demo === "advanced" ? "advanced" : "simple";
}

export function commandEnabled(
  snapshot: MvvmProjectionSnapshot,
  member: number,
): boolean {
  const command = snapshot.commands.get(member);
  return snapshot.synchronized &&
    command?.canExecute === true &&
    !command.isExecuting;
}
