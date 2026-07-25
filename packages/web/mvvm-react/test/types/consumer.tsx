import type { MvvmProjection } from "@webuitoolkit/mvvm";
import {
  ReactMvvmProvider,
  createReactMvvmStore,
  useMvvmCollection,
  useMvvmCommand,
  useMvvmProperty,
  useMvvmSnapshot,
  useMvvmValidation,
} from "@webuitoolkit/mvvm-react";

declare const projection: MvvmProjection;
const store = createReactMvvmStore(projection);

function Consumer() {
  const snapshot = useMvvmSnapshot();
  const property = useMvvmProperty(1);
  const collection = useMvvmCollection(2);
  const command = useMvvmCommand(3);
  const validation = useMvvmValidation(4);
  return (
    <output>
      {snapshot.phase}:{String(property)}:{collection?.length}:{String(command?.canExecute)}:
      {validation?.length}
    </output>
  );
}

export const fixture = (
  <ReactMvvmProvider store={store}>
    <Consumer />
  </ReactMvvmProvider>
);
