export interface RunicRuntimeState {
  readonly connection?: Readonly<{ state?: string; transport?: string }>;
}

export interface RunicTraceEntry {
  readonly kind: string;
  readonly label: string;
}

export interface RunicDevtoolsObserver {
  readonly state: (state: RunicRuntimeState) => void;
  readonly trace: (entry: RunicTraceEntry) => void;
}

export declare function createRunicDevtoolsObserver(): RunicDevtoolsObserver;
export declare function preserveRunicHmrResource<T>(key: string, create: () => T): T;
export declare function disposeRunicHmrResource(
  key: string,
  dispose?: (resource: unknown) => void | Promise<void>,
): Promise<void>;
