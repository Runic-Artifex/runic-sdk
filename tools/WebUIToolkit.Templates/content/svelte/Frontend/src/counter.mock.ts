import {
  createMvvmMockChannelFactory,
  type MvvmMockFixture,
} from "@webuitoolkit/mvvm";

import { CounterContract } from "./counter-contract.g";

/** Development-only Counter host. Production entrypoints never import this module. */
export const createCounterMockChannel = createMvvmMockChannelFactory(
  createCounterMockFixture(),
);

function createCounterMockFixture(): MvvmMockFixture {
  let count = 0;
  let step = 1;
  const history = [0];
  return {
    contract: CounterContract.contractName,
    initial: [
      { type: "property", member: 1, value: count },
      { type: "property", member: 2, value: step },
      { type: "validation", member: 2, errors: [] },
      { type: "collection", member: 3, items: history },
      { type: "property", member: 4, value: summary() },
      { type: "command", member: 10, canExecute: true, isExecuting: false },
    ],
    setProperty(request) {
      const value = request.payload.value;
      const valid = typeof value === "number" &&
        Number.isInteger(value) &&
        value >= 1 &&
        value <= 10;
      if (valid) step = value;
      return {
        changes: [
          ...(valid
            ? [{ type: "property" as const, member: 2, value: step }]
            : []),
          {
            type: "validation",
            member: 2,
            errors: valid ? [] : ["Step must be a whole number from 1 through 10."],
          },
        ],
      };
    },
    execute() {
      count += step;
      history.push(count);
      return {
        changes: [
          { type: "property", member: 1, value: count },
          {
            type: "collection",
            member: 3,
            operation: "insert",
            index: history.length - 1,
            items: [count],
          },
          { type: "property", member: 4, value: summary() },
        ],
      };
    },
  };

  function summary(): string {
    return `${count} after ${history.length - 1} increment(s) · MOCK`;
  }
}
