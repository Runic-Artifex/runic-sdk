import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmProperty,
  MvvmReadonlyProperty,
  type MvvmProjection,
} from "@webuitoolkit/mvvm";
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
const typedAmount = new MvvmProperty<number>(projection, 1);
const typedLabel = new MvvmReadonlyProperty<string>(projection, 2);
const typedItems = new MvvmCollection<{ readonly id: string }>(projection, 3);
const typedSubmit = new MvvmCommandWithArgument<number, string>(projection, 4);

function Consumer() {
  const snapshot = useMvvmSnapshot();
  const property = useMvvmProperty(1);
  const collection = useMvvmCollection(2);
  const command = useMvvmCommand(3);
  const validation = useMvvmValidation(4);
  const amount: number | undefined = useMvvmProperty(typedAmount);
  const label: string | undefined = useMvvmProperty(typedLabel);
  const items: readonly { readonly id: string }[] = useMvvmCollection(typedItems);
  const typedCommand = useMvvmCommand(typedSubmit);
  const typedValidation = useMvvmValidation(typedAmount);
  void typedSubmit.execute(42);
  // @ts-expect-error generated command arguments stay strongly typed.
  void typedSubmit.execute("42");
  return (
    <output>
      {snapshot.phase}:{String(property)}:{collection?.length}:{String(command?.canExecute)}:
      {validation?.length}:{amount}:{label}:{items.length}:{String(typedCommand?.canExecute)}:
      {typedValidation?.length}
    </output>
  );
}

export const fixture = (
  <ReactMvvmProvider store={store}>
    <Consumer />
  </ReactMvvmProvider>
);
