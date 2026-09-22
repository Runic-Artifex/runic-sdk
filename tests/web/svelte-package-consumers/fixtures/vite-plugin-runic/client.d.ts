export interface RunicRuntimeState {
  readonly connection?: Readonly<{ state?: string; transport?: string }>;
}

export type RunicDiagnosticDetailValue = string | number | boolean | null;
export type RunicDiagnosticDetail = Readonly<Record<string, RunicDiagnosticDetailValue>>;
export declare function reportRunicState(state: RunicRuntimeState): void;
export declare function createRunicDiagnosticReporter(source: string): {
  readonly report: (entry: Readonly<{
    kind: string;
    label: string;
    detail?: RunicDiagnosticDetail;
  }>) => void;
};
export declare function preserveRunicHmrResource<T>(key: string, create: () => T): T;
export declare function disposeRunicHmrResource(
  key: string,
  dispose?: (resource: unknown) => void | Promise<void>,
): Promise<void>;
